using System.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Data.Sqlite;
using NextGameAI.Web.Data;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Tests;

public sealed class FixtureIsolationTests
{
    [Trait("Host", "None")]
    [Fact]
    public void DatabaseGuardAcceptsOnlyApprovedPrivateMemoryConnectionBeforeOpen()
    {
        using var approved = new SqliteConnection(TestDatabaseGuard.ConnectionString);
        var guard = new TestDatabaseGuard(approved);
        guard.Validate(approved);
        using var otherMemory = new SqliteConnection(TestDatabaseGuard.ConnectionString);
        using var file = new SqliteConnection("Data Source=must-never-open.db");
        Assert.Throws<InvalidOperationException>(() => guard.Validate(otherMemory));
        Assert.Throws<InvalidOperationException>(() => guard.Validate(file));
        approved.ConnectionString = "Data Source=must-never-open.db";
        Assert.Throws<InvalidOperationException>(() => guard.Validate(approved));
        Assert.Equal(ConnectionState.Closed, approved.State);
        Assert.Equal(ConnectionState.Closed, file.State);
    }

    [Trait("Host", "None")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionInterceptorRejectsUnapprovedConnectionBeforeNativeOpen(bool asynchronous)
    {
        using var approved = new SqliteConnection(TestDatabaseGuard.ConnectionString);
        using var unapproved = new SqliteConnection(TestDatabaseGuard.ConnectionString);
        using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>()
            .UseSqlite(unapproved).AddInterceptors(new TestDatabaseGuard(approved)).Options);
        if (asynchronous)
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.Database.OpenConnectionAsync());
        else
            Assert.Throws<InvalidOperationException>(() => db.Database.OpenConnection());
        Assert.Equal(ConnectionState.Closed, unapproved.State);
    }

    [Trait("Host", "None")]
    [Fact]
    public void SyntheticConfigurationReplacesExternalValuesAndLeavesCredentialsAbsent()
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Igdb:ClientId"] = "synthetic-unwanted",
            ["Igdb:ClientSecret"] = "synthetic-unwanted",
            ["ConnectionStrings:Library"] = "Data Source=must-never-open.db",
            ["UnexpectedSetting"] = "synthetic-unwanted"
        });
        TestHostConfiguration.Apply(config, AppContext.BaseDirectory);
        Assert.Single(config.Sources);
        Assert.Null(config["Igdb:ClientId"]);
        Assert.Null(config["Igdb:ClientSecret"]);
        Assert.Null(config["UnexpectedSetting"]);
        Assert.Equal(TestDatabaseGuard.ConnectionString, config.GetConnectionString("Library"));
        Assert.Equal("Testing", config[HostDefaults.EnvironmentKey]);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    [Trait("Host", "None")]
    [Theory]
    [InlineData("")]
    [InlineData("TwitchAuthentication")]
    [InlineData("IgdbApiTransport")]
    [InlineData("UnregisteredOutboundClient")]
    public async Task FactoryBlocksEveryUnsimulatedClientWithoutNetwork(string name)
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        services.Insert(0, ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, TestHttpBarrier>());
        using var root = services.BuildServiceProvider();
        using var http = root.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => http.GetAsync("https://never-connect.invalid/"));
        Assert.Equal("HTTP de saída bloqueado: registre um handler simulado no teste.", error.Message);
    }

    [Trait("Host", "None")]
    [Fact]
    public async Task ExplicitFakeHandlerWorksAndCannotBeReplacedByLaterSocketRegistration()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("simulated").ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler());
        services.AddSimulatedHttp("simulated", () => new FakeHandler());
        services.Insert(0, ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, TestHttpBarrier>());
        using var root = services.BuildServiceProvider();
        using var http = root.GetRequiredService<IHttpClientFactory>().CreateClient("simulated");
        using var response = await http.GetAsync("https://never-connect.invalid/");
        Assert.True(response.IsSuccessStatusCode);
    }

    [Trait("Host", "Mvc")]
    [Fact]
    public void HostUsesTestingSyntheticConfigurationAndOnlyMemoryMigrations()
    {
        using var app = new LibraryApplication();
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        Assert.Equal("Testing", services.GetRequiredService<IWebHostEnvironment>().EnvironmentName);
        var config = services.GetRequiredService<IConfiguration>();
        Assert.Null(config["Igdb:ClientId"]);
        Assert.Null(config["Igdb:ClientSecret"]);
        Assert.Equal(TestDatabaseGuard.ConnectionString, config.GetConnectionString("Library"));
        Assert.False(services.GetRequiredService<IIgdbTokenProvider>().IsConfigured);
        var db = services.GetRequiredService<NextGameAiDbContext>();
        var connection = Assert.IsType<SqliteConnection>(db.Database.GetDbConnection());
        new TestDatabaseGuard(connection).Validate(db);
        Assert.Equal(3, db.Database.GetAppliedMigrations().Count());
        Assert.Empty(db.Games);
    }
}
