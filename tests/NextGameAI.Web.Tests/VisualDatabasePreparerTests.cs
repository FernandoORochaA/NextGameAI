using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NextGameAI.VisualSetup;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.VisualDevelopment;

namespace NextGameAI.Web.Tests;

[Trait("Host", "None")]
public sealed class VisualDatabasePreparerTests
{
    private static readonly DateTime Instant = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(Instant); }
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "NextGameAI-setup-test-" + Guid.NewGuid().ToString("N"));
        public VisualDatabaseGuard Guard { get; }
        public Sandbox() { Directory.CreateDirectory(Root); Guard = new(Root); }
        public VisualDatabasePreparer Preparer(Func<PreparationStage, CancellationToken, Task>? hook = null) =>
            VisualDatabasePreparer.ForTests(Root, new Clock(), hook);
        public NextGameAiDbContext Read() => new(new DbContextOptionsBuilder<NextGameAiDbContext>()
            .UseSqlite(Guard.ConnectionString).AddInterceptors(Guard).Options);
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("NextGameAI-setup-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe cleanup");
            Directory.Delete(full, recursive: true);
        }
    }

    [Fact]
    public async Task RealPreparationMigratesSeedsVerifiesAndReopensWithDailyGuard()
    {
        using var s = new Sandbox(); Assert.False(File.Exists(s.Guard.DatabasePath));
        await s.Preparer().PrepareAsync(); s.Guard.RequirePrepared();
        Assert.Equal(s.Guard.GetPreparationMarkerContents(), await File.ReadAllTextAsync(s.Guard.DatabasePath + ".ready"));
        Assert.False(File.Exists(s.Guard.DatabasePath + ".ready.pending"));
        using var db = s.Read();
        // Daily usage does not run migration tooling (which creates ReadOnly probe connections).
        // Read the history with an ordinary guarded ReadWrite query instead.
        Assert.Equal(new[] { "20261022004238_InitialLibrary", "20261024195821_IgdbCatalogMetadata", "20261024220151_AddCatalogEnrichmentMetadata" },
            await db.Database.SqlQueryRaw<string>("SELECT MigrationId AS Value FROM __EFMigrationsHistory ORDER BY MigrationId").ToArrayAsync());
        Assert.Equal(4, await db.Games.CountAsync()); Assert.Equal(4, await db.LibraryEntries.CountAsync());
        Assert.Equal(5, await db.GameOwnerships.CountAsync());
        Assert.All(await db.Games.ToArrayAsync(), g => { Assert.StartsWith("Visual:", g.Title); Assert.Null(g.IgdbId); Assert.Null(g.CoverImageId); });
        Assert.Equal(new int?[] { 9, 0, null, 6 }, await db.LibraryEntries.OrderBy(e => e.Id).Select(e => e.Rating).ToArrayAsync());
        var reader = new GameMetadataReadService(db, new GameEnrichmentPolicy(new Clock()));
        var a = (await reader.GetByGameIdAsync(1001))!; var b = (await reader.GetByGameIdAsync(1002))!;
        var c = (await reader.GetByGameIdAsync(1003))!; var d = (await reader.GetByGameIdAsync(1004))!;
        Assert.Single(a.Genres); Assert.Single(a.Themes); Assert.Single(a.GameModes); Assert.Single(a.PlayerPerspectives);
        Assert.Equal(3600, a.TimeToBeat!.NormallySeconds); Assert.Equal(2, a.MultiplayerModes.Count);
        Assert.All(a.States, state => Assert.Equal(EnrichmentFreshness.Recent, state.Freshness));
        Assert.Null(b.TimeToBeat!.HastilySeconds); Assert.Equal(0, b.TimeToBeat.NormallySeconds);
        var mode = Assert.Single(b.MultiplayerModes); Assert.True(mode.CampaignCoop); Assert.False(mode.DropIn);
        Assert.Null(mode.LanCoop); Assert.Equal(0, mode.OfflineMax); Assert.Null(mode.OnlineMax); Assert.Null(mode.Platform);
        Assert.Null(c.TimeToBeat); Assert.Empty(c.Genres); Assert.All(c.States, state => Assert.Null(state.LastResult));
        var old = d.States.Single(state => state.Section == CatalogMetadataSection.Genres);
        Assert.Equal(EnrichmentFreshness.Expired, old.Freshness); Assert.Equal(Instant.AddDays(-45), old.LastSuccessAtUtc);
        Assert.Equal(Instant.AddDays(-1), old.LastAttemptAtUtc); Assert.Single(d.Genres);
        Assert.DoesNotContain("SEGREDO_VISUAL_SINTETICO", System.Text.Json.JsonSerializer.Serialize(a));
        Assert.True(await db.CatalogTaxonomies.AnyAsync(t => t.Kind == CatalogTaxonomyKind.Keyword));
    }

    [Theory]
    [InlineData((int)PreparationStage.Created)] [InlineData((int)PreparationStage.Migrating)] [InlineData((int)PreparationStage.Migrated)]
    [InlineData((int)PreparationStage.Seeding)] [InlineData((int)PreparationStage.Seeded)] [InlineData((int)PreparationStage.Verifying)]
    [InlineData((int)PreparationStage.Verified)] [InlineData((int)PreparationStage.Publishing)]
    public async Task FailureAtEveryBoundaryNeverPublishesReadyAndReexecutionCannotEraseEvidence(int failureValue)
    {
        var failure = (PreparationStage)failureValue;
        using var s = new Sandbox(); var error = new InvalidOperationException("Synthetic failure");
        var observed = await Record.ExceptionAsync(() => s.Preparer((stage, _) => stage == failure ? throw error : Task.CompletedTask).PrepareAsync());
        Assert.Same(error, observed); Assert.False(File.Exists(s.Guard.DatabasePath + ".ready"));
        Assert.Throws<InvalidOperationException>(() => s.Guard.RequirePrepared());
        var before = await File.ReadAllBytesAsync(s.Guard.DatabasePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Preparer().PrepareAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(s.Guard.DatabasePath));
        if (failure == PreparationStage.Seeded)
        {
            // Read only the known synthetic file for checking rollback; daily guard correctly refuses incomplete preparation.
            using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(s.Guard.ConnectionString).Options);
            Assert.Empty(await db.Games.ToArrayAsync()); Assert.Empty(await db.LibraryEntries.ToArrayAsync());
        }
    }

    [Fact]
    public async Task SuccessfulSecondPreparationDoesNotModifyAnyExistingByte()
    {
        using var s = new Sandbox(); await s.Preparer().PrepareAsync();
        var before = await File.ReadAllBytesAsync(s.Guard.DatabasePath); var marker = await File.ReadAllBytesAsync(s.Guard.DatabasePath + ".ready");
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Preparer().PrepareAsync());
        Assert.Equal(before, await File.ReadAllBytesAsync(s.Guard.DatabasePath));
        Assert.Equal(marker, await File.ReadAllBytesAsync(s.Guard.DatabasePath + ".ready"));
    }

    [Theory]
    [InlineData("")] [InlineData(".ready")] [InlineData(".ready.pending")] [InlineData("-wal")] [InlineData("-shm")] [InlineData("-journal")]
    public async Task ExistingUnknownFileIsNeverOpenedOrOverwritten(string suffix)
    {
        using var s = new Sandbox(); Directory.CreateDirectory(Path.GetDirectoryName(s.Guard.DatabasePath)!);
        var target = s.Guard.DatabasePath + suffix; byte[] sentinel = [1, 2, 3, 4]; await File.WriteAllBytesAsync(target, sentinel);
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Preparer().PrepareAsync());
        Assert.Equal(sentinel, await File.ReadAllBytesAsync(target));
        if (suffix != "") Assert.False(File.Exists(s.Guard.DatabasePath));
    }

    [Fact]
    public async Task ParallelPreparersCannotOwnTheSameDestination()
    {
        using var s = new Sandbox(); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = s.Preparer(async (stage, ct) => { if (stage == PreparationStage.Created) { reached.SetResult(); await release.Task.WaitAsync(ct); } }).PrepareAsync();
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Preparer().PrepareAsync());
        }
        finally { release.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(15)); }
        s.Guard.RequirePrepared();
    }

    [Fact]
    public async Task CancelBeforeCreationLeavesNoApplicationDirectory()
    {
        using var s = new Sandbox(); using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Preparer().PrepareAsync(ct.Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(s.Root));
    }

    [Theory]
    [InlineData((int)PreparationStage.Migrating)] [InlineData((int)PreparationStage.Seeded)] [InlineData((int)PreparationStage.Publishing)]
    public async Task CancellationDuringPreparationDoesNotPublishOrLeakOpenResources(int phaseValue)
    {
        var phase = (PreparationStage)phaseValue;
        using var s = new Sandbox(); using var ct = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Preparer((stage, _) =>
        { if (stage == phase) ct.Cancel(); return Task.CompletedTask; }).PrepareAsync(ct.Token));
        Assert.False(File.Exists(s.Guard.DatabasePath + ".ready"));
        using var exclusive = new FileStream(s.Guard.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("Development")] [InlineData("Production")] [InlineData("Testing")] [InlineData("")]
    public void OtherEnvironmentsAreRejectedBeforeDestinationIsResolved(string environment) =>
        Assert.Throws<InvalidOperationException>(() => VisualDatabasePreparer.ValidateIntent(
            ["prepare-synthetic", "--confirm-synthetic-only"], environment, environment));

    [Theory]
    [InlineData("")] [InlineData("prepare-synthetic")] [InlineData("prepare-synthetic --confirm-synthetic-only --path=forbidden")]
    [InlineData("reset --confirm-synthetic-only")]
    public void MissingConfirmationAndExtraArgumentsCannotSelectAnotherDestination(string input) =>
        Assert.Throws<InvalidOperationException>(() => VisualDatabasePreparer.ValidateIntent(input.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            VisualEnvironment.Name, VisualEnvironment.Name));

    [Theory]
    [InlineData("relative-root")] [InlineData("C:\\SyntheticForbidden")]
    public void InternalTestEntryCannotTargetArbitraryRoots(string root) =>
        Assert.Throws<InvalidOperationException>(() => VisualDatabasePreparer.ForTests(root, new Clock()));

    [Fact]
    public async Task MarkerPublicationConflictCannotOverwriteUnknownMarker()
    {
        using var s = new Sandbox();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Preparer(async (stage, ct) =>
        { if (stage == PreparationStage.Publishing) await File.WriteAllTextAsync(s.Guard.DatabasePath + ".ready.pending", "synthetic-conflict", ct); }).PrepareAsync());
        Assert.False(File.Exists(s.Guard.DatabasePath + ".ready"));
        Assert.Equal("synthetic-conflict", await File.ReadAllTextAsync(s.Guard.DatabasePath + ".ready.pending"));
    }

    [Fact]
    public async Task VerificationRejectsUnexpectedSyntheticRecordBeforeMarker()
    {
        using var s = new Sandbox();
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Preparer(async (stage, ct) =>
        {
            if (stage != PreparationStage.Verifying) return;
            // Sole unguarded test connection is explicitly tied to this fixture's synthetic created file.
            using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(s.Guard.ConnectionString).Options);
            db.Games.Add(new() { Title = "Synthetic unexpected extra" }); await db.SaveChangesAsync(ct);
        }).PrepareAsync());
        Assert.False(File.Exists(s.Guard.DatabasePath + ".ready"));
    }

    [Fact]
    public async Task ReplacedPreparedFileIsRejectedByDailyGuard()
    {
        using var s = new Sandbox(); await s.Preparer().PrepareAsync();
        File.Move(s.Guard.DatabasePath, Path.Combine(s.Root, "owned-previous.bin"));
        await File.WriteAllBytesAsync(s.Guard.DatabasePath, []);
        Assert.Throws<InvalidOperationException>(() => s.Guard.RequirePrepared());
    }

    [Fact]
    public async Task FaultDuringRealMigrationSqlLeavesNoReadyAndClosedFile()
    {
        using var s = new Sandbox(); var calls = 0; var fault = new InvalidOperationException("Synthetic migration SQL fault");
        var observed = await Record.ExceptionAsync(() => s.Preparer((stage, _) =>
        { if (stage == PreparationStage.MigrationSql && ++calls == 2) throw fault; return Task.CompletedTask; }).PrepareAsync());
        Assert.Same(fault, observed); Assert.Equal(2, calls); Assert.False(File.Exists(s.Guard.DatabasePath + ".ready"));
        using var exclusive = new FileStream(s.Guard.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ExistingDirectoryJunctionIsRejectedBeforeCreatingFilesThroughIt()
    {
        using var s = new Sandbox(); var target = Path.Combine(s.Root, "synthetic-link-target"); Directory.CreateDirectory(target);
        var junction = Path.Combine(s.Root, "NextGameAI");
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "/c", "mklink", "/J", junction, target }) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!; process.WaitForExit();
        try
        {
            Assert.Equal(0, process.ExitCode);
            await Assert.ThrowsAsync<InvalidOperationException>(() => s.Preparer().PrepareAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        }
        finally { if (Directory.Exists(junction)) Directory.Delete(junction); }
    }

    [Fact]
    public async Task FileInPlaceOfApplicationDirectoryIsNeverReplaced()
    {
        using var s = new Sandbox(); var file = Path.Combine(s.Root, "NextGameAI");
        await File.WriteAllTextAsync(file, "synthetic-sentinel");
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.Preparer().PrepareAsync());
        Assert.Equal("synthetic-sentinel", await File.ReadAllTextAsync(file));
    }
}
