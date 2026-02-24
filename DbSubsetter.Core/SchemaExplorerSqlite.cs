using System.Data;
using Microsoft.Data.Sqlite;

namespace DbSubsetter.Core;

public class SchemaExplorerSqlite : ISchemaExplorer
{
    public async Task<bool> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(connectionString);
        await cn.OpenAsync(ct);
        return true;
    }

    /// <summary>Returns table names as they appear in SQLite (single name, no schema).</summary>
    public async Task<List<string>> GetTablesAsync(string connectionString, CancellationToken ct = default)
    {
        const string sql = """
            SELECT name FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;
        await using var cn = new SqliteConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await rdr.ReadAsync(ct))
            tables.Add(rdr.GetString(0));
        return tables;
    }

    /// <summary>Returns the first column of the primary key (SQLite has rowid but tables can have PK).</summary>
    public async Task<string?> GetPrimaryKeyColumnAsync(string connectionString, string table, CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(connectionString);
        await cn.OpenAsync(ct);
        var quoted = QuoteId(table);
        await using var cmd = new SqliteCommand($"PRAGMA table_info({quoted})", cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var pk = rdr.GetInt64(5); // pk column
            if (pk >= 1)
                return rdr.GetString(1); // name
        }
        return null;
    }

    public async Task<List<RootRowCandidate>> GetSampleRowsAsync(
        string connectionString,
        string table,
        string pkColumn,
        int limit = 200,
        CancellationToken ct = default)
    {
        var quotedTable = QuoteId(table);
        var quotedPk = QuoteId(pkColumn);
        var sql = $"SELECT {quotedPk} FROM {quotedTable} ORDER BY {quotedPk} DESC LIMIT {limit}";
        await using var cn = new SqliteConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<RootRowCandidate>();
        while (await rdr.ReadAsync(ct))
        {
            var val = rdr.GetValue(0);
            string pkVal = val?.ToString() ?? "";
            list.Add(new RootRowCandidate(pkVal, pkVal));
        }
        return list;
    }

    public async Task<List<ColumnInfo>> GetTableColumnsAsync(
        string connectionString,
        string table,
        CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(connectionString);
        await cn.OpenAsync(ct);
        var quoted = QuoteId(table);
        await using var cmd = new SqliteCommand($"PRAGMA table_info({quoted})", cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var cols = new List<(string Name, string Type, bool Pk)>();
        while (await rdr.ReadAsync(ct))
            cols.Add((rdr.GetString(1), rdr.GetString(2) ?? "", rdr.GetInt64(5) >= 1));

        var fkMap = new Dictionary<string, (string Table, string Column)>(StringComparer.OrdinalIgnoreCase);
        await using (var fkCmd = new SqliteCommand($"PRAGMA foreign_key_list({quoted})", cn))
        await using (var fkRdr = await fkCmd.ExecuteReaderAsync(ct))
            while (await fkRdr.ReadAsync(ct))
                fkMap[fkRdr.GetString(3)] = (fkRdr.GetString(2), fkRdr.GetString(4)); // from -> (table, to)

        return cols.Select(c => new ColumnInfo(
            c.Name,
            c.Type,
            c.Pk,
            fkMap.TryGetValue(c.Name, out var fk) ? fk.Table : null,
            fkMap.TryGetValue(c.Name, out fk) ? fk.Column : null)).ToList();
    }

    public async Task<DataTable> GetTableRowsAsync(
        string connectionString,
        string table,
        int limit,
        string? whereClause = null,
        CancellationToken ct = default)
    {
        var where = string.IsNullOrWhiteSpace(whereClause) ? "" : " WHERE " + whereClause;
        var sql = $"SELECT * FROM {QuoteId(table)}{where} LIMIT {limit}";
        await using var cn = new SqliteConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var dt = new DataTable();
        await Task.Run(() => dt.Load(rdr), ct);
        return dt;
    }

    internal static string QuoteId(string id)
    {
        return "\"" + id.Replace("\"", "\"\"") + "\"";
    }
}
