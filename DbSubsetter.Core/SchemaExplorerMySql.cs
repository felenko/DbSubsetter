using System.Data;
using MySqlConnector;

namespace DbSubsetter.Core;

public class SchemaExplorerMySql : ISchemaExplorer
{
    public async Task<bool> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        await using var cn = new MySqlConnection(connectionString);
        await cn.OpenAsync(ct);
        return true;
    }

    public async Task<List<string>> GetTablesAsync(string connectionString, CancellationToken ct = default)
    {
        const string sql = "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME";
        await using var cn = new MySqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await rdr.ReadAsync(ct))
            tables.Add(rdr.GetString(0));
        return tables;
    }

    public async Task<string?> GetPrimaryKeyColumnAsync(string connectionString, string table, CancellationToken ct = default)
    {
        var (schema, name) = ParseTable(table);
        var sql = """
            SELECT COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = COALESCE(@schema, DATABASE()) AND TABLE_NAME = @table
            AND CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY ORDINAL_POSITION LIMIT 1
            """;
        await using var cn = new MySqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@table", name);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o?.ToString();
    }

    public async Task<List<RootRowCandidate>> GetSampleRowsAsync(string connectionString, string table, string pkColumn, int limit = 200, CancellationToken ct = default)
    {
        var (schema, name) = ParseTable(table);
        var tbl = schema != null ? $"`{schema}`.`{name}`" : $"`{name}`";
        var sql = $"SELECT `{pkColumn.Replace("`", "``")}` FROM {tbl} ORDER BY `{pkColumn.Replace("`", "``")}` DESC LIMIT {limit}";
        await using var cn = new MySqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<RootRowCandidate>();
        while (await rdr.ReadAsync(ct))
        {
            var val = rdr.GetValue(0);
            list.Add(new RootRowCandidate(val?.ToString() ?? "", val?.ToString() ?? ""));
        }
        return list;
    }

    public async Task<List<ColumnInfo>> GetTableColumnsAsync(string connectionString, string table, CancellationToken ct = default)
    {
        var (schema, name) = ParseTable(table);
        var schemaCond = schema != null ? "AND c.TABLE_SCHEMA = @schema" : "AND c.TABLE_SCHEMA = DATABASE()";
        var sql = $"""
            SELECT c.COLUMN_NAME, c.DATA_TYPE, CASE WHEN k.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS is_pk,
                   fk.REFERENCED_TABLE_NAME, fk.REFERENCED_COLUMN_NAME
            FROM information_schema.COLUMNS c
            LEFT JOIN information_schema.KEY_COLUMN_USAGE k ON k.TABLE_SCHEMA = c.TABLE_SCHEMA AND k.TABLE_NAME = c.TABLE_NAME AND k.CONSTRAINT_NAME = 'PRIMARY' AND k.COLUMN_NAME = c.COLUMN_NAME
            LEFT JOIN information_schema.KEY_COLUMN_USAGE fk ON fk.TABLE_SCHEMA = c.TABLE_SCHEMA AND fk.TABLE_NAME = c.TABLE_NAME AND fk.COLUMN_NAME = c.COLUMN_NAME AND fk.REFERENCED_TABLE_NAME IS NOT NULL
            WHERE c.TABLE_NAME = @table {schemaCond}
            ORDER BY c.ORDINAL_POSITION
            """;
        await using var cn = new MySqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@table", name);
        if (schema != null) cmd.Parameters.AddWithValue("@schema", schema);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ColumnInfo>();
        while (await rdr.ReadAsync(ct))
        {
            var isPk = rdr.GetInt32(2) == 1;
            string? fkTable = rdr.IsDBNull(3) ? null : rdr.GetString(3);
            string? fkCol = rdr.IsDBNull(4) ? null : rdr.GetString(4);
            list.Add(new ColumnInfo(rdr.GetString(0), rdr.GetString(1), isPk, fkTable, fkCol));
        }
        return list;
    }

    public async Task<DataTable> GetTableRowsAsync(string connectionString, string table, int limit, string? whereClause = null, CancellationToken ct = default)
    {
        var (schema, name) = ParseTable(table);
        var tbl = schema != null ? $"`{schema}`.`{name}`" : $"`{name}`";
        var where = string.IsNullOrWhiteSpace(whereClause) ? "" : " WHERE " + whereClause;
        var sql = $"SELECT * FROM {tbl}{where} LIMIT {limit}";
        await using var cn = new MySqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var dt = new DataTable();
        await Task.Run(() => dt.Load(rdr), ct);
        return dt;
    }

    public async Task<DataTable> ExecuteQueryAsync(string connectionString, string sql, CancellationToken ct = default)
    {
        await using var cn = new MySqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new MySqlCommand(sql, cn) { CommandTimeout = 30 };
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var dt = new DataTable();
        await Task.Run(() => dt.Load(rdr), ct);
        return dt;
    }

    static (string? schema, string name) ParseTable(string table)
    {
        var parts = table.Replace("`", "").Split('.');
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, table.Replace("`", ""));
    }
}
