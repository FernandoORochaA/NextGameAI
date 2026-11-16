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
        await RunOperationAsync(args, Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            () => new VisualDatabaseGuard(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Console.Out, cancellationToken);
    }

    internal static async Task RunOperationAsync(string[] args, string? dotnet, string? aspnet,
        Func<VisualDatabaseGuard> guardFactory, TextWriter output, CancellationToken ct,
        Func<string, long>? availableSpace = null)
    {
        ValidateIntent(args, dotnet, aspnet);
        ct.ThrowIfCancellationRequested();
        await output.WriteLineAsync("Ambiente: VisualDevelopment válido.");
        var guard = guardFactory(); // Resolved only after intent validation. Never loads host/configuration/secrets.
        await output.WriteLineAsync($"Destino: {guard.DatabasePath}");
        if (args[0] == "check-synthetic")
        {
            var result = guard.CheckPreparation(availableSpace);
            await output.WriteLineAsync("Armazenamento: local fixo; ancestrais existentes validados.");
            await output.WriteLineAsync(result.MissingDirectories
                ? "Diretórios: ausentes, criáveis em princípio; nenhuma criação realizada."
                : "Diretórios: existentes e válidos.");
            await output.WriteLineAsync($"Espaço disponível: {result.AvailableBytes} bytes (sem garantia de suficiência futura).");
            await output.WriteLineAsync("A pré-checagem não reserva o destino, não garante permissões de escrita e não autoriza preparação. Todas as guardas serão repetidas.");
            if (result.Conflicts.Length != 0)
                throw new OperationalFailure("Conflitos: " + string.Join(", ", result.Conflicts) + ". Nenhum artefato foi alterado.");
            ct.ThrowIfCancellationRequested();
            await output.WriteLineAsync("Condições preliminares satisfeitas; nenhum conflito identificado.");
            return;
        }
        await new VisualDatabasePreparer(guard, TimeProvider.System, async (stage, _) =>
        { if (stage != PreparationStage.MigrationSql) await output.WriteLineAsync($"Etapa: {stage}"); }).PrepareAsync(ct);
    }

    internal static void ValidateIntent(string[] args, string? dotnet, string? aspnet)
    {
        if (dotnet != VisualEnvironment.Name || aspnet != VisualEnvironment.Name)
            throw new OperationalFailure("Ambiente inválido: DOTNET_ENVIRONMENT e ASPNETCORE_ENVIRONMENT devem ser VisualDevelopment.");
        if (!args.SequenceEqual(new[] { "prepare-synthetic", "--confirm-synthetic-only" }) &&
            !args.SequenceEqual(new[] { "check-synthetic" }))
            throw new OperationalFailure("Argumentos inválidos. Use check-synthetic ou prepare-synthetic --confirm-synthetic-only; opções adicionais são recusadas.");
    }

    internal static VisualDatabasePreparer ForTests(string root, TimeProvider clock,
        Func<PreparationStage, CancellationToken, Task>? observe = null,
        Action<VisualDatabaseGuard.PublicationPoint>? publicationProbe = null)
    {
        var full = Path.GetFullPath(root);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!Path.IsPathFullyQualified(root) || !full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("NextGameAI-setup-test-", StringComparison.Ordinal) ||
            !Directory.Exists(full)) throw new InvalidOperationException("Somente raiz temporária sintética exclusiva.");
        return new(new VisualDatabaseGuard(full) { PublicationProbe = publicationProbe }, clock, observe);
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
