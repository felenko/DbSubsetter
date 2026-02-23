using Microsoft.Data.SqlClient;

namespace DbSubsetter.Core;

public class SchemaExplorer
{
    public async Task<bool> TestConnectionAsync(string connectionString, CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        return true;
    }

    public async Task<List<string>> GetTablesAsync(string connectionString, CancellationToken ct = default)
    {
        const string sql = """
            SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            ORDER BY s.name, t.name;
            """;

        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var tables = new List<string>();
        while (await rdr.ReadAsync(ct))
            tables.Add(rdr.GetString(0));
        return tables;
    }

    public async Task<string?> GetPrimaryKeyColumnAsync(string connectionString, string table, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP (1) c.name
            FROM   sys.indexes        i
            JOIN   sys.index_columns  ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN   sys.columns        c  ON c.object_id  = i.object_id AND c.column_id = ic.column_id
            WHERE  i.is_primary_key = 1
              AND  QUOTENAME(OBJECT_SCHEMA_NAME(i.object_id)) + '.' +
                   QUOTENAME(OBJECT_NAME(i.object_id))        = @tbl
            ORDER BY ic.key_ordinal;
            """;

        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@tbl", table);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
