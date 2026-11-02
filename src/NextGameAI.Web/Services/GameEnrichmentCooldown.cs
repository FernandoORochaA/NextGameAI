using System.Net;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Services;

public enum EnrichmentFailureKind { None, Temporary, InvalidResponse, EmptyPreserved, NotFound, RateLimited, Persistence, Cancelled }
public enum CooldownOrigin { None, Memory, Persisted }
public enum EnrichmentBlock { None, Recent, Cooldown, InconsistentTimestamp }
public sealed record EnrichmentCooldownDecision(bool CanAttempt, bool CooldownActive, DateTime? NextAllowedAtUtc,
    EnrichmentBlock Block, CooldownOrigin Origin, bool OriginalReasonUnknown, EnrichmentFreshness Freshness,
    EnrichmentFailureKind Reason);

// Explicitly fed by the future coordinator, never called automatically by persistence.
// Only scalar diagnostic data is retained; no payload, keyword, exception, credential or scoped service.
public sealed class GameEnrichmentCooldown(TimeProvider clock, GameEnrichmentPolicy freshness)
{
    public const int Capacity = 1024;
    private sealed record Attempt(DateTime AtUtc, DateTime UntilUtc, EnrichmentFailureKind Kind);
    private readonly Dictionary<(int Game, CatalogMetadataSection Section), Attempt> attempts = [];
    private readonly object gate = new(); // Protects this small cache, not enrichment execution or database writes.

    public int Count { get { lock (gate) { Prune(); return attempts.Count; } } }

    public void RecordResult(int gameId, DateTime attemptAtUtc, TaxonomyEnrichmentResult result)
    {
        if (result.ExternalStatus == IgdbMetadataStatus.NotRequested) return;
        var reason = result.Outcome switch
        {
            TaxonomyEnrichmentOutcome.Applied => EnrichmentFailureKind.None,
            TaxonomyEnrichmentOutcome.EmptyPreserved => EnrichmentFailureKind.EmptyPreserved,
            TaxonomyEnrichmentOutcome.GameNotFound => EnrichmentFailureKind.NotFound,
            TaxonomyEnrichmentOutcome.InvalidResponse => EnrichmentFailureKind.InvalidResponse,
            _ when result.StatusCode == HttpStatusCode.TooManyRequests || result.Failure == IgdbQueryFailure.RateLimited => EnrichmentFailureKind.RateLimited,
            _ => EnrichmentFailureKind.Temporary
        };
        Record(gameId, result.Section, attemptAtUtc, reason, result.RetryAfter);
    }

    // Call None only after confirmed success; Persistence only after rollback. Cancellation is neutral.
    public void Record(int gameId, CatalogMetadataSection section, DateTime attemptAtUtc,
        EnrichmentFailureKind kind, TimeSpan? retryAfter = null)
    {
        Validate(gameId, section);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == EnrichmentFailureKind.Cancelled) return;
        var at = Utc(attemptAtUtc);
        if (at > clock.GetUtcNow().UtcDateTime) throw new ArgumentException("A tentativa não pode estar no futuro.", nameof(attemptAtUtc));
        var interval = kind switch
        {
            EnrichmentFailureKind.None => TimeSpan.Zero,
            EnrichmentFailureKind.InvalidResponse => TimeSpan.FromHours(1),
            EnrichmentFailureKind.EmptyPreserved or EnrichmentFailureKind.NotFound => TimeSpan.FromHours(24),
            EnrichmentFailureKind.Persistence => TimeSpan.FromMinutes(1),
            EnrichmentFailureKind.RateLimited when retryAfter > TimeSpan.FromMinutes(15) => retryAfter.Value,
            _ => TimeSpan.FromMinutes(15)
        };
        var item = new Attempt(at, Add(at, interval), kind);
        lock (gate)
        {
            Prune();
            var key = (gameId, section);
            if (attempts.TryGetValue(key, out var previous) &&
                (previous.AtUtc > at || previous.AtUtc == at && (kind != EnrichmentFailureKind.None || previous.Kind == EnrichmentFailureKind.None))) return;
            if (!attempts.ContainsKey(key) && attempts.Count >= Capacity)
                attempts.Remove(attempts.MinBy(p => p.Value.AtUtc).Key);
            attempts[key] = item;
        }
    }

    public EnrichmentCooldownDecision Evaluate(int gameId, CatalogMetadataSection section, GameEnrichmentState? state)
    {
        Validate(gameId, section);
        if (state is not null && state.GameId != gameId) throw new ArgumentException("O estado deve pertencer ao jogo avaliado.", nameof(state));
        var valid = freshness.Evaluate(section, state);
        if (valid.Freshness == EnrichmentFreshness.InconsistentTimestamp)
            return new(false, false, null, EnrichmentBlock.InconsistentTimestamp, CooldownOrigin.None, false, valid.Freshness, EnrichmentFailureKind.None);
        Attempt? memory;
        lock (gate) { Prune(); attempts.TryGetValue((gameId, section), out memory); }
        if (memory?.AtUtc > clock.GetUtcNow().UtcDateTime)
            return new(false, false, null, EnrichmentBlock.InconsistentTimestamp, CooldownOrigin.Memory, false, valid.Freshness, memory.Kind);
        DateTime? until = null;
        var origin = CooldownOrigin.None;
        var unknown = false;
        var reason = EnrichmentFailureKind.None;
        // Detailed external outcomes must match the persisted attempt exactly. Persistence failure is
        // the exception: rollback intentionally left no persisted record of that newer attempt.
        if (memory is not null && (state is null ||
            memory.Kind == EnrichmentFailureKind.Persistence && memory.AtUtc > Utc(state.LastAttemptAtUtc) ||
            memory.AtUtc == Utc(state.LastAttemptAtUtc) && state.LastResult != CatalogUpdateResult.Success &&
            (state.LastResult == CatalogUpdateResult.NotFound ? memory.Kind == EnrichmentFailureKind.NotFound :
                memory.Kind is not (EnrichmentFailureKind.None or EnrichmentFailureKind.NotFound or EnrichmentFailureKind.Persistence))))
        { until = memory.UntilUtc; origin = CooldownOrigin.Memory; reason = memory.Kind; }
        else if (state is not null && state.LastResult != CatalogUpdateResult.Success)
        {
            unknown = state.LastResult == CatalogUpdateResult.TemporaryFailure;
            reason = unknown ? EnrichmentFailureKind.Temporary : EnrichmentFailureKind.NotFound;
            until = Add(Utc(state.LastAttemptAtUtc), unknown ? TimeSpan.FromMinutes(15) : TimeSpan.FromHours(24));
            origin = CooldownOrigin.Persisted;
        }
        var active = until > clock.GetUtcNow().UtcDateTime;
        var block = active ? EnrichmentBlock.Cooldown : !valid.Eligible ? EnrichmentBlock.Recent : EnrichmentBlock.None;
        var next = active && !valid.Eligible && valid.ExpiresAtUtc > until ? valid.ExpiresAtUtc : active ? until : !valid.Eligible ? valid.ExpiresAtUtc : null;
        return new(valid.Eligible && !active, active, next, block, origin, unknown, valid.Freshness, reason);
    }

    private void Prune()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        // Keep success watermarks for 24h to reject delayed failures; all entries remain bounded.
        foreach (var pair in attempts.Where(p => (p.Value.Kind == EnrichmentFailureKind.None ? Add(p.Value.AtUtc, TimeSpan.FromHours(24)) : p.Value.UntilUtc) <= now).ToArray())
            attempts.Remove(pair.Key);
    }
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Local
        ? throw new ArgumentException("O instante deve seguir a convenção UTC.") : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    private static DateTime Add(DateTime value, TimeSpan interval) => interval.Ticks > DateTime.MaxValue.Ticks - value.Ticks ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : value + interval;
    private static void Validate(int gameId, CatalogMetadataSection section)
    {
        if (gameId <= 0) throw new ArgumentOutOfRangeException(nameof(gameId));
        if (!Enum.IsDefined(section)) throw new ArgumentOutOfRangeException(nameof(section));
    }
}
