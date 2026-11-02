using System.Net;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Services;

public enum EnrichmentFailureKind { None, Temporary, InvalidResponse, EmptyPreserved, NotFound, RateLimited, Persistence, Cancelled }
public enum CooldownOrigin { None, Memory, Persisted }
public enum EnrichmentBlock { None, Recent, Cooldown, InconsistentTimestamp }
// Prepared once using a post-client reference. Publication is a separate, explicit commit acknowledgement.
public sealed class EnrichmentCooldownObservation
{
    public EnrichmentAttemptId Identity { get; }
    public DateTime PersistedAttemptAtUtc { get; }
    public DateTime ReferenceAtUtc { get; }
    public DateTime UntilUtc { get; }
    public EnrichmentFailureKind Kind { get; }
    internal EnrichmentCooldownObservation(EnrichmentAttemptId identity, DateTime at, DateTime reference, DateTime until, EnrichmentFailureKind kind)
    { Identity = identity; PersistedAttemptAtUtc = at; ReferenceAtUtc = reference; UntilUtc = until; Kind = kind; }
}
public sealed record EnrichmentCooldownDecision(bool CanAttempt, bool CooldownActive, DateTime? NextAllowedAtUtc,
    EnrichmentBlock Block, CooldownOrigin Origin, bool OriginalReasonUnknown, EnrichmentFreshness Freshness,
    EnrichmentFailureKind Reason);

// Explicitly fed by the future coordinator, never called automatically by persistence.
// Only scalar diagnostic data is retained; no payload, keyword, exception, credential or scoped service.
public sealed class GameEnrichmentCooldown(TimeProvider clock, GameEnrichmentPolicy freshness)
{
    public const int Capacity = 1024;
    private sealed record Attempt(DateTime AtUtc, DateTime UntilUtc, EnrichmentFailureKind Kind,
        long Sequence = 0, DateTime ReferenceAtUtc = default);
    private readonly Dictionary<(int Game, CatalogMetadataSection Section), Attempt> attempts = [];
    private readonly object gate = new(); // Protects this small cache, not enrichment execution or database writes.

    public int Count { get { lock (gate) { Prune(); return attempts.Count; } } }

    public EnrichmentCooldownObservation? PrepareResult(EnrichmentAttemptId identity, DateTime persistedAttemptAtUtc,
        DateTime clientCompletedAtUtc, TaxonomyEnrichmentResult result)
    {
        ValidateIdentity(identity);
        if (result.Section != identity.Section) throw new ArgumentException("A seção não corresponde à tentativa.", nameof(result));
        if (result.ExternalStatus == IgdbMetadataStatus.NotRequested) return null;
        var at = PastUtc(persistedAttemptAtUtc);
        var reference = PastUtc(clientCompletedAtUtc);
        var kind = Classify(result);
        return new(identity, at, reference, Add(reference, Interval(kind, result.RetryAfter)), kind);
    }

    // Caller must invoke only after a successful commit. Preparation alone has no cache side effects.
    public void RecordConfirmed(EnrichmentCooldownObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var id = observation.Identity;
        ValidateIdentity(id);
        Store(id.GameId, id.Section, new(observation.PersistedAttemptAtUtc, observation.UntilUtc,
            observation.Kind, id.Sequence, observation.ReferenceAtUtc));
    }

    // Caller invokes after rollback; there is no success acknowledgement or compensating database write.
    public void RecordPersistenceFailure(EnrichmentAttemptId identity, DateTime failedAtUtc)
    {
        ValidateIdentity(identity); var at = PastUtc(failedAtUtc);
        Store(identity.GameId, identity.Section, new(at, Add(at, TimeSpan.FromMinutes(1)), EnrichmentFailureKind.Persistence, identity.Sequence, at));
    }

    private static void ValidateIdentity(EnrichmentAttemptId identity)
    {
        Validate(identity.GameId, identity.Section);
        if (identity.Sequence <= 0) throw new ArgumentException("Identidade de tentativa inválida.", nameof(identity));
    }

    private static EnrichmentFailureKind Classify(TaxonomyEnrichmentResult result) => result.Outcome switch
    {
        TaxonomyEnrichmentOutcome.Applied => EnrichmentFailureKind.None,
        TaxonomyEnrichmentOutcome.EmptyPreserved => EnrichmentFailureKind.EmptyPreserved,
        TaxonomyEnrichmentOutcome.GameNotFound => EnrichmentFailureKind.NotFound,
        TaxonomyEnrichmentOutcome.InvalidResponse => EnrichmentFailureKind.InvalidResponse,
        _ when result.StatusCode == HttpStatusCode.TooManyRequests || result.Failure == IgdbQueryFailure.RateLimited => EnrichmentFailureKind.RateLimited,
        _ => EnrichmentFailureKind.Temporary
    };

    private static TimeSpan Interval(EnrichmentFailureKind kind, TimeSpan? retryAfter) => kind switch
    {
        EnrichmentFailureKind.None => TimeSpan.Zero,
        EnrichmentFailureKind.InvalidResponse => TimeSpan.FromHours(1),
        EnrichmentFailureKind.EmptyPreserved or EnrichmentFailureKind.NotFound => TimeSpan.FromHours(24),
        EnrichmentFailureKind.Persistence => TimeSpan.FromMinutes(1),
        EnrichmentFailureKind.RateLimited when retryAfter > TimeSpan.FromMinutes(15) => retryAfter.Value,
        _ => TimeSpan.FromMinutes(15)
    };

    private DateTime PastUtc(DateTime value)
    {
        var at = Utc(value);
        if (at > clock.GetUtcNow().UtcDateTime) throw new ArgumentException("O instante não pode estar no futuro.");
        return at;
    }

    public void RecordResult(int gameId, DateTime attemptAtUtc, TaxonomyEnrichmentResult result)
    {
        if (result.ExternalStatus == IgdbMetadataStatus.NotRequested) return;
        var reason = Classify(result);
        Record(gameId, result.Section, attemptAtUtc, reason, result.RetryAfter);
    }

    // Call None only after confirmed success; Persistence only after rollback. Cancellation is neutral.
    public void Record(int gameId, CatalogMetadataSection section, DateTime attemptAtUtc,
        EnrichmentFailureKind kind, TimeSpan? retryAfter = null)
    {
        Validate(gameId, section);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == EnrichmentFailureKind.Cancelled) return;
        var at = PastUtc(attemptAtUtc);
        Store(gameId, section, new(at, Add(at, Interval(kind, retryAfter)), kind));
    }

    private void Store(int gameId, CatalogMetadataSection section, Attempt item)
    {
        lock (gate)
        {
            Prune();
            var key = (gameId, section);
            if (attempts.TryGetValue(key, out var previous))
            {
                if (previous.Sequence > 0 && item.Sequence == 0) return; // Legacy calls cannot supersede an identified attempt.
                var order = item.Sequence > 0 && previous.Sequence > 0 ? item.Sequence.CompareTo(previous.Sequence) : item.AtUtc.CompareTo(previous.AtUtc);
                if (order < 0) return;
                if (order == 0)
                {
                    if (previous.Sequence > 0 && (previous.AtUtc != item.AtUtc || previous.ReferenceAtUtc != item.ReferenceAtUtc))
                        throw new ArgumentException("A mesma tentativa deve manter seus instantes de referência.");
                    if (previous.Kind == EnrichmentFailureKind.None) return;
                }
                if (item.Kind != EnrichmentFailureKind.None && previous.UntilUtc > item.UntilUtc)
                    item = item with { UntilUtc = previous.UntilUtc };
            }
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
