using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NextGameAI.Web.Data;

namespace NextGameAI.Web.Tests;

internal sealed class TestDatabaseGuard(SqliteConnection approved) : DbConnectionInterceptor
{
    internal const string ConnectionString = "Data Source=:memory:;Foreign Keys=True";

    internal void Validate(DbConnection connection)
    {
        // Read options only. Never open or inspect an unapproved file to validate it.
        if (!ReferenceEquals(connection, approved) || connection is not SqliteConnection sqlite ||
            new SqliteConnectionStringBuilder(sqlite.ConnectionString) is not
                { DataSource: ":memory:", ForeignKeys: true, Mode: SqliteOpenMode.ReadWriteCreate, Cache: SqliteCacheMode.Default })
            throw new InvalidOperationException("Conexão recusada: os testes exigem a conexão SQLite privada em memória.");
    }

    internal void Validate(NextGameAiDbContext db)
    {
        if (!db.Database.IsSqlite())
            throw new InvalidOperationException("O contexto de testes deve utilizar SQLite.");
        Validate(db.Database.GetDbConnection());
    }

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result)
    {
        Validate(connection);
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
        ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        Validate(connection);
        return ValueTask.FromResult(result);
    }
}
