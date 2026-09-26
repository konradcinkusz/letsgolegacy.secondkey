using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using SecondKey.Artifacts.Runs;

namespace SecondKey.Replay.Probes;

/// <summary>The rows of the watched tables at one moment, keyed by primary key.</summary>
public sealed class TableSnapshot
{
    internal TableSnapshot(Dictionary<string, Dictionary<string, JsonObject>> rows, Dictionary<string, IReadOnlyList<string>> keys)
    {
        Rows = rows;
        Keys = keys;
    }

    internal Dictionary<string, Dictionary<string, JsonObject>> Rows { get; }

    internal Dictionary<string, IReadOnlyList<string>> Keys { get; }
}

/// <summary>
/// Watches named SQL Server tables and reports what one request changed in them — rows
/// inserted, deleted and updated — by comparing the tables before and after. Meant for the
/// small, seeded databases of a verification sandbox, not for production-sized tables.
/// </summary>
public sealed class SqlServerTableProbe
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<string> _tables;

    public SqlServerTableProbe(string connectionString, IReadOnlyList<string> tables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(tables);
        _connectionString = connectionString;
        _tables = tables;
    }

    public async Task<TableSnapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.Ordinal);
        var keys = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        foreach (var table in _tables)
        {
            var key = await PrimaryKeyAsync(connection, table, cancellationToken).ConfigureAwait(false);
            keys[table] = key;
            var tableRows = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            await using var command = new SqlCommand($"SELECT * FROM {QuoteTable(table)}", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = ToJson(reader.GetValue(i));
                }

                var id = key.Count > 0 ? new JsonArray(key.Select(k => row[k]?.DeepClone()).ToArray()).ToJsonString() : row.ToJsonString();
                tableRows[id] = row;
            }

            rows[table] = tableRows;
        }

        return new TableSnapshot(rows, keys);
    }

    /// <summary>What changed between two snapshots, per table; tables without changes are left out.</summary>
    public static DbDelta? Diff(TableSnapshot before, TableSnapshot after, string probeName = "sqlserver-tables")
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var tables = new SortedDictionary<string, TableDelta>(StringComparer.Ordinal);
        foreach (var (table, afterRows) in after.Rows)
        {
            var beforeRows = before.Rows.GetValueOrDefault(table) ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var key = after.Keys.GetValueOrDefault(table) ?? [];
            var inserted = afterRows.Where(r => !beforeRows.ContainsKey(r.Key)).Select(r => r.Value).ToList();
            var deleted = beforeRows.Where(r => !afterRows.ContainsKey(r.Key)).Select(r => r.Value).ToList();
            var updated = afterRows
                .Where(r => beforeRows.TryGetValue(r.Key, out var old) && !JsonNode.DeepEquals(old, r.Value))
                .Select(r => new RowUpdate
                {
                    Key = KeyOf(r.Value, key),
                    Before = beforeRows[r.Key],
                    After = r.Value,
                })
                .ToList();
            if (inserted.Count + deleted.Count + updated.Count > 0)
            {
                tables[table] = new TableDelta { Inserted = inserted, Deleted = deleted, Updated = updated };
            }
        }

        return tables.Count == 0 ? null : new DbDelta { Probe = probeName, Tables = tables };
    }

    internal static JsonNode? ToJson(object value) => value switch
    {
        DBNull => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        byte or short or int or long => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        decimal d => JsonValue.Create(d),
        float or double => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        DateTime dt => JsonValue.Create(dt.ToString("o", CultureInfo.InvariantCulture)),
        DateTimeOffset dto => JsonValue.Create(dto.ToString("o", CultureInfo.InvariantCulture)),
        Guid g => JsonValue.Create(g.ToString("D")),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    internal static string QuoteTable(string table) =>
        string.Join('.', table.Split('.').Select(Resets.SqlServerSnapshotReset.Quote));

    private static JsonObject KeyOf(JsonObject row, IReadOnlyList<string> key)
    {
        var result = new JsonObject();
        foreach (var column in key)
        {
            result[column] = row[column]?.DeepClone();
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> PrimaryKeyAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@table)
            ORDER BY ic.key_ordinal
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@table", table);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}
