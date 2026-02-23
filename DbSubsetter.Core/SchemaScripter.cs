using Microsoft.Data.SqlClient;
using System.Text;

namespace DbSubsetter.Core;

public class SchemaScripter
{
    public async Task<string> GetCreateTableSqlAsync(string srcConnStr, string table, CancellationToken ct)
    {
        await using var cn = new SqlConnection(srcConnStr);
        await cn.OpenAsync(ct);

        const string colSql = """
            SELECT c.column_id, c.name, tp.name AS type_name,
                   c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed,
                   CAST(IDENT_SEED(OBJECT_ID(@tbl)) AS bigint) AS identity_seed,
                   CAST(IDENT_INCR(OBJECT_ID(@tbl)) AS bigint) AS identity_incr,
                   dc.definition AS default_def, dc.name AS default_name,
                   MAX(CASE WHEN ix.is_primary_key = 1 THEN 1 ELSE 0 END) AS is_pk
            FROM sys.columns c
            JOIN sys.types tp ON tp.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints dc
                   ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.index_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.indexes ix
                   ON ix.object_id = ic.object_id AND ix.index_id = ic.index_id AND ix.is_primary_key = 1
            WHERE c.object_id = OBJECT_ID(@tbl)
              AND tp.name NOT IN ('timestamp', 'rowversion')
            GROUP BY c.column_id, c.name, tp.name, c.max_length, c.precision, c.scale,
                     c.is_nullable, c.is_identity, c.is_computed, dc.definition, dc.name
            ORDER BY c.column_id
            """;

        await using var cmd = new SqlCommand(colSql, cn);
        cmd.Parameters.AddWithValue("@tbl", table);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var columns = new List<(int columnId, string name, string typeName, short maxLength,
            byte precision, byte scale, bool isNullable, bool isIdentity, bool isComputed,
            long? identitySeed, long? identityIncr, string? defaultDef, string? defaultName)>();

        while (await rdr.ReadAsync(ct))
        {
            columns.Add((
                rdr.GetInt32(0),
                rdr.GetString(1),
                rdr.GetString(2),
                rdr.GetInt16(3),
                rdr.GetByte(4),
                rdr.GetByte(5),
                rdr.GetBoolean(6),
                rdr.GetBoolean(7),
                rdr.GetBoolean(8),
                rdr.IsDBNull(9) ? null : rdr.GetInt64(9),
                rdr.IsDBNull(10) ? null : rdr.GetInt64(10),
                rdr.IsDBNull(11) ? null : rdr.GetString(11),
                rdr.IsDBNull(12) ? null : rdr.GetString(12)
            ));
        }
        await rdr.CloseAsync();

        // PK constraint name
        const string pkNameSql = """
            SELECT TOP 1 i.name FROM sys.indexes i
            WHERE i.object_id = OBJECT_ID(@tbl) AND i.is_primary_key = 1
            """;
        await using var pkNameCmd = new SqlCommand(pkNameSql, cn);
        pkNameCmd.Parameters.AddWithValue("@tbl", table);
        string? pkConstraintName = (string?)await pkNameCmd.ExecuteScalarAsync(ct);

        // PK column list
        const string pkColsSql = """
            SELECT c.name FROM sys.index_columns ic
            JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@tbl) AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal
            """;
        await using var pkColsCmd = new SqlCommand(pkColsSql, cn);
        pkColsCmd.Parameters.AddWithValue("@tbl", table);
        await using var pkColsRdr = await pkColsCmd.ExecuteReaderAsync(ct);
        var pkCols = new List<string>();
        while (await pkColsRdr.ReadAsync(ct))
            pkCols.Add(pkColsRdr.GetString(0));
        await pkColsRdr.CloseAsync();

        // Build CREATE TABLE SQL
        var sb = new StringBuilder();
        sb.AppendLine($"IF OBJECT_ID(N'{table}', N'U') IS NULL");
        sb.AppendLine($"CREATE TABLE {table} (");

        var colDefs = new List<string>();
        foreach (var col in columns)
        {
            if (col.isComputed) continue;

            var colSb = new StringBuilder();
            colSb.Append($"    [{EscName(col.name)}] ");
            colSb.Append(BuildTypeString(col.typeName, col.maxLength, col.precision, col.scale));

            if (col.isIdentity && col.identitySeed.HasValue && col.identityIncr.HasValue)
                colSb.Append($" IDENTITY({col.identitySeed},{col.identityIncr})");

            colSb.Append(col.isNullable ? " NULL" : " NOT NULL");

            if (col.defaultDef != null)
            {
                if (col.defaultName != null)
                    colSb.Append($" CONSTRAINT [{EscName(col.defaultName)}] DEFAULT {col.defaultDef}");
                else
                    colSb.Append($" DEFAULT {col.defaultDef}");
            }

            colDefs.Add(colSb.ToString());
        }

        if (pkConstraintName != null && pkCols.Count > 0)
        {
            var pkColList = string.Join(", ", pkCols.Select(c => $"[{EscName(c)}] ASC"));
            colDefs.Add($"    CONSTRAINT [{EscName(pkConstraintName)}] PRIMARY KEY CLUSTERED ({pkColList})");
        }

        sb.Append(string.Join(",\n", colDefs));
        sb.AppendLine();
        sb.AppendLine(");");

        return sb.ToString();
    }

    public async Task<List<string>> GetForeignKeySqlsAsync(string srcConnStr, string table, CancellationToken ct)
    {
        const string fkSql = """
            SELECT fk.name, QUOTENAME(ps.name)+'.'+QUOTENAME(pt.name) AS parent_table,
                   QUOTENAME(rs.name)+'.'+QUOTENAME(rt.name) AS ref_table,
                   fkc.constraint_column_id, pc.name AS parent_col, rc.name AS ref_col
            FROM sys.foreign_keys fk
            JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fk.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fk.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE fk.parent_object_id = OBJECT_ID(@tbl)
            ORDER BY fk.name, fkc.constraint_column_id
            """;

        await using var cn = new SqlConnection(srcConnStr);
        await cn.OpenAsync(ct);
        await using var cmd = new SqlCommand(fkSql, cn);
        cmd.Parameters.AddWithValue("@tbl", table);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        var fkGroups = new Dictionary<string, (string parentTable, string refTable, List<(string parentCol, string refCol)> cols)>(
            StringComparer.OrdinalIgnoreCase);

        while (await rdr.ReadAsync(ct))
        {
            string fkName = rdr.GetString(0);
            string parentTable = rdr.GetString(1);
            string refTable = rdr.GetString(2);
            string parentCol = rdr.GetString(4);
            string refCol = rdr.GetString(5);

            if (!fkGroups.TryGetValue(fkName, out var fkInfo))
            {
                fkInfo = (parentTable, refTable, new List<(string, string)>());
                fkGroups[fkName] = fkInfo;
            }
            fkInfo.cols.Add((parentCol, refCol));
        }

        var result = new List<string>();
        foreach (var (fkName, (parentTable, refTable, cols)) in fkGroups)
        {
            var parentCols = string.Join(", ", cols.Select(c => $"[{EscName(c.parentCol)}]"));
            var refCols = string.Join(", ", cols.Select(c => $"[{EscName(c.refCol)}]"));
            result.Add($"""
                IF OBJECT_ID(N'[{EscName(fkName)}]', N'F') IS NULL
                ALTER TABLE {parentTable} ADD CONSTRAINT [{EscName(fkName)}]
                    FOREIGN KEY ({parentCols}) REFERENCES {refTable} ({refCols});
                """);
        }

        return result;
    }

    internal async Task<List<string>> GetBulkColumnListAsync(SqlConnection cn, string table, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name FROM sys.columns c
            JOIN sys.types tp ON tp.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@tbl)
              AND c.is_computed = 0 AND tp.name NOT IN ('timestamp','rowversion')
            ORDER BY c.column_id
            """;
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@tbl", table);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var cols = new List<string>();
        while (await rdr.ReadAsync(ct))
            cols.Add(rdr.GetString(0));
        return cols;
    }

    private static string BuildTypeString(string typeName, short maxLength, byte precision, byte scale)
    {
        return typeName.ToLowerInvariant() switch
        {
            "nvarchar" or "nchar" => maxLength == -1 ? $"{typeName}(MAX)" : $"{typeName}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{typeName}(MAX)" : $"{typeName}({maxLength})",
            "decimal" or "numeric" => $"{typeName}({precision},{scale})",
            "float" => precision == 24 ? "real" : "float",
            "datetime2" or "time" or "datetimeoffset" => $"{typeName}({scale})",
            _ => typeName
        };
    }

    private static string EscName(string name) => name.Replace("]", "]]");
}
