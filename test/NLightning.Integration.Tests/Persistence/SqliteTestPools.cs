using Microsoft.Data.Sqlite;

namespace NLightning.Integration.Tests.Persistence;

/// <summary>
/// Clears the connection pool of one test database file before the test deletes it (NL-747).
/// </summary>
/// <remarks>
/// Never <see cref="SqliteConnection.ClearAllPools"/> in a test: test classes run in parallel, and clearing every pool
/// disposes the idle connections of other tests' databases while those tests may be opening them, which fails their
/// <c>Open</c> with <see cref="ObjectDisposedException"/> on the <c>sqlite3</c> handle (seen as
/// <c>Bolt11BlindedInvoiceTests</c> failing its migration in a loaded full run). The pool is keyed by the connection
/// string, so this must be the string the test's node uses: <c>Data Source=&lt;path&gt;</c>.
/// </remarks>
internal static class SqliteTestPools
{
    public static void Clear(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        SqliteConnection.ClearPool(connection);
    }
}