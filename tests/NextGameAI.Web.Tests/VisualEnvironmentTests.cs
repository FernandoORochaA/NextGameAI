using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NextGameAI.Web.Data;
using NextGameAI.Web.Services.Igdb;
using NextGameAI.Web.VisualDevelopment;

namespace NextGameAI.Web.Tests;

[Trait("Host", "None")]
public sealed class VisualEnvironmentTests
{
    // Only synthetic files under a unique temporary root; no SQLite file is opened or migrated.
    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "NextGameAI-visual-test-" + Guid.NewGuid().ToString("N"));
        public VisualDatabaseGuard Guard { get; }
        public Sandbox() { Directory.CreateDirectory(Root); Guard = new(Root); }
        public void MarkSyntheticFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Guard.DatabasePath)!);
            File.WriteAllBytes(Guard.DatabasePath, []); // Not a database, metadata-only guard tests.
            File.WriteAllText(Guard.DatabasePath + ".ready", Guard.GetPreparationMarkerContents());
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(Root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("NextGameAI-visual-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe test cleanup");
            Directory.Delete(full, recursive: true);
        }
    }

    private static IConfiguration Configuration(string? version = "1", string? enabled = "false", string? connection = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["VisualDevelopment:ConfigurationVersion"] = version, ["Igdb:Enabled"] = enabled,
            ["ConnectionStrings:Library"] = connection }).Build();

    private static VisualDatabaseGuard WithDriveType(string root, Func<string, uint> classify) =>
        (VisualDatabaseGuard)Activator.CreateInstance(typeof(VisualDatabaseGuard),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [root, classify], null)!;

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(4u)] [InlineData(5u)] [InlineData(6u)] [InlineData(99u)]
    public void Vd01OnlyKnownFixedStorageIsAllowed(uint type)
    {
        var queries = new List<string>();
        // Z: is entirely synthetic: the injected classification performs no OS or filesystem lookup.
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => WithDriveType(@"Z:\SyntheticData",
            root => { queries.Add(root); return type; }));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(new[] { @"Z:\" }, queries);
    }

    [Fact]
    public void Vd01NativeClassificationFailureIsRejectedBeforeFilesystemAccess()
    {
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => WithDriveType(@"Z:\SyntheticData",
            _ => throw new System.ComponentModel.Win32Exception(5)));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }

    [Fact]
    public void Vd01UncRootIsRejectedWithoutEvenClassifyingIt()
    {
        var calls = 0;
        var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => WithDriveType(@"\\synthetic-server\share",
            _ => { calls++; return 3; }));
        Assert.IsType<InvalidOperationException>(error.InnerException); Assert.Equal(0, calls);
    }

    [Fact]
    public void Vd01FixedVolumeAcceptsPreparedSyntheticDestinationAndUsesDriveRoot()
    {
        using var s = new Sandbox(); s.MarkSyntheticFile(); var queried = new List<string>();
        var guard = WithDriveType(s.Root, root => { queried.Add(root); return 3; });
        guard.ValidateConnectionString(guard.ConnectionString); guard.RequirePrepared();
        Assert.NotEmpty(queried);
        Assert.All(queried, root => Assert.Equal(Path.GetPathRoot(s.Root), root));
        Assert.Equal(s.Guard.GetPreparationMarkerContents(), guard.GetPreparationMarkerContents());
    }

    [Fact]
    public async Task Vd01ChangedDriveTypeBlocksBothOpensAndPreparationWithoutCreatingSqlite()
    {
        using var s = new Sandbox(); uint type = 3; var guard = WithDriveType(s.Root, _ => type);
        using var connection = new SqliteConnection(guard.ConnectionString);
        using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>()
            .UseSqlite(connection).AddInterceptors(guard).Options);
        type = 4;
        Assert.Throws<InvalidOperationException>(() => guard.ValidateConnectionString(guard.ConnectionString));
        Assert.Throws<InvalidOperationException>(() => guard.RequirePrepared());
        Assert.Throws<InvalidOperationException>(() => guard.GetPreparationMarkerContents());
        Assert.Throws<InvalidOperationException>(() => db.Database.OpenConnection());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.OpenConnectionAsync());
        Assert.Equal(ConnectionState.Closed, connection.State); Assert.False(File.Exists(guard.DatabasePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(guard.DatabasePath)));
    }

    [Theory]
    [InlineData("Development")] [InlineData("Production")] [InlineData("Testing")]
    public void ProtectedProfileRejectsWrongEnvironmentBeforeBuilder(string environment) =>
        Assert.Throws<InvalidOperationException>(() => VisualEnvironment.ValidateLaunch([], "1", environment, environment));

    [Fact]
    public void NormalProfilesRemainUntouchedAndProtectedProfileAcceptsOnlyMatchingSelection()
    {
        VisualEnvironment.ValidateLaunch([], null, null, "Development");
        VisualEnvironment.ValidateLaunch([], "1", VisualEnvironment.Name, VisualEnvironment.Name);
        Assert.Throws<InvalidOperationException>(() => VisualEnvironment.ValidateLaunch(["--environment", "Development"],
            "1", VisualEnvironment.Name, VisualEnvironment.Name));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("2")]
    public void MissingEmptyOrUnknownVisualConfigurationFailsWithoutCreatingAnything(string? version)
    {
        using var s = new Sandbox();
        Assert.Throws<InvalidOperationException>(() => VisualEnvironment.Configure(VisualEnvironment.Name, Configuration(version), s.Root));
        Assert.False(Directory.Exists(Path.GetDirectoryName(s.Guard.DatabasePath)));
    }

    [Theory]
    [InlineData("Development", "false")] [InlineData("Testing", "false")] [InlineData("VisualDevelopment", "true")]
    public void IncompatibleEnvironmentOrEnabledIntegrationIsRejected(string environment, string enabled)
    {
        using var s = new Sandbox();
        Assert.Throws<InvalidOperationException>(() => VisualEnvironment.Configure(environment, Configuration(enabled: enabled), s.Root));
    }

    [Theory]
    [InlineData("Igdb:ClientId")] [InlineData("Igdb:ClientSecret")]
    public void LaterConfigurationOverridesCannotIntroduceCredentials(string key)
    {
        using var s = new Sandbox(); var config = new ConfigurationBuilder().AddConfiguration(Configuration())
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = "synthetic-unwanted" }).Build();
        Assert.Throws<InvalidOperationException>(() => VisualEnvironment.Configure(VisualEnvironment.Name, config, s.Root));
    }

    [Theory]
    [InlineData("Data Source=nextgameai.db")]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=file:visual?mode=memory")]
    [InlineData("Data Source=\\\\synthetic-server\\share\\nextgameai.visual.db")]
    [InlineData("")]
    public void ArbitraryConnectionsNeverReachAnOpen(string connection)
    {
        using var s = new Sandbox();
        Assert.ThrowsAny<Exception>(() => s.Guard.ValidateConnectionString(connection));
        Assert.ThrowsAny<Exception>(() => VisualEnvironment.Configure(VisualEnvironment.Name, Configuration(connection: connection), s.Root));
        Assert.False(File.Exists(s.Guard.DatabasePath));
    }

    [Theory]
    [InlineData("Password=synthetic")]
    [InlineData("Mode=ReadWriteCreate")]
    [InlineData("Pooling=True")]
    [InlineData("Foreign Keys=False")]
    [InlineData("Cache=Shared")]
    [InlineData("Default Timeout=60")]
    public void UnexpectedSqliteOptionsAreRejected(string option)
    {
        using var s = new Sandbox();
        Assert.ThrowsAny<Exception>(() => s.Guard.ValidateConnectionString(s.Guard.ConnectionString + ";" + option));
    }

    [Fact]
    public void EquivalentCanonicalPathAndWindowsCaseAreAcceptedButSiblingAndRelativePathsAreNot()
    {
        using var s = new Sandbox(); var builder = new SqliteConnectionStringBuilder(s.Guard.ConnectionString);
        builder.DataSource = Path.Combine(Path.GetDirectoryName(s.Guard.DatabasePath)!, "..", "VisualDevelopment", VisualDatabaseGuard.FileName).ToUpperInvariant();
        s.Guard.ValidateConnectionString(builder.ToString());
        builder.DataSource = Path.Combine(s.Root, VisualDatabaseGuard.FileName);
        Assert.Throws<InvalidOperationException>(() => s.Guard.ValidateConnectionString(builder.ToString()));
        builder.DataSource = VisualDatabaseGuard.FileName;
        Assert.Throws<InvalidOperationException>(() => s.Guard.ValidateConnectionString(builder.ToString()));
    }

    [Fact]
    public void MissingDatabaseAndMissingOrMismatchedMarkerFailWithoutCreation()
    {
        using var s = new Sandbox(); Assert.Throws<InvalidOperationException>(() => s.Guard.RequirePrepared());
        Assert.False(File.Exists(s.Guard.DatabasePath));
        s.MarkSyntheticFile(); s.Guard.RequirePrepared();
        File.WriteAllText(s.Guard.DatabasePath + ".ready", "invalid");
        Assert.Throws<InvalidOperationException>(() => s.Guard.RequirePrepared());
        File.Delete(s.Guard.DatabasePath + ".ready");
        Assert.Throws<InvalidOperationException>(() => s.Guard.RequirePrepared());
    }

    [Fact]
    public void ConfigurationUsesOnlyFixedDestinationAndMarkerIsBoundToFileIdentity()
    {
        using var s = new Sandbox(); s.MarkSyntheticFile();
        var configured = VisualEnvironment.Configure(VisualEnvironment.Name, Configuration(), s.Root);
        Assert.Equal(s.Guard.DatabasePath, configured.DatabasePath);
        var builder = new SqliteConnectionStringBuilder(configured.ConnectionString);
        Assert.Equal(SqliteOpenMode.ReadWrite, builder.Mode); Assert.False(builder.Pooling);
        File.Move(s.Guard.DatabasePath, Path.Combine(s.Root, "previous.bin"));
        File.WriteAllBytes(s.Guard.DatabasePath, []);
        Assert.Throws<InvalidOperationException>(() => configured.RequirePrepared());
    }

    [Fact]
    public async Task SyncAndAsyncEfOpenAreRejectedBeforeNativeSqliteCanCreateFiles()
    {
        using var s = new Sandbox();
        using var connection = new SqliteConnection(s.Guard.ConnectionString);
        using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>()
            .UseSqlite(connection).AddInterceptors(s.Guard).Options);
        Assert.Throws<InvalidOperationException>(() => db.Database.OpenConnection());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.OpenConnectionAsync());
        Assert.Equal(ConnectionState.Closed, connection.State); Assert.False(File.Exists(s.Guard.DatabasePath));
        using var other = new SqliteConnection("Data Source=" + Path.Combine(s.Root, "forbidden.db"));
        using var wrong = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(other).AddInterceptors(s.Guard).Options);
        Assert.Throws<InvalidOperationException>(() => wrong.Database.OpenConnection());
        Assert.False(File.Exists(Path.Combine(s.Root, "forbidden.db")));
    }

    [Theory]
    [InlineData("")] [InlineData("-wal")] [InlineData("-shm")] [InlineData("-journal")] [InlineData(".ready")]
    public void ExistingHardLinkIsRejectedUsingMetadataOnly(string suffix)
    {
        using var s = new Sandbox(); s.MarkSyntheticFile(); var alias = Path.Combine(s.Root, "alias.bin");
        var target = s.Guard.DatabasePath + suffix;
        if (!File.Exists(target)) File.WriteAllBytes(target, []);
        Assert.True(CreateHardLink(alias, target, IntPtr.Zero), "Synthetic hard link creation must succeed.");
        Assert.Throws<InvalidOperationException>(() => s.Guard.RequirePrepared());
    }

    [Fact]
    public void DirectoryJunctionIsRejectedBeforeReadingTheLinkedFile()
    {
        using var s = new Sandbox(); var target = Path.Combine(s.Root, "synthetic-target"); Directory.CreateDirectory(target);
        var junction = Path.Combine(s.Root, "NextGameAI");
        var info = new ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J");
        info.ArgumentList.Add(junction); info.ArgumentList.Add(target);
        using var process = Process.Start(info)!; process.WaitForExit();
        try
        {
            Assert.Equal(0, process.ExitCode);
            Directory.CreateDirectory(Path.Combine(target, "VisualDevelopment"));
            File.WriteAllBytes(Path.Combine(target, "VisualDevelopment", VisualDatabaseGuard.FileName), []);
            Assert.Throws<InvalidOperationException>(() => s.Guard.RequirePrepared());
        }
        finally { if (Directory.Exists(junction)) Directory.Delete(junction); }
    }

    [Theory]
    [InlineData("")] [InlineData("TwitchAuthentication")] [InlineData("IgdbApiTransport")] [InlineData("OtherClient")]
    public async Task EveryFactoryClientIsBlockedEvenAfterLaterSocketRegistration(string name)
    {
        var services = new ServiceCollection(); VisualEnvironment.AddHttpBarrier(services);
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler());
        using var root = services.BuildServiceProvider();
        using var client = root.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://never-connect.invalid/"));
        Assert.Contains("HTTP externo bloqueado", error.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealServiceRegistrationsCannotSendTokensOrQueries(bool search)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Igdb:Enabled"] = "true", ["Igdb:ClientId"] = "synthetic", ["Igdb:ClientSecret"] = "synthetic" }).Build();
        var services = new ServiceCollection(); services.AddLogging();
        services.AddIgdbAuthentication(configuration); services.AddIgdbSearch(); VisualEnvironment.AddHttpBarrier(services);
        using var root = services.BuildServiceProvider();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (search) await root.GetRequiredService<IIgdbClient>().SearchAsync("Synthetic");
            else await root.GetRequiredService<IIgdbTokenProvider>().GetTokenAsync();
        });
        Assert.Contains("HTTP externo bloqueado", error.Message);
    }

    [Fact]
    public async Task ExistingTestingMemoryGuardStillAllowsOnlyItsApprovedMemoryConnection()
    {
        using var connection = new SqliteConnection(TestDatabaseGuard.ConnectionString); var guard = new TestDatabaseGuard(connection);
        using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(connection).AddInterceptors(guard).Options);
        await db.Database.OpenConnectionAsync(); Assert.Equal(ConnectionState.Open, connection.State);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr security);
}
