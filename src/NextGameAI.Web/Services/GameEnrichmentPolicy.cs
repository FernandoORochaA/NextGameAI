using NextGameAI.Web.Models;

namespace NextGameAI.Web.Services;

public enum EnrichmentFreshness { NeverQueried, NoSuccessfulUpdate, Recent, Expired, InconsistentTimestamp }

public sealed record EnrichmentEligibility(CatalogMetadataSection Section, EnrichmentFreshness Freshness,
    bool Eligible, DateTime? ExpiresAtUtc);

// Evaluation only: no HTTP, database access, cooldown, scheduling or modification of states.
public sealed class GameEnrichmentPolicy(TimeProvider clock)
{
    public static TimeSpan Lifetime { get; } = TimeSpan.FromDays(30);

    public EnrichmentEligibility Evaluate(CatalogMetadataSection section, GameEnrichmentState? state)
    {
        if (!Enum.IsDefined(section)) throw new ArgumentOutOfRangeException(nameof(section));
        if (state is not null && state.Section != section) throw new ArgumentException("O estado deve corresponder à seção avaliada.", nameof(state));
        var now = clock.GetUtcNow().UtcDateTime;
        if (state is null) return new(section, EnrichmentFreshness.NeverQueried, true, null);
        // SQLite commonly returns Unspecified: stored ticks follow the UTC convention, not local time.
        var attempt = DateTime.SpecifyKind(state.LastAttemptAtUtc, DateTimeKind.Utc);
        var success = state.LastSuccessAtUtc.HasValue ? DateTime.SpecifyKind(state.LastSuccessAtUtc.Value, DateTimeKind.Utc) : (DateTime?)null;
        if (state.LastAttemptAtUtc.Kind == DateTimeKind.Local || state.LastSuccessAtUtc?.Kind == DateTimeKind.Local ||
            attempt > now || success > now || success > attempt || !Enum.IsDefined(state.LastResult) ||
            state.LastResult == CatalogUpdateResult.Success && (!success.HasValue || success != attempt))
            return new(section, EnrichmentFreshness.InconsistentTimestamp, false, null);
        if (!success.HasValue) return new(section, EnrichmentFreshness.NoSuccessfulUpdate, true, null);
        var expires = success.Value > DateTime.MaxValue - Lifetime ? DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc) : success.Value + Lifetime;
        var recent = now - success.Value < Lifetime;
        return new(section, recent ? EnrichmentFreshness.Recent : EnrichmentFreshness.Expired, !recent, expires);
    }
}
