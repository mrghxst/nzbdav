using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NzbWebDAV.Database.Interceptors;

/// <summary>
/// Applies the SQLite PRAGMAs that NzbDav relies on every time a connection is opened:
/// <list type="bullet">
///   <item><c>foreign_keys=ON</c> — enforce the relational constraints declared in the model.</item>
///   <item><c>journal_mode=WAL</c> — allow readers (WebDAV streaming, the UI) to proceed
///         concurrently with the single writer (queue/health/cleanup services), instead of
///         serializing behind the default rollback journal's database-wide lock.</item>
///   <item><c>busy_timeout</c> — wait for a contended write lock rather than failing
///         immediately with <c>SQLITE_BUSY</c>.</item>
///   <item><c>synchronous=NORMAL</c> — the recommended durability/throughput trade-off under WAL.</item>
/// </list>
/// WAL is persisted on the database file once set, but re-applying it on each open is
/// idempotent and keeps the behavior explicit regardless of how the file was created.
/// </summary>
public class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const int BusyTimeoutMs = 5000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        command.ExecuteNonQuery();
    }

    private static readonly string PragmaSql = $"""
        PRAGMA foreign_keys = ON;
        PRAGMA journal_mode = WAL;
        PRAGMA busy_timeout = {BusyTimeoutMs};
        PRAGMA synchronous = NORMAL;
        """;
}
