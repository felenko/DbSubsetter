using System.Data;
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

    /// <summary>
    /// Gets sample rows from a table for choosing the root entry. Returns PK value and a display string.
    /// </summary>
    public async Task<List<RootRowCandidate>> GetSampleRowsAsync(
        string connectionString,
        string table,
        string pkColumn,
        int limit = 200,
        CancellationToken ct = default)
    {
        string Esc(string id) => $"[{id.Replace("]", "]]")}]";
        string pkEsc = Esc(pkColumn);

        string sql = $"""
            SELECT TOP ({limit}) {pkEsc}
            FROM {table}
            ORDER BY {pkEsc} DESC;
            """;

        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
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
        const string sql = """
            SELECT c.name,
                   tp.name +
                     CASE WHEN tp.name IN ('nvarchar','varchar','char','nchar')
                          THEN '(' + CASE c.max_length WHEN -1 THEN 'max'
                               ELSE CAST(c.max_length/(CASE WHEN tp.name LIKE 'n%' THEN 2 ELSE 1 END) AS varchar) END + ')'
                          WHEN tp.name IN ('decimal','numeric')
                          THEN '(' + CAST(c.precision AS varchar) + ',' + CAST(c.scale AS varchar) + ')'
                          ELSE '' END  AS data_type,
                   MAX(CASE WHEN ix.is_primary_key = 1 THEN 1 ELSE 0 END) AS is_pk,
                   MAX(CASE WHEN fkc.parent_column_id IS NOT NULL THEN 1 ELSE 0 END) AS is_fk,
                   MAX(QUOTENAME(rs.name) + '.' + QUOTENAME(rt.name)) AS fk_ref_table,
                   MAX(rc.name) AS fk_ref_column
            FROM sys.columns c
            JOIN sys.types tp ON tp.user_type_id = c.user_type_id
            LEFT JOIN sys.index_columns ic  ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.indexes ix        ON ix.object_id = ic.object_id AND ix.index_id = ic.index_id AND ix.is_primary_key = 1
            LEFT JOIN sys.foreign_key_columns fkc ON fkc.parent_object_id = c.object_id AND fkc.parent_column_id = c.column_id
            LEFT JOIN sys.tables rt   ON rt.object_id = fkc.referenced_object_id
            LEFT JOIN sys.schemas rs  ON rs.schema_id = rt.schema_id
            LEFT JOIN sys.columns rc  ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE c.object_id = OBJECT_ID(@tbl)
            GROUP BY c.column_id, c.name, tp.name, c.max_length, c.precision, c.scale
            ORDER BY c.column_id
            """;

        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@tbl", table);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var list = new List<ColumnInfo>();
        while (await rdr.ReadAsync(ct))
        {
            list.Add(new ColumnInfo(
                Name: rdr.GetString(0),
                DataType: rdr.GetString(1),
                IsPrimaryKey: rdr.GetInt32(2) == 1,
                FkReferencedTable: rdr.IsDBNull(4) ? null : rdr.GetString(4),
                FkReferencedColumn: rdr.IsDBNull(5) ? null : rdr.GetString(5)));
        }
        return list;
    }

    public async Task<DataTable> GetTableRowsAsync(
        string connectionString,
        string table,
        int limit,
        string? whereClause = null,
        CancellationToken ct = default)
    {
        string where = string.IsNullOrWhiteSpace(whereClause) ? "" : $" WHERE {whereClause}";
        string sql = $"SELECT TOP (@limit) * FROM {table}{where}";

        await using var cn = new SqlConnection(connectionString);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@limit", limit);

        var dt = new DataTable();
        using var adapter = new SqlDataAdapter(cmd);
        await Task.Run(() => adapter.Fill(dt), ct);
        return dt;
    }
}
