using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using NextGameAI.Web.Data;

namespace NextGameAI.Web.Tests;

public sealed class LibraryApplication : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new(TestDatabaseGuard.ConnectionString);

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // DeferredHostBuilder puts host settings in entry-point args before CreateBuilder runs.
        // ConfigureWebHost alone would change the environment too late to prevent initial secrets loading.
        builder.UseEnvironment("Testing");
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((context, config) =>
        {
            if (context.HostingEnvironment.EnvironmentName != "Testing")
                throw new InvalidOperationException("O host de testes exige o ambiente Testing.");
            TestHostConfiguration.Apply(config, context.HostingEnvironment.ContentRootPath);
        });
        builder.ConfigureServices(services =>
        {
            var guard = new TestDatabaseGuard(connection);
            services.RemoveAll<NextGameAiDbContext>();
            services.RemoveAll<DbContextOptions<NextGameAiDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NextGameAiDbContext>>();
            services.AddDbContext<NextGameAiDbContext>(options =>
                options.UseSqlite(connection).AddInterceptors(guard));
            services.RemoveAll<NextGameAiDbContext>();
            services.AddScoped(sp =>
            {
                var db = new NextGameAiDbContext(sp.GetRequiredService<DbContextOptions<NextGameAiDbContext>>());
                try { guard.Validate(db); return db; }
                catch { db.Dispose(); throw; }
            });

            // First filter runs last after next(): later HTTP registrations cannot bypass the barrier.
            services.Insert(0, ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, TestHttpBarrier>());

            using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>()
                .UseSqlite(connection).AddInterceptors(guard).Options);
            guard.Validate(db);
            // Validate before the first native SQLite open, including this manually held connection.
            guard.Validate(connection);
            connection.Open();
            db.Database.Migrate();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }
}

internal static class TestHostConfiguration
{
    internal static void Apply(IConfigurationBuilder config, string contentRoot)
    {
        config.Sources.Clear();
        config.AddInMemoryCollection(Values(contentRoot));
    }

    internal static Dictionary<string, string?> Values(string contentRoot) => new()
    {
        [HostDefaults.EnvironmentKey] = "Testing",
        [HostDefaults.ApplicationKey] = typeof(Program).Assembly.GetName().Name,
        [HostDefaults.ContentRootKey] = contentRoot,
        ["ConnectionStrings:Library"] = TestDatabaseGuard.ConnectionString,
        ["Igdb:Enabled"] = "true",
        ["Igdb:ClientId"] = null,
        ["Igdb:ClientSecret"] = null,
        ["Igdb:TimeoutSeconds"] = "10",
        ["Logging:LogLevel:Default"] = "Warning",
        ["AllowedHosts"] = "*"
    };
}
