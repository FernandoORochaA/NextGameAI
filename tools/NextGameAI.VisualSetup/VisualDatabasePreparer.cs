using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using NextGameAI.Web.Data;
using NextGameAI.Web.VisualDevelopment;

namespace NextGameAI.VisualSetup;

internal enum PreparationStage { Created, Migrating, Migrated, Seeding, Seeded, Verifying, Verified, Publishing, MigrationSql }

public sealed class VisualDatabasePreparer
{
    private readonly VisualDatabaseGuard guard;
    private readonly TimeProvider clock;
    private readonly Func<PreparationStage, CancellationToken, Task>? observe;
    private VisualDatabasePreparer(VisualDatabaseGuard guard, TimeProvider clock,
        Func<PreparationStage, CancellationToken, Task>? observe = null)
    { this.guard = guard; this.clock = clock; this.observe = observe; }

    public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        ValidateIntent(args, Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
        cancellationToken.ThrowIfCancellationRequested();
        // No host, configuration discovery, secrets, HTTP registrations or MVC entry point.
        var guard = new VisualDatabaseGuard(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Console.WriteLine("Preparação exclusivamente sintética: NextGameAI/VisualDevelopment/nextgameai.visual.db sob os dados locais. Sem sobrescrita.");
        await new VisualDatabasePreparer(guard, TimeProvider.System, (stage, _) =>
        { if (stage != PreparationStage.MigrationSql) Console.WriteLine($"Etapa: {stage}"); return Task.CompletedTask; }).PrepareAsync(cancellationToken);
    }

    internal static void ValidateIntent(string[] args, string? dotnet, string? aspnet)
    {
        if (!args.SequenceEqual(new[] { "prepare-synthetic", "--confirm-synthetic-only" }) ||
            dotnet != VisualEnvironment.Name || aspnet != VisualEnvironment.Name)
            throw new InvalidOperationException("Exige intenção sintética explícita e ambiente visual. Caminhos e opções adicionais não são aceitos.");
    }

    internal static VisualDatabasePreparer ForTests(string root, TimeProvider clock,
        Func<PreparationStage, CancellationToken, Task>? observe = null)
    {
        var full = Path.GetFullPath(root);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(root) || !full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("NextGameAI-setup-test-", StringComparison.Ordinal) ||
            !Directory.Exists(full)) throw new InvalidOperationException("Somente raiz temporária sintética exclusiva.");
        return new(new VisualDatabaseGuard(full), clock, observe);
    }

    internal async Task PrepareAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var lease = guard.BeginPreparation();
        var instant = clock.GetUtcNow().UtcDateTime;
        var demo = VisualDemo.Create(instant);
        var expected = VisualDemo.Serialize(demo); // Capture before EF relationship fixup.
        async Task Stage(PreparationStage stage)
        { ct.ThrowIfCancellationRequested(); if (observe is not null) await observe(stage, ct); ct.ThrowIfCancellationRequested(); lease.Check(); }
        await Stage(PreparationStage.Created);
        var migrationProbe = new MigrationProbe(() => Stage(PreparationStage.MigrationSql));
        await using (var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>()
            .UseSqlite(guard.ConnectionString).AddInterceptors(lease, migrationProbe).Options))
        {
            await Stage(PreparationStage.Migrating);
            await db.Database.MigrateAsync(ct);
            migrationProbe.Active = false;
            await Stage(PreparationStage.Migrated);
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await Stage(PreparationStage.Seeding);
                VisualDemo.Add(db, demo);
                await db.SaveChangesAsync(ct);
                await Stage(PreparationStage.Seeded);
                await transaction.CommitAsync(ct);
            }
            db.ChangeTracker.Clear();
            await Stage(PreparationStage.Verifying);
            var migrations = db.Database.GetMigrations().ToArray();
            string[] required = ["20261022004238_InitialLibrary", "20261024195821_IgdbCatalogMetadata", "20261024220151_AddCatalogEnrichmentMetadata"];
            if (!migrations.SequenceEqual(required) || !(await db.Database.GetAppliedMigrationsAsync(ct)).SequenceEqual(required) ||
                (await db.Database.GetPendingMigrationsAsync(ct)).Any() || await VisualDemo.ReadSnapshotAsync(db, ct) != expected)
                throw new InvalidOperationException("Esquema ou demonstração não corresponde ao contrato.");
            await db.Database.OpenConnectionAsync(ct);
            try
            {
                foreach (var pragma in new[] { "PRAGMA foreign_key_check;", "PRAGMA integrity_check;" })
                {
                    await using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = pragma;
                    await using var reader = await command.ExecuteReaderAsync(ct);
                    if (pragma.Contains("foreign_key"))
                    { if (await reader.ReadAsync(ct)) throw new InvalidOperationException("Violação referencial na demonstração."); }
                    else if (!await reader.ReadAsync(ct) || reader.GetString(0) != "ok" || await reader.ReadAsync(ct))
                        throw new InvalidOperationException("Integridade SQLite recusada.");
                }
            }
            finally { await db.Database.CloseConnectionAsync(); }
            await Stage(PreparationStage.Verified);
        }
        // No context, transaction or native SQLite connection survives marker publication.
        await Stage(PreparationStage.Publishing);
        lease.Publish(ct);
    }

    // Controlled fault boundary during actual migration SQL, never logs commands or data.
    private sealed class MigrationProbe(Func<Task> beforeCommand) : DbCommandInterceptor
    {
        public bool Active = true;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (Active) await beforeCommand(); return result; }
    }
}
