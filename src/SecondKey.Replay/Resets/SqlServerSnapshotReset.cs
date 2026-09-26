using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SecondKey.Artifacts.Runs;

namespace SecondKey.Replay.Resets;

/// <summary>
/// Restores a SQL Server database from a database snapshot before every scenario (S11). The
/// snapshot is created once, from the database as it is when the replay starts — so every
/// scenario on this side starts from that exact state. Database snapshots exist in every
/// SQL Server edition since 2016 SP1.
/// </summary>
public sealed class SqlServerSnapshotReset : IStateReset
{
    private readonly string _master;
    private readonly string _database;
    private readonly string _snapshot;
    private bool _ensured;

    public SqlServerSnapshotReset(string connectionString, string database, string snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot);
        _master = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        _database = database;
        _snapshot = snapshot;
    }

    public ResetMethod Method => ResetMethod.SqlServerSnapshot;

    /// <summary>Creates the snapshot unless it already exists. Called by the first reset.</summary>
    public async Task EnsureSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var exists = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @snapshot AND source_database_id = DB_ID(@database)", connection))
        {
            exists.Parameters.AddWithValue("@snapshot", _snapshot);
            exists.Parameters.AddWithValue("@database", _database);
            if ((int)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0)
            {
                _ensured = true;
                return;
            }
        }

        var files = new List<(string Name, string Path)>();
        await using (var list = new SqlCommand("SELECT name, physical_name FROM sys.master_files WHERE database_id = DB_ID(@database) AND type = 0", connection))
        {
            list.Parameters.AddWithValue("@database", _database);
            await using var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                files.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        if (files.Count == 0)
        {
            throw new InvalidOperationException($"database '{_database}' was not found or has no data files");
        }

        var sql = new StringBuilder($"CREATE DATABASE {Quote(_snapshot)} ON ");
        sql.AppendJoin(", ", files.Select(f => $"(NAME = {Quote(f.Name)}, FILENAME = {Literal(SnapshotPath(f.Path, f.Name))})"));
        sql.Append(CultureInfo.InvariantCulture, $" AS SNAPSHOT OF {Quote(_database)}");
        await using var create = new SqlCommand(sql.ToString(), connection);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _ensured = true;
    }

    public async Task<ResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (!_ensured)
            {
                await EnsureSnapshotAsync(cancellationToken).ConfigureAwait(false);
            }

            // Our own pooled connections to the database would break on the restore; drop them first.
            SqlConnection.ClearAllPools();
            await using var connection = new SqlConnection(_master);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var restore = $"""
                ALTER DATABASE {Quote(_database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                RESTORE DATABASE {Quote(_database)} FROM DATABASE_SNAPSHOT = {Literal(_snapshot)};
                ALTER DATABASE {Quote(_database)} SET MULTI_USER;
                """;
            await using var command = new SqlCommand(restore, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new ResetResult(true, clock.Elapsed.TotalMilliseconds, null);
        }
        catch (SqlException ex)
        {
            return new ResetResult(false, clock.Elapsed.TotalMilliseconds, $"restoring '{_database}' from '{_snapshot}' failed: {ex.Message}");
        }
    }

    /// <summary>Drops the snapshot, e.g. after a replay; the database itself is untouched.</summary>
    public async Task DropSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new SqlCommand($"IF DB_ID({Literal(_snapshot)}) IS NOT NULL DROP DATABASE {Quote(_snapshot)}", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _ensured = false;
    }

    internal static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal static string Literal(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private string SnapshotPath(string dataFile, string logicalName)
    {
        var separator = dataFile.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        var directory = dataFile[..(dataFile.LastIndexOf(separator) + 1)];
        return $"{directory}{_snapshot}_{logicalName}.ss";
    }
}
