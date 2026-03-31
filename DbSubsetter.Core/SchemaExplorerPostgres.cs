using System.Data;
using Npgsql;

namespace DbSubsetter.Core;

public class SchemaExplorerPostgres : ISchemaExplorer
{
    public async Task<bool> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        await using var cn = new NpgsqlConnection(connectionString);
        await cn.OpenAsync(ct);
        return true;
    }

    public async Task<List<string>> GetTablesAsync(string connectionString, CancellationToken ct = default)
    {
        const string sql = """
            SELECT table_schema || '.' || table_name
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND table_type = 'BASE TABLE'
            ORDER BY table_schema, table_name
            """;
        await using var cn = new NpgsqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await rdr.ReadAsync(ct))
            tables.Add(rdr.GetString(0));
        return tables;
    }

    public async Task<string?> GetPrimaryKeyColumnAsync(string connectionString, string table, CancellationToken ct = default)
    {
        var (schema, name) = ParseTable(table);
        const string sql = """
            SELECT a.attname FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey) AND a.attnum > 0 AND NOT a.attisdropped
            JOIN pg_class c ON c.oid = i.indrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE i.indisprimary AND c.relname = @name AND n.nspname = COALESCE(@schema, 'public')
            ORDER BY array_position(i.indkey, a.attnum) LIMIT 1
            """;
        await using var cn = new NpgsqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("schema", (object?)schema ?? DBNull.Value);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o?.ToString();
    }

    public async Task<List<RootRowCandidate>> GetSampleRowsAsync(string connectionString, string table, string pkColumn, int limit = 200, CancellationToken ct = default)
    {
        var quoted = QuoteId(table);
        var pkQuoted = QuoteId(pkColumn);
        var sql = $"SELECT {pkQuoted} FROM {quoted} ORDER BY {pkQuoted} DESC LIMIT {limit}";
        await using var cn = new NpgsqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, cn);
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
        const string sql = """
            SELECT c.column_name, c.data_type,
                   EXISTS (SELECT 1 FROM information_schema.table_constraints tc
                           JOIN information_schema.key_column_usage k ON tc.constraint_name = k.constraint_name AND tc.table_schema = k.table_schema AND tc.table_name = k.table_name
                           WHERE tc.table_schema = c.table_schema AND tc.table_name = c.table_name AND tc.constraint_type = 'PRIMARY KEY' AND k.column_name = c.column_name) AS is_pk
            FROM information_schema.columns c
            WHERE c.table_schema = COALESCE(@schema, 'public') AND c.table_name = @name
            ORDER BY c.ordinal_position
            """;
        await using var cn = new NpgsqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("schema", (object?)schema ?? DBNull.Value);
        cmd.Parameters.AddWithValue("name", name);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var list = new List<ColumnInfo>();
        while (await rdr.ReadAsync(ct))
            list.Add(new ColumnInfo(rdr.GetString(0), rdr.GetString(1), rdr.GetBoolean(2), null, null));
        return list;
    }

    public async Task<DataTable> GetTableRowsAsync(string connectionString, string table, int limit, string? whereClause = null, CancellationToken ct = default)
    {
        var quoted = QuoteId(table);
        var where = string.IsNullOrWhiteSpace(whereClause) ? "" : " WHERE " + whereClause;
        var sql = $"SELECT * FROM {quoted}{where} LIMIT {limit}";
        await using var cn = new NpgsqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var dt = new DataTable();
        await Task.Run(() => dt.Load(rdr), ct);
        return dt;
    }

    static string QuoteId(string id)
    {
        var parts = id.Replace("\"", "").Split('.');
        return string.Join(".", parts.Select(p => "\"" + p.Replace("\"", "\"\"") + "\""));
    }

    public async Task<DataTable> ExecuteQueryAsync(string connectionString, string sql, CancellationToken ct = default)
    {
        await using var cn = new NpgsqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, cn) { CommandTimeout = 30 };
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var dt = new DataTable();
        await Task.Run(() => dt.Load(rdr), ct);
        return dt;
    }

    static (string? schema, string name) ParseTable(string table)
    {
        var parts = table.Replace("\"", "").Split('.');
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, table.Replace("\"", ""));
    }
}
