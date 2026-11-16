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
        Assert.ThrowsAny<InvalidOperationException>(() => VisualDatabasePreparer.ValidateIntent(
            ["prepare-synthetic", "--confirm-synthetic-only"], environment, environment));

    [Theory]
    [InlineData("")] [InlineData("prepare-synthetic")] [InlineData("prepare-synthetic --confirm-synthetic-only --path=forbidden")]
    [InlineData("reset --confirm-synthetic-only")]
    public void MissingConfirmationAndExtraArgumentsCannotSelectAnotherDestination(string input) =>
        Assert.ThrowsAny<InvalidOperationException>(() => VisualDatabasePreparer.ValidateIntent(input.Split(' ', StringSplitOptions.RemoveEmptyEntries),
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
            Assert.Throws<InvalidOperationException>(() => s.Guard.CheckPreparation());
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

    private static Task<int> Entry(Sandbox s, string[] args, StringWriter output, StringWriter error,
        CancellationToken ct = default, Func<string, long>? space = null) =>
        VisualSetupEntry.ExecuteAsync(args, VisualEnvironment.Name, VisualEnvironment.Name,
            () => s.Guard, output, error, ct, space);

    [Fact]
    public async Task CheckUsesAbsoluteFixedDestinationWithoutCreatingAnything()
    {
        using var s = new Sandbox(); using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(0, await Entry(s, ["check-synthetic"], output, error));
        Assert.Contains(s.Guard.DatabasePath, output.ToString());
        Assert.Contains("não reserva", output.ToString()); Assert.Contains("Espaço disponível", output.ToString());
        Assert.Contains("ausentes", output.ToString()); Assert.Empty(error.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(s.Root));
    }

    [Theory]
    [InlineData("")] [InlineData(".ready")] [InlineData(".ready.pending")]
    [InlineData("-wal")] [InlineData("-shm")] [InlineData("-journal")]
    public async Task CheckDetectsEveryArtifactWithoutOpeningSqliteOrChangingBytes(string suffix)
    {
        using var s = new Sandbox(); Directory.CreateDirectory(Path.GetDirectoryName(s.Guard.DatabasePath)!);
        var path = s.Guard.DatabasePath + suffix; byte[] bytes = [17, 23, 91];
        await File.WriteAllBytesAsync(path, bytes);
        var files = Directory.GetFileSystemEntries(s.Root, "*", SearchOption.AllDirectories);
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, await Entry(s, ["check-synthetic"], output, error));
        Assert.Contains("Conflitos", error.ToString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(files, Directory.GetFileSystemEntries(s.Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("", "VisualDevelopment", "VisualDevelopment")]
    [InlineData("check-synthetic extra", "VisualDevelopment", "VisualDevelopment")]
    [InlineData("check-synthetic --confirm-synthetic-only", "VisualDevelopment", "VisualDevelopment")]
    [InlineData("prepare-synthetic", "VisualDevelopment", "VisualDevelopment")]
    [InlineData("reset", "VisualDevelopment", "VisualDevelopment")]
    [InlineData("check-synthetic --path=forbidden", "VisualDevelopment", "VisualDevelopment")]
    [InlineData("check-synthetic prepare-synthetic", "VisualDevelopment", "VisualDevelopment")]
    [InlineData("prepare-synthetic --confirm-synthetic-only --overwrite", "VisualDevelopment", "VisualDevelopment")]
    [InlineData("check-synthetic", null, null)]
    [InlineData("check-synthetic", "Development", "Development")]
    [InlineData("check-synthetic", "VisualDevelopment", "Development")]
    [InlineData("check-synthetic", "Development", "VisualDevelopment")]
    public async Task EntryRejectsInvalidIntentBeforeResolvingAnyDestination(string input, string? dotnet, string? aspnet)
    {
        var calls = 0; using var output = new StringWriter(); using var error = new StringWriter();
        var code = await VisualSetupEntry.ExecuteAsync(input.Split(' ', StringSplitOptions.RemoveEmptyEntries), dotnet, aspnet,
            () => { calls++; throw new Exception("must not resolve"); }, output, error);
        Assert.Equal(1, code); Assert.Equal(0, calls); Assert.Empty(output.ToString());
        Assert.Contains(dotnet != VisualEnvironment.Name || aspnet != VisualEnvironment.Name ? "Ambiente inválido" : "Argumentos inválidos", error.ToString());
    }

    [Fact]
    public async Task EntryCancellationReturnsTwoBeforeResolvingDestination()
    {
        using var ct = new CancellationTokenSource(); ct.Cancel();
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(2, await VisualSetupEntry.ExecuteAsync(["check-synthetic"], VisualEnvironment.Name, VisualEnvironment.Name,
            () => throw new Exception("must not resolve"), output, error, ct.Token));
        Assert.Contains("cancelada", error.ToString());
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1)]
    public async Task CheckRefusesUnavailableOrUnqueryableSpaceWithoutWriting(int mode)
    {
        using var s = new Sandbox(); using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, await Entry(s, ["check-synthetic"], output, error, space: _ => mode == 1 ? throw new IOException("sensitive raw error") : mode));
        Assert.DoesNotContain("sensitive raw error", error.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(s.Root));
    }

    [Theory]
    [InlineData(4u)] [InlineData(0u)]
    public void CheckCannotUseRemoteOrIndeterminateStorage(uint type)
    {
        using var s = new Sandbox();
        Assert.Throws<InvalidOperationException>(() => new VisualDatabaseGuard(s.Root, _ => type));
        Assert.Empty(Directory.EnumerateFileSystemEntries(s.Root));
    }

    [Fact]
    public void CheckRejectsUnreadableMetadataAndNonDirectoryAncestorWithoutWriting()
    {
        using var s = new Sandbox();
        var file = Path.Combine(s.Root, "NextGameAI"); File.WriteAllText(file, "sentinel");
        Assert.Throws<InvalidOperationException>(() => s.Guard.CheckPreparation());
        Assert.Equal("sentinel", File.ReadAllText(file)); Assert.Single(Directory.GetFileSystemEntries(s.Root));
        // A deleted synthetic OS root is indeterminate, never treated as a creatable root.
        File.Delete(file); Directory.Delete(s.Root);
        try { Assert.ThrowsAny<IOException>(() => s.Guard.CheckPreparation()); }
        finally { Directory.CreateDirectory(s.Root); }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task InternalPublicationFailuresDoNotLeaveAValidFinalMarker(int value)
    {
        using var s = new Sandbox(); var point = (VisualDatabaseGuard.PublicationPoint)value;
        await Assert.ThrowsAsync<IOException>(() => VisualDatabasePreparer.ForTests(s.Root, new Clock(),
            publicationProbe: p => { if (p == point) throw new IOException("synthetic fault"); }).PrepareAsync());
        Assert.False(File.Exists(s.Guard.DatabasePath + ".ready"));
        Assert.Throws<InvalidOperationException>(() => s.Guard.RequirePrepared());
        Assert.Equal(point is VisualDatabaseGuard.PublicationPoint.Write or VisualDatabaseGuard.PublicationPoint.Flush or VisualDatabaseGuard.PublicationPoint.Move,
            File.Exists(s.Guard.DatabasePath + ".ready.pending"));
        using var exclusive = new FileStream(s.Guard.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task LateFinalMarkerConflictIsNeverOverwritten()
    {
        using var s = new Sandbox(); var marker = s.Guard.DatabasePath + ".ready";
        await Assert.ThrowsAsync<IOException>(() => VisualDatabasePreparer.ForTests(s.Root, new Clock(), publicationProbe: p =>
        { if (p == VisualDatabaseGuard.PublicationPoint.Move) File.WriteAllText(marker, "unknown-owner"); }).PrepareAsync());
        Assert.Equal("unknown-owner", File.ReadAllText(marker)); Assert.True(File.Exists(marker + ".pending"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationAtMoveBoundaryRespectsCommitPoint(bool afterMove)
    {
        using var s = new Sandbox(); using var ct = new CancellationTokenSource();
        var prep = VisualDatabasePreparer.ForTests(s.Root, new Clock(), publicationProbe: p =>
        { if (p == (afterMove ? VisualDatabaseGuard.PublicationPoint.Validate : VisualDatabaseGuard.PublicationPoint.Move)) ct.Cancel(); });
        if (afterMove) { await prep.PrepareAsync(ct.Token); s.Guard.RequirePrepared(); }
        else { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prep.PrepareAsync(ct.Token)); Assert.False(File.Exists(s.Guard.DatabasePath + ".ready")); }
    }

    [Fact]
    public async Task ActualPostMoveValidationFailureRemovesOnlyOwnedMarker()
    {
        using var s = new Sandbox();
        await Assert.ThrowsAsync<InvalidOperationException>(() => VisualDatabasePreparer.ForTests(s.Root, new Clock(), publicationProbe: p =>
        { if (p == VisualDatabaseGuard.PublicationPoint.Validate) File.AppendAllText(s.Guard.DatabasePath + ".ready", "invalid"); }).PrepareAsync());
        Assert.False(File.Exists(s.Guard.DatabasePath + ".ready")); Assert.True(File.Exists(s.Guard.DatabasePath));
    }

    [Fact]
    public async Task ReplacedMarkerAfterMoveIsPreservedOnValidationFailure()
    {
        using var s = new Sandbox(); var marker = s.Guard.DatabasePath + ".ready";
        await Assert.ThrowsAsync<InvalidOperationException>(() => VisualDatabasePreparer.ForTests(s.Root, new Clock(), publicationProbe: p =>
        {
            if (p != VisualDatabaseGuard.PublicationPoint.Validate) return;
            File.Move(marker, Path.Combine(s.Root, "owned-marker")); File.WriteAllText(marker, "unknown replacement");
        }).PrepareAsync());
        Assert.Equal("unknown replacement", File.ReadAllText(marker));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task PartialWriteAndFlushFailuresRetainPendingAndCloseItsHandle(bool flush)
    {
        using var s = new Sandbox(); using var output = new StringWriter(); using var error = new StringWriter();
        s.Guard.PendingStreamProbe = (point, stream) =>
        {
            if (!flush && point == VisualDatabaseGuard.PublicationPoint.Write)
            { stream.WriteByte(42); throw new IOException("synthetic partial write"); }
            if (flush && point == VisualDatabaseGuard.PublicationPoint.Flush)
            { stream.Dispose(); } // Actual Flush(true) then fails on the disposed owned stream.
        };
        Assert.Equal(1, await Entry(s, ["prepare-synthetic", "--confirm-synthetic-only"], output, error));
        Assert.False(File.Exists(s.Guard.DatabasePath + ".ready"));
        using var pending = new FileStream(s.Guard.DatabasePath + ".ready.pending", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(pending.Length > 0);
        if (!flush) Assert.Equal(42, pending.ReadByte());
    }

    [Fact]
    public async Task PendingCreationRaceFailsActualCreateNewWithoutOverwrite()
    {
        using var s = new Sandbox(); var pending = s.Guard.DatabasePath + ".ready.pending";
        await Assert.ThrowsAsync<IOException>(() => VisualDatabasePreparer.ForTests(s.Root, new Clock(), publicationProbe: p =>
        { if (p == VisualDatabaseGuard.PublicationPoint.CreatePending) File.WriteAllText(pending, "unknown-owner"); }).PrepareAsync());
        Assert.Equal("unknown-owner", File.ReadAllText(pending)); Assert.False(File.Exists(s.Guard.DatabasePath + ".ready"));
    }

    [Fact]
    public void CheckRejectsHardLinkedArtifactWithoutChangingIt()
    {
        using var s = new Sandbox(); Directory.CreateDirectory(Path.GetDirectoryName(s.Guard.DatabasePath)!);
        var original = Path.Combine(s.Root, "owned-sentinel"); File.WriteAllText(original, "synthetic");
        Assert.True(CreateSyntheticHardLink(s.Guard.DatabasePath, original, IntPtr.Zero));
        Assert.Throws<InvalidOperationException>(() => s.Guard.CheckPreparation());
        Assert.Equal("synthetic", File.ReadAllText(original));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateSyntheticHardLink(string name, string existing, IntPtr security);

    [Fact]
    public async Task PermissionFailureDoesNotExposeRawDetails()
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        Assert.Equal(1, await VisualSetupEntry.ExecuteAsync(["check-synthetic"], VisualEnvironment.Name, VisualEnvironment.Name,
            () => throw new UnauthorizedAccessException("sensitive details"), output, error));
        Assert.Contains("Permissão insuficiente", error.ToString()); Assert.DoesNotContain("sensitive details", error.ToString());
    }

    private sealed class MissingFinalMessage : StringWriter
    {
        public override Task WriteLineAsync(string? value) => value == "Preparação sintética concluída e validada."
            ? throw new IOException("synthetic output failure") : base.WriteLineAsync(value);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConsoleSuccessOrMissingFinalMessageDoesNotUndoPublication(bool failOutput)
    {
        using var s = new Sandbox(); using StringWriter output = failOutput ? new MissingFinalMessage() : new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(failOutput ? 1 : 0, await Entry(s, ["prepare-synthetic", "--confirm-synthetic-only"], output, error));
        s.Guard.RequirePrepared();
        var bytes = await File.ReadAllBytesAsync(s.Guard.DatabasePath);
        Assert.Equal(1, await Entry(s, ["prepare-synthetic", "--confirm-synthetic-only"], new StringWriter(), new StringWriter()));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(s.Guard.DatabasePath));
    }

}
