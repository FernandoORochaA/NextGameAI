using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;

namespace NextGameAI.Web.Tests;

[Trait("Host", "None")]
public sealed class GameEnrichmentPolicyTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(-3));
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static GameEnrichmentState State(DateTime time, CatalogMetadataSection section = CatalogMetadataSection.Genres) =>
        new() { GameId = 1, Section = section, LastAttemptAtUtc = time, LastSuccessAtUtc = time, LastResult = CatalogUpdateResult.Success };

    [Theory]
    [InlineData(CatalogMetadataSection.Genres)] [InlineData(CatalogMetadataSection.Themes)]
    [InlineData(CatalogMetadataSection.GameModes)] [InlineData(CatalogMetadataSection.PlayerPerspectives)]
    [InlineData(CatalogMetadataSection.Keywords)] [InlineData(CatalogMetadataSection.TimeToBeat)] [InlineData(CatalogMetadataSection.Multiplayer)]
    public void NoStateIsEligibleButDoesNotTriggerAnyOperation(CatalogMetadataSection section)
    {
        var policy = new GameEnrichmentPolicy(new Clock());
        var result = policy.Evaluate(section, null);
        Assert.True(result.Eligible); Assert.Equal(EnrichmentFreshness.NeverQueried, result.Freshness); Assert.Null(result.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(-1, DateTimeKind.Utc, false)] [InlineData(0, DateTimeKind.Utc, true)] [InlineData(1, DateTimeKind.Utc, true)]
    [InlineData(-1, DateTimeKind.Unspecified, false)] [InlineData(0, DateTimeKind.Unspecified, true)] [InlineData(1, DateTimeKind.Unspecified, true)]
    public void ExactThirtyDaysUsesUtcTicksWithoutLocalConversion(long ticks, DateTimeKind kind, bool eligible)
    {
        var clock = new Clock(); var policy = new GameEnrichmentPolicy(clock);
        var success = DateTime.SpecifyKind(clock.Now.UtcDateTime.AddDays(-30).AddTicks(-ticks), kind);
        var state = State(success); var result = policy.Evaluate(state.Section, state);
        Assert.Equal(eligible, result.Eligible);
        Assert.Equal(eligible ? EnrichmentFreshness.Expired : EnrichmentFreshness.Recent, result.Freshness);
        Assert.Equal(DateTimeKind.Utc, result.ExpiresAtUtc!.Value.Kind);
        Assert.Equal(DateTime.SpecifyKind(success, DateTimeKind.Utc).AddDays(30), result.ExpiresAtUtc);
        Assert.Equal(result, policy.Evaluate(state.Section, state));
        Assert.Equal(success, state.LastSuccessAtUtc);
    }

    [Theory]
    [InlineData(CatalogUpdateResult.TemporaryFailure)] [InlineData(CatalogUpdateResult.NotFound)]
    public void NoSuccessIsEligibleWithoutImplementingCooldown(CatalogUpdateResult result)
    {
        var clock = new Clock(); var state = State(clock.Now.UtcDateTime); state.LastSuccessAtUtc = null; state.LastResult = result;
        var eligibility = new GameEnrichmentPolicy(clock).Evaluate(state.Section, state);
        Assert.True(eligibility.Eligible); Assert.Equal(EnrichmentFreshness.NoSuccessfulUpdate, eligibility.Freshness);
    }

    [Theory]
    [InlineData(10, false)] [InlineData(31, true)]
    public void LaterFailureDoesNotRenewSuccessOrChangeAvailabilityOfStoredData(int age, bool eligible)
    {
        var clock = new Clock(); var state = State(clock.Now.UtcDateTime.AddDays(-age));
        state.LastAttemptAtUtc = clock.Now.UtcDateTime; state.LastResult = CatalogUpdateResult.TemporaryFailure;
        var before = state.LastSuccessAtUtc;
        var result = new GameEnrichmentPolicy(clock).Evaluate(state.Section, state);
        Assert.Equal(eligible, result.Eligible); Assert.Equal(before, state.LastSuccessAtUtc);
    }

    [Theory]
    [InlineData("attempt-future")] [InlineData("success-future")] [InlineData("order")]
    [InlineData("local")] [InlineData("missing-success")] [InlineData("success-mismatch")]
    public void InconsistentTimestampBlocksAutomaticEligibility(string issue)
    {
        var clock = new Clock(); var state = State(clock.Now.UtcDateTime);
        switch (issue)
        {
            case "attempt-future": state.LastAttemptAtUtc = state.LastAttemptAtUtc.AddTicks(1); state.LastResult = CatalogUpdateResult.TemporaryFailure; break;
            case "success-future": state.LastSuccessAtUtc = state.LastSuccessAtUtc!.Value.AddTicks(1); break;
            case "order": state.LastAttemptAtUtc = state.LastAttemptAtUtc.AddDays(-1); break;
            case "local": state.LastAttemptAtUtc = DateTime.SpecifyKind(state.LastAttemptAtUtc, DateTimeKind.Local); break;
            case "missing-success": state.LastSuccessAtUtc = null; break;
            case "success-mismatch": state.LastSuccessAtUtc = state.LastSuccessAtUtc!.Value.AddTicks(-1); break;
        }
        var result = new GameEnrichmentPolicy(clock).Evaluate(state.Section, state);
        Assert.False(result.Eligible); Assert.Equal(EnrichmentFreshness.InconsistentTimestamp, result.Freshness);
    }

    [Fact]
    public void SevenSectionsAreIndependentAndInjectedClockControlsExpiration()
    {
        var clock = new Clock(); var policy = new GameEnrichmentPolicy(clock);
        var states = Enum.GetValues<CatalogMetadataSection>().Select((s, i) => State(clock.Now.UtcDateTime.AddDays(-(25+i)), s)).ToArray();
        Assert.Equal(5, states.Count(s => !policy.Evaluate(s.Section, s).Eligible));
        clock.Now = clock.Now.AddDays(10);
        Assert.All(states, s => Assert.True(policy.Evaluate(s.Section, s).Eligible));
    }

    [Fact]
    public async Task SqliteUnspecifiedTimestampIsEvaluatedAsStoredUtcAndContentWithoutStateIsNotFresh()
    {
        using var connection = new SqliteConnection(TestDatabaseGuard.ConnectionString);
        var guard = new TestDatabaseGuard(connection); guard.Validate(connection); connection.Open();
        using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(connection).AddInterceptors(guard).Options);
        db.Database.Migrate(); var clock = new Clock(); var policy = new GameEnrichmentPolicy(clock);
        var game = new Game { Title = "Synthetic game", IgdbId = 974 }; db.Games.Add(game); await db.SaveChangesAsync();
        db.GameTimeToBeats.Add(new() { GameId = game.Id, NormallySeconds = 10 }); await db.SaveChangesAsync();
        Assert.True(policy.Evaluate(CatalogMetadataSection.TimeToBeat, null).Eligible);
        var state = State(clock.Now.UtcDateTime.AddDays(-30), CatalogMetadataSection.TimeToBeat); state.GameId = game.Id;
        db.GameEnrichmentStates.Add(state); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var stored = await db.GameEnrichmentStates.SingleAsync(); Assert.Equal(DateTimeKind.Unspecified, stored.LastSuccessAtUtc!.Value.Kind);
        Assert.Equal(EnrichmentFreshness.Expired, policy.Evaluate(stored.Section, stored).Freshness);
        Assert.Equal(10, (await db.GameTimeToBeats.SingleAsync()).NormallySeconds);
    }
}
