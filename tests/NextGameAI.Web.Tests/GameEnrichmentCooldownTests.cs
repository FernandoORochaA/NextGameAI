using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Tests;

[Trait("Host", "None")]
public sealed class GameEnrichmentCooldownTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        public override DateTimeOffset GetUtcNow() => new(Now);
    }
    private const CatalogMetadataSection Section = CatalogMetadataSection.Genres;
    private readonly Clock clock = new();
    private GameEnrichmentCooldown Create() => new(clock, new(clock));
    private GameEnrichmentState State(CatalogUpdateResult result = CatalogUpdateResult.TemporaryFailure) => new()
    {
        GameId = 1, Section = Section, LastAttemptAtUtc = clock.Now,
        LastSuccessAtUtc = clock.Now.AddDays(-31), LastResult = result
    };

    [Theory]
    [InlineData(EnrichmentFailureKind.Temporary, 15)]
    [InlineData(EnrichmentFailureKind.InvalidResponse, 60)]
    [InlineData(EnrichmentFailureKind.EmptyPreserved, 1440)]
    [InlineData(EnrichmentFailureKind.NotFound, 1440)]
    [InlineData(EnrichmentFailureKind.Persistence, 1)]
    [InlineData(EnrichmentFailureKind.RateLimited, 15)]
    public void IntervalsAndExactBoundaries(EnrichmentFailureKind kind, int minutes)
    {
        var policy = Create(); var start = clock.Now;
        policy.Record(1, Section, start, kind);
        var state = kind == EnrichmentFailureKind.Persistence ? null : State(kind == EnrichmentFailureKind.NotFound ? CatalogUpdateResult.NotFound : CatalogUpdateResult.TemporaryFailure);
        var initial = policy.Evaluate(1, Section, state);
        Assert.True(initial.CooldownActive); Assert.False(initial.CanAttempt);
        Assert.Equal(start.AddMinutes(minutes), initial.NextAllowedAtUtc);
        Assert.Equal(CooldownOrigin.Memory, initial.Origin); Assert.False(initial.OriginalReasonUnknown);
        clock.Now = start.AddMinutes(minutes).AddTicks(-1); Assert.False(policy.Evaluate(1, Section, state).CanAttempt);
        clock.Now = clock.Now.AddTicks(1); Assert.True(policy.Evaluate(1, Section, state).CanAttempt);
        clock.Now = clock.Now.AddTicks(1); Assert.True(policy.Evaluate(1, Section, state).CanAttempt);
    }

    [Theory]
    [InlineData(-10, 15)] [InlineData(0, 15)] [InlineData(3, 15)] [InlineData(90, 90)]
    public void RateLimitUsesMaximumWithoutNegativeIntervals(int retryMinutes, int expected)
    {
        var policy = Create(); policy.RecordResult(1, clock.Now, new(Section, TaxonomyEnrichmentOutcome.ExternalFailure,
            IgdbMetadataStatus.TransportFailure, IgdbQueryFailure.RateLimited, HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(retryMinutes)));
        var decision = policy.Evaluate(1, Section, State());
        Assert.Equal(clock.Now.AddMinutes(expected), decision.NextAllowedAtUtc);
        Assert.Equal(EnrichmentFailureKind.RateLimited, decision.Reason);
    }

    [Fact]
    public void HugeRetryAfterSaturatesSafely()
    {
        var policy = Create(); policy.Record(1, Section, clock.Now, EnrichmentFailureKind.RateLimited, TimeSpan.MaxValue);
        Assert.Equal(DateTime.MaxValue, policy.Evaluate(1, Section, State()).NextAllowedAtUtc);
    }

    [Theory]
    [InlineData(TaxonomyEnrichmentOutcome.Applied, IgdbMetadataStatus.Success, EnrichmentFailureKind.None)]
    [InlineData(TaxonomyEnrichmentOutcome.EmptyPreserved, IgdbMetadataStatus.Empty, EnrichmentFailureKind.EmptyPreserved)]
    [InlineData(TaxonomyEnrichmentOutcome.InvalidResponse, IgdbMetadataStatus.Success, EnrichmentFailureKind.InvalidResponse)]
    [InlineData(TaxonomyEnrichmentOutcome.GameNotFound, IgdbMetadataStatus.GameNotFound, EnrichmentFailureKind.NotFound)]
    [InlineData(TaxonomyEnrichmentOutcome.ExternalFailure, IgdbMetadataStatus.TransportFailure, EnrichmentFailureKind.Temporary)]
    public void TypedResultsMapWithoutInventingSuccess(TaxonomyEnrichmentOutcome outcome, IgdbMetadataStatus status, EnrichmentFailureKind expected)
    {
        var policy = Create();
        policy.RecordResult(1, clock.Now, new(Section, outcome, status, null));
        var result = policy.Evaluate(1, Section, null);
        Assert.Equal(expected, result.Reason);
        Assert.Equal(expected != EnrichmentFailureKind.None, result.CooldownActive);
    }

    [Fact]
    public void InfrastructureNotFoundIsNotCatalogNotFoundAndTimeoutIsTemporary()
    {
        var policy = Create();
        policy.RecordResult(1, clock.Now, new(Section, TaxonomyEnrichmentOutcome.ExternalFailure,
            IgdbMetadataStatus.TransportFailure, IgdbQueryFailure.HttpError, HttpStatusCode.NotFound));
        policy.RecordResult(2, clock.Now, new(Section, TaxonomyEnrichmentOutcome.ExternalFailure,
            IgdbMetadataStatus.TransportFailure, IgdbQueryFailure.Timeout));
        Assert.Equal(clock.Now.AddMinutes(15), policy.Evaluate(1, Section, null).NextAllowedAtUtc);
        Assert.Equal(clock.Now.AddMinutes(15), policy.Evaluate(2, Section, null).NextAllowedAtUtc);
        Assert.Equal(EnrichmentFailureKind.Temporary, policy.Evaluate(1, Section, null).Reason);
    }

    [Fact]
    public void GamesSectionsAndAttemptIdentityAreIndependent()
    {
        var policy = Create(); var state = State();
        policy.Record(1, Section, clock.Now, EnrichmentFailureKind.EmptyPreserved);
        Assert.True(policy.Evaluate(2, Section, null).CanAttempt);
        Assert.True(policy.Evaluate(1, CatalogMetadataSection.Multiplayer, null).CanAttempt);
        state.LastAttemptAtUtc = state.LastAttemptAtUtc.AddTicks(-1);
        var decision = policy.Evaluate(1, Section, state);
        Assert.Equal(CooldownOrigin.Persisted, decision.Origin); Assert.True(decision.OriginalReasonUnknown);
        Assert.Equal(state.LastAttemptAtUtc.AddMinutes(15), decision.NextAllowedAtUtc);
    }

    [Fact]
    public void LaterSuccessAndOlderFailureCannotResurrectCooldown()
    {
        var policy = Create(); var first = clock.Now;
        policy.Record(1, Section, first, EnrichmentFailureKind.EmptyPreserved);
        clock.Now = clock.Now.AddSeconds(1);
        policy.Record(1, Section, clock.Now, EnrichmentFailureKind.None);
        policy.Record(1, Section, first, EnrichmentFailureKind.EmptyPreserved);
        Assert.False(policy.Evaluate(1, Section, null).CooldownActive);
        var success = State(CatalogUpdateResult.Success); success.LastSuccessAtUtc = success.LastAttemptAtUtc;
        Assert.Equal(EnrichmentBlock.Recent, policy.Evaluate(1, Section, success).Block);
        // Same timestamp is also safe when success supersedes a failure.
        policy.Record(2, Section, clock.Now, EnrichmentFailureKind.Temporary);
        policy.Record(2, Section, clock.Now, EnrichmentFailureKind.None);
        Assert.False(policy.Evaluate(2, Section, null).CooldownActive);
    }

    [Fact]
    public void NewerPersistedAttemptDiscardsOldMemoryAndRestartUsesFallback()
    {
        var policy = Create(); var state = State();
        policy.Record(1, Section, clock.Now, EnrichmentFailureKind.EmptyPreserved);
        var restart = Create().Evaluate(1, Section, state);
        Assert.Equal(CooldownOrigin.Persisted, restart.Origin); Assert.True(restart.OriginalReasonUnknown);
        Assert.Equal(clock.Now.AddMinutes(15), restart.NextAllowedAtUtc);
        clock.Now = clock.Now.AddMinutes(2); state.LastAttemptAtUtc = clock.Now;
        Assert.Equal(CooldownOrigin.Persisted, policy.Evaluate(1, Section, state).Origin);
        state.LastResult = CatalogUpdateResult.Success; state.LastSuccessAtUtc = clock.Now;
        Assert.False(policy.Evaluate(1, Section, state).CooldownActive);
    }

    [Fact]
    public void PersistedNotFoundSurvivesRestartWithoutChangingOldSuccess()
    {
        var state = State(CatalogUpdateResult.NotFound); var success = state.LastSuccessAtUtc;
        var result = Create().Evaluate(1, Section, state);
        Assert.Equal(clock.Now.AddHours(24), result.NextAllowedAtUtc);
        Assert.Equal(EnrichmentFreshness.Expired, result.Freshness); Assert.False(result.OriginalReasonUnknown);
        Assert.Equal(success, state.LastSuccessAtUtc);
    }

    [Fact]
    public void RecentDataAndCooldownRemainDistinctAndDoNotRenewFreshness()
    {
        var policy = Create(); var state = State(); state.LastSuccessAtUtc = clock.Now.AddDays(-29);
        policy.Record(1, Section, clock.Now, EnrichmentFailureKind.Temporary);
        var result = policy.Evaluate(1, Section, state);
        Assert.True(result.CooldownActive); Assert.Equal(EnrichmentFreshness.Recent, result.Freshness);
        Assert.Equal(state.LastSuccessAtUtc.Value.AddDays(30), result.NextAllowedAtUtc);
        clock.Now = clock.Now.AddMinutes(15);
        Assert.Equal(EnrichmentBlock.Recent, policy.Evaluate(1, Section, state).Block);
    }

    [Fact]
    public void CancelledAndNotRequestedAreNeutralEvenWhenCooldownExists()
    {
        var policy = Create(); policy.Record(1, Section, clock.Now, EnrichmentFailureKind.Temporary);
        var before = policy.Evaluate(1, Section, State());
        policy.Record(1, Section, clock.Now, EnrichmentFailureKind.Cancelled);
        policy.RecordResult(1, clock.Now, new(Section, TaxonomyEnrichmentOutcome.InvalidResponse, IgdbMetadataStatus.NotRequested, null));
        Assert.Equal(before, policy.Evaluate(1, Section, State()));
        Assert.True(Create().Evaluate(1, Section, null).CanAttempt);
    }

    [Fact]
    public void PersistenceFailureMayBeNewerThanUnchangedPersistedState()
    {
        var policy = Create(); var state = State(); clock.Now = clock.Now.AddDays(1);
        var previous = state.LastAttemptAtUtc;
        policy.Record(1, Section, clock.Now, EnrichmentFailureKind.Persistence);
        var result = policy.Evaluate(1, Section, state);
        Assert.Equal(CooldownOrigin.Memory, result.Origin); Assert.Equal(clock.Now.AddMinutes(1), result.NextAllowedAtUtc);
        Assert.Equal(previous, state.LastAttemptAtUtc);
    }

    [Fact]
    public void InvalidTimestampsAndIdentityAreRejectedWithoutLocalConversion()
    {
        var policy = Create(); var state = State(); state.LastAttemptAtUtc = clock.Now.AddTicks(1);
        Assert.Equal(EnrichmentBlock.InconsistentTimestamp, policy.Evaluate(1, Section, state).Block);
        Assert.Throws<ArgumentException>(() => policy.Record(1, Section, clock.Now.AddTicks(1), EnrichmentFailureKind.Temporary));
        Assert.Throws<ArgumentException>(() => policy.Record(1, Section, DateTime.SpecifyKind(clock.Now, DateTimeKind.Local), EnrichmentFailureKind.Temporary));
        Assert.Throws<ArgumentException>(() => policy.Evaluate(2, Section, state));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(0, Section, null));
        state.LastAttemptAtUtc = DateTime.SpecifyKind(clock.Now, DateTimeKind.Unspecified);
        Assert.True(policy.Evaluate(1, Section, state).CooldownActive);
    }

    [Fact]
    public void ExpirationAndCapacityBoundRetention()
    {
        var policy = Create();
        for (var id = 1; id <= GameEnrichmentCooldown.Capacity + 20; id++)
            policy.Record(id, Section, clock.Now, EnrichmentFailureKind.Temporary);
        Assert.Equal(GameEnrichmentCooldown.Capacity, policy.Count);
        clock.Now = clock.Now.AddMinutes(15); Assert.Equal(0, policy.Count);
    }

    [Fact]
    public void CacheRetainsOnlyScalarDiagnosticsAndSingletonUsesReplaceableClock()
    {
        var fields = typeof(GameEnrichmentCooldown).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(HttpClient) || f.FieldType == typeof(CancellationTokenSource));
        var attempt = typeof(GameEnrichmentCooldown).GetNestedType("Attempt", System.Reflection.BindingFlags.NonPublic)!;
        Assert.All(attempt.GetProperties(), p => Assert.True(p.PropertyType == typeof(DateTime) || p.PropertyType.IsEnum));
        var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(clock); services.AddIgdbSearch();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Same(provider.GetRequiredService<GameEnrichmentCooldown>(), provider.GetRequiredService<GameEnrichmentCooldown>());
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
    }
}
