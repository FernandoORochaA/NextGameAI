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
        Assert.All(attempt.GetProperties(), p => Assert.True(p.PropertyType == typeof(DateTime) || p.PropertyType == typeof(long) || p.PropertyType.IsEnum));
        var services = new ServiceCollection(); services.AddSingleton<TimeProvider>(clock); services.AddIgdbSearch();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Same(provider.GetRequiredService<GameEnrichmentCooldown>(), provider.GetRequiredService<GameEnrichmentCooldown>());
        Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
    }

    private static TaxonomyEnrichmentResult Limited(int minutes, CatalogMetadataSection section = Section) =>
        new(section, TaxonomyEnrichmentOutcome.ExternalFailure, IgdbMetadataStatus.TransportFailure,
            IgdbQueryFailure.RateLimited, HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(minutes));

    [Theory]
    [InlineData(40, 40)] [InlineData(5, 15)] [InlineData(15, 15)] [InlineData(-1, 15)]
    public async Task IdentifiedAttemptUsesPostClientReferenceAndFixedAbsoluteDeadline(int minutes, int expected)
    {
        var c = new GameEnrichmentCoordinator(); using var game = await c.AcquireGameAsync(1);
        var id = game.CreateAttempt(Section); var started = clock.Now;
        clock.Now = clock.Now.AddMinutes(10); var completed = clock.Now;
        var policy = Create(); var observation = policy.PrepareResult(id, started, completed, Limited(minutes))!;
        Assert.Equal(completed.AddMinutes(expected), observation.UntilUtc);
        Assert.Equal(DateTimeKind.Utc, observation.UntilUtc.Kind);
        clock.Now = clock.Now.AddMinutes(3); // Simulated wait for writing/commit.
        policy.RecordConfirmed(observation);
        Assert.Equal(observation.UntilUtc, policy.Evaluate(1, Section, null).NextAllowedAtUtc);
        clock.Now = clock.Now.AddMinutes(1); policy.RecordConfirmed(observation);
        Assert.Equal(observation.UntilUtc, policy.Evaluate(1, Section, null).NextAllowedAtUtc);
        Assert.Equal(started, observation.PersistedAttemptAtUtc);
        Assert.Equal(completed, observation.ReferenceAtUtc);
    }

    [Theory]
    [InlineData(15, 40, false, 40)] [InlineData(40, 15, false, 40)]
    [InlineData(15, 40, true, 40)] [InlineData(40, 15, true, 40)]
    public async Task EqualTimestampsNeverLoseLongerDeadline(int first, int second, bool sameIdentity, int expected)
    {
        using var game = await new GameEnrichmentCoordinator().AcquireGameAsync(1);
        var a = game.CreateAttempt(Section); var b = sameIdentity ? a : game.CreateAttempt(Section);
        var policy = Create();
        policy.RecordConfirmed(policy.PrepareResult(a, clock.Now, clock.Now, Limited(first))!);
        policy.RecordConfirmed(policy.PrepareResult(b, clock.Now, clock.Now, Limited(second))!);
        Assert.Equal(clock.Now.AddMinutes(expected), policy.Evaluate(1, Section, State()).NextAllowedAtUtc);
    }

    [Fact]
    public async Task LateOldFailureCannotReplaceNewerFailureOrConfirmedSuccess()
    {
        using var game = await new GameEnrichmentCoordinator().AcquireGameAsync(1);
        var old = game.CreateAttempt(Section); var current = game.CreateAttempt(Section);
        var policy = Create(); var time = clock.Now;
        var delayed = policy.PrepareResult(old, time, time, Limited(120))!;
        policy.RecordConfirmed(policy.PrepareResult(current, time, time, Limited(15))!);
        policy.RecordConfirmed(delayed);
        Assert.Equal(time.AddMinutes(15), policy.Evaluate(1, Section, null).NextAllowedAtUtc);
        var successId = game.CreateAttempt(Section);
        var success = policy.PrepareResult(successId, time, time, new(Section, TaxonomyEnrichmentOutcome.Applied, IgdbMetadataStatus.Success, null))!;
        Assert.True(policy.Evaluate(1, Section, null).CooldownActive); // Issued/prepared is not confirmed.
        policy.RecordConfirmed(success); policy.RecordConfirmed(delayed);
        Assert.False(policy.Evaluate(1, Section, null).CooldownActive);
        var newer = game.CreateAttempt(Section);
        policy.RecordConfirmed(policy.PrepareResult(newer, time, time, Limited(40))!);
        Assert.True(policy.Evaluate(1, Section, null).CooldownActive);
    }

    [Fact]
    public async Task IdentityStillRequiresCorrectPersistedGameSectionAndTimestamp()
    {
        using var game = await new GameEnrichmentCoordinator().AcquireGameAsync(1);
        var id = game.CreateAttempt(Section); var policy = Create();
        Assert.Throws<ArgumentException>(() => policy.PrepareResult(id, clock.Now, clock.Now, Limited(40, CatalogMetadataSection.Themes)));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.PrepareResult(default, clock.Now, clock.Now, Limited(40)));
        policy.RecordConfirmed(policy.PrepareResult(id, clock.Now, clock.Now, Limited(40))!);
        Assert.True(policy.Evaluate(2, Section, null).CanAttempt);
        Assert.True(policy.Evaluate(1, CatalogMetadataSection.Themes, null).CanAttempt);
        var state = State(); state.LastAttemptAtUtc = state.LastAttemptAtUtc.AddTicks(-1);
        Assert.Equal(CooldownOrigin.Persisted, policy.Evaluate(1, Section, state).Origin);
        Assert.Throws<ArgumentException>(() => policy.Evaluate(2, Section, state));
    }

    [Fact]
    public async Task RollbackRecordsOnlyOneMinuteAndPreparationDoesNotClaimSuccess()
    {
        using var game = await new GameEnrichmentCoordinator().AcquireGameAsync(1);
        var id = game.CreateAttempt(Section); var policy = Create();
        _ = policy.PrepareResult(id, clock.Now, clock.Now, new(Section, TaxonomyEnrichmentOutcome.Applied, IgdbMetadataStatus.Success, null));
        Assert.Equal(0, policy.Count);
        policy.RecordPersistenceFailure(id, clock.Now);
        Assert.Equal(clock.Now.AddMinutes(1), policy.Evaluate(1, Section, null).NextAllowedAtUtc);
        Assert.Equal(EnrichmentFreshness.NeverQueried, policy.Evaluate(1, Section, null).Freshness);
    }

    [Fact]
    public async Task RestartEvictionAndExpiryKeepExistingFallbackAndCapacity()
    {
        var c = new GameEnrichmentCoordinator(); var policy = Create();
        using (var game = await c.AcquireGameAsync(1))
            policy.RecordConfirmed(policy.PrepareResult(game.CreateAttempt(Section), clock.Now, clock.Now, Limited(40))!);
        Assert.Equal(clock.Now.AddMinutes(15), Create().Evaluate(1, Section, State()).NextAllowedAtUtc);
        for (var i = 2; i <= GameEnrichmentCooldown.Capacity + 1; i++)
        {
            clock.Now = clock.Now.AddTicks(1);
            using var game = await c.AcquireGameAsync(i);
            policy.RecordConfirmed(policy.PrepareResult(game.CreateAttempt(Section), clock.Now, clock.Now, Limited(40))!);
        }
        Assert.Equal(GameEnrichmentCooldown.Capacity, policy.Count);
        Assert.Equal(CooldownOrigin.Persisted, policy.Evaluate(1, Section, State()).Origin);
        clock.Now = clock.Now.AddMinutes(40); Assert.Equal(0, policy.Count);
    }

    [Fact]
    public async Task ConcurrentRecordPreservesMaximumAndNewSuccessWatermark()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var game = await new GameEnrichmentCoordinator().AcquireGameAsync(1);
        var id = game.CreateAttempt(Section); var policy = Create();
        var observations = Enumerable.Range(15, 32).Select(m => policy.PrepareResult(id, clock.Now, clock.Now, Limited(m))!).ToArray();
        var tasks = observations.Select(o => Task.Run(async () => { await start.Task; policy.RecordConfirmed(o); })).ToArray();
        start.SetResult(); await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(clock.Now.AddMinutes(46), policy.Evaluate(1, Section, null).NextAllowedAtUtc);
    }

    [Fact]
    public async Task InvalidReferencesAreRejectedAndNotRequestedRemainsNeutral()
    {
        using var game = await new GameEnrichmentCoordinator().AcquireGameAsync(1);
        var id = game.CreateAttempt(Section); var policy = Create();
        Assert.Throws<ArgumentException>(() => policy.PrepareResult(id, clock.Now, clock.Now.AddTicks(1), Limited(40)));
        Assert.Throws<ArgumentException>(() => policy.PrepareResult(id, clock.Now, DateTime.SpecifyKind(clock.Now, DateTimeKind.Local), Limited(40)));
        Assert.Null(policy.PrepareResult(id, clock.Now, clock.Now, new(Section, TaxonomyEnrichmentOutcome.InvalidResponse, IgdbMetadataStatus.NotRequested, null)));
        Assert.Equal(0, policy.Count);
        var first = policy.PrepareResult(id, clock.Now, clock.Now, Limited(40))!; policy.RecordConfirmed(first);
        clock.Now = clock.Now.AddMinutes(1);
        Assert.Throws<ArgumentException>(() => policy.RecordConfirmed(policy.PrepareResult(id, first.PersistedAttemptAtUtc, clock.Now, Limited(40))!));
        Assert.Equal(first.UntilUtc, policy.Evaluate(1, Section, null).NextAllowedAtUtc);
    }

    [Fact]
    public async Task CancellationAfterPreparationPublishesNothingAndDoesNotClearEarlierFailure()
    {
        using var game = await new GameEnrichmentCoordinator().AcquireGameAsync(1);
        var policy = Create(); var old = game.CreateAttempt(Section);
        var first = policy.PrepareResult(old, clock.Now, clock.Now, Limited(40))!; policy.RecordConfirmed(first);
        var next = game.CreateAttempt(Section);
        using var cancellation = new CancellationTokenSource();
        _ = policy.PrepareResult(next, clock.Now, clock.Now, new(Section, TaxonomyEnrichmentOutcome.Applied, IgdbMetadataStatus.Success, null));
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => cancellation.Token.ThrowIfCancellationRequested());
        Assert.Equal(first.UntilUtc, policy.Evaluate(1, Section, null).NextAllowedAtUtc);
    }

    [Fact]
    public async Task NewIdentityOrdersAttemptsWithoutDependingOnClockResolutionOrDirection()
    {
        using var game = await new GameEnrichmentCoordinator().AcquireGameAsync(1);
        var a = game.CreateAttempt(Section); var policy = Create();
        policy.RecordConfirmed(policy.PrepareResult(a, clock.Now, clock.Now, Limited(40))!);
        var b = game.CreateAttempt(Section); var earlier = clock.Now.AddTicks(-1);
        policy.RecordConfirmed(policy.PrepareResult(b, earlier, earlier, Limited(60))!);
        Assert.Equal(earlier.AddMinutes(60), policy.Evaluate(1, Section, null).NextAllowedAtUtc);
        policy.RecordConfirmed(policy.PrepareResult(a, clock.Now, clock.Now, Limited(120))!);
        Assert.Equal(earlier.AddMinutes(60), policy.Evaluate(1, Section, null).NextAllowedAtUtc);
    }

    [Fact]
    public async Task IdentifiedUpdatesAndEvictionStayBoundedUnderConcurrentPublication()
    {
        var c = new GameEnrichmentCoordinator(); var policy = Create();
        var observations = new List<EnrichmentCooldownObservation>();
        for (var i = 1; i <= GameEnrichmentCooldown.Capacity + 32; i++)
        {
            using var game = await c.AcquireGameAsync(i);
            observations.Add(policy.PrepareResult(game.CreateAttempt(Section), clock.Now, clock.Now, Limited(40))!);
        }
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = observations.Select(o => Task.Run(async () =>
        {
            await start.Task;
            policy.RecordConfirmed(o); policy.RecordConfirmed(o);
            Assert.InRange(policy.Count, 1, GameEnrichmentCooldown.Capacity);
        })).ToArray();
        start.SetResult(); await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(GameEnrichmentCooldown.Capacity, policy.Count);
        clock.Now = clock.Now.AddMinutes(40); Assert.Equal(0, policy.Count);
    }
}
