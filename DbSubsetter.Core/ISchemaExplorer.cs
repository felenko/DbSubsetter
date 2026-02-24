using System.Data;

namespace DbSubsetter.Core;

public interface ISchemaExplorer
{
    Task<bool> TestConnectionAsync(string connectionString, CancellationToken ct = default);
    Task<List<string>> GetTablesAsync(string connectionString, CancellationToken ct = default);
    Task<string?> GetPrimaryKeyColumnAsync(string connectionString, string table, CancellationToken ct = default);
    Task<List<RootRowCandidate>> GetSampleRowsAsync(string connectionString, string table, string pkColumn, int limit = 200, CancellationToken ct = default);
    Task<List<ColumnInfo>> GetTableColumnsAsync(string connectionString, string table, CancellationToken ct = default);
    Task<DataTable> GetTableRowsAsync(string connectionString, string table, int limit, string? whereClause = null, CancellationToken ct = default);
}
