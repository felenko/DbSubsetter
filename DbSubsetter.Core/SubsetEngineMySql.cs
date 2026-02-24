using MySqlConnector;
using System.Collections.Concurrent;
using System.Text;

namespace DbSubsetter.Core;

/// <summary>Subsets a MySQL database to a SQL file (INSERT statements). File mode only.</summary>
public class SubsetEngineMySql
{
    private readonly string _connStr;
    private readonly string _rootTable;
    private readonly string _rootPkVal;
    private readonly string _outFile;
    private readonly int _maxRowsPerTable;
    private readonly IProgress<SubsetProgress>? _progress;
    private readonly HashSet<string> _excludedTables;

    private Queue<string> _queue = new();
    private HashSet<string> _processedTables = new();
    private string _rootPkCol = string.Empty;
    private int _tablesProcessed;
    private readonly ConcurrentDictionary<string, HashSet<string>> _pkSets = new();
    private readonly ConcurrentDictionary<string, byte> _visitedRows = new();

    public long TotalOut { get; private set; }

    public SubsetEngineMySql(string connStr, string rootTable, string rootPkVal, string outFile,
        int maxRowsPerTable = 1000, IProgress<SubsetProgress>? progress = null,
        IReadOnlyCollection<string>? excludedTables = null)
    {
        _connStr = connStr;
        _rootTable = rootTable;
        _rootPkVal = rootPkVal;
        _outFile = outFile;
        _maxRowsPerTable = maxRowsPerTable;
        _progress = progress;
        _excludedTables = excludedTables is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(excludedTables.Select(Canon), StringComparer.OrdinalIgnoreCase);
    }

    private void Report(string message, string currentTable = "")
    {
        _progress?.Report(new SubsetProgress(
            message, currentTable, TotalOut, _tablesProcessed, _queue.Count));
    }

    static string Canon(string tbl)
        => tbl.Replace("`", "").Replace("\"", "").Split('.').LastOrDefault()?.ToLowerInvariant() ?? tbl.ToLowerInvariant();

    static string Esc(string id) => "`" + id.Replace("`", "``") + "`";

    static string Lit(object? v)
    {
        if (v is null or DBNull) return "NULL";
        if (v is byte[] bytes) return "0x" + BitConverter.ToString(bytes).Replace("-", "");
        if (v is int or long or short or byte) return v.ToString()!;
        if (v is float or double or decimal) return ((IFormattable)v).ToString(null, System.Globalization.CultureInfo.InvariantCulture)!;
        if (v is string s) return "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'";
        if (v is char c) return "'" + c.ToString().Replace("\\", "\\\\").Replace("'", "''") + "'";
        if (v is bool b) return b ? "1" : "0";
        if (v is DateTime dt) return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.fff") + "'";
        if (v is DateTimeOffset dto) return "'" + dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + "'";
        if (v is Guid g) return "'" + g + "'";
        if (v is TimeSpan ts) return "'" + ts + "'";
        if (v is IFormattable f) return "'" + (f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "").Replace("\\", "\\\\").Replace("'", "''") + "'";
        return "'" + (v.ToString() ?? "").Replace("\\", "\\\\").Replace("'", "''") + "'";
    }

    static (string? schema, string name) ParseTable(string table)
    {
        var parts = table.Replace("`", "").Split('.');
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, table.Replace("`", ""));
    }

    private string Qual(string table)
    {
        var (schema, name) = ParseTable(table);
        return schema != null ? $"{Esc(schema)}.{Esc(name)}" : Esc(name);
    }

    private async Task<string> GetPkColAsync(MySqlConnection cn, string table, CancellationToken ct)
    {
        var (schema, name) = ParseTable(table);
        const string sql = """
            SELECT COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = COALESCE(@schema, DATABASE()) AND TABLE_NAME = @name
            AND CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY ORDINAL_POSITION LIMIT 1
            """;
        await using var cmd = new MySqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", name);
        var o = await cmd.ExecuteScalarAsync(ct);
        if (o != null) return o.ToString()!;
        throw new InvalidOperationException($"No PK on {table}");
    }

    private async Task<List<FkInfo>> GetReferencingFksAsync(MySqlConnection cn, string parentTbl, CancellationToken ct)
    {
        var (parentSchema, parentName) = ParseTable(parentTbl);
        const string sql = """
            SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE REFERENCED_TABLE_SCHEMA IS NOT NULL AND REFERENCED_TABLE_NAME IS NOT NULL
            AND REFERENCED_TABLE_SCHEMA = COALESCE(@ps, DATABASE()) AND REFERENCED_TABLE_NAME = @pt
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION
            """;
        await using var cmd = new MySqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@ps", (object?)parentSchema ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pt", parentName);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var dbSchema = parentSchema ?? await GetCurrentSchemaAsync(cn, ct);
        var byChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await rdr.ReadAsync(ct))
        {
            var childSchema = rdr.GetString(0);
            var childName = rdr.GetString(1);
            var childFkCol = rdr.GetString(2);
            var childKey = (childSchema == dbSchema ? childName : $"{childSchema}.{childName}");
            if (!byChild.ContainsKey(childKey))
                byChild[childKey] = childFkCol;
        }
        var list = new List<FkInfo>();
        foreach (var (childTable, childFkCol) in byChild)
        {
            var childPkCol = await GetPkColAsync(cn, childTable, ct);
            list.Add(new FkInfo(childTable, childPkCol, childFkCol));
        }
        return list;
    }

    private async Task<string> GetCurrentSchemaAsync(MySqlConnection cn, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT DATABASE()", cn);
        var o = await cmd.ExecuteScalarAsync(ct);
        return o?.ToString() ?? "";
    }

    private static IEnumerable<List<string>> Batch(IEnumerable<string> src, int size)
    {
        var bucket = new List<string>(size);
        foreach (var s in src)
        {
            bucket.Add(s);
            if (bucket.Count == size) { yield return bucket; bucket = new List<string>(size); }
        }
        if (bucket.Count > 0) yield return bucket;
    }

    private async Task DumpRows(List<string> pkList, string pkCol, string table, string canon,
        MySqlConnection cn, StreamWriter writer, CancellationToken ct)
    {
        var qual = Qual(table);
        foreach (var batch in Batch(pkList, 1000))
        {
            ct.ThrowIfCancellationRequested();
            var where = $"{Esc(pkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
            var sql = $"SELECT * FROM {qual} WHERE {where} ORDER BY {Esc(pkCol)} DESC LIMIT {_maxRowsPerTable}";
            await using var cmd = new MySqlCommand(sql, cn);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            var cols = Enumerable.Range(0, rdr.FieldCount).Select(i => Esc(rdr.GetName(i))).ToArray();
            while (await rdr.ReadAsync(ct))
            {
                var pkVal = rdr.GetValue(rdr.GetOrdinal(pkCol)).ToString()!;
                if (!_visitedRows.TryAdd($"{canon}|{pkVal}", 0)) continue;
                var vals = new string[rdr.FieldCount];
                for (int i = 0; i < rdr.FieldCount; i++) vals[i] = Lit(rdr.GetValue(i));
                await writer.WriteLineAsync(
                    $"INSERT INTO {qual} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");
                TotalOut++;
                if (TotalOut % 250 == 0) Report($"   ... {TotalOut:N0} rows written", table);
            }
        }
    }

    private async Task HarvestChildKeys(string parentTable, List<string> parentPkList,
        MySqlConnection cn, CancellationToken ct)
    {
        var fks = await GetReferencingFksAsync(cn, parentTable, ct);
        foreach (var fk in fks)
        {
            ct.ThrowIfCancellationRequested();
            var childCan = Canon(fk.ChildTable);
            if (_excludedTables.Contains(childCan)) continue;
            if (!_pkSets.TryGetValue(childCan, out var childSet))
            {
                childSet = new HashSet<string>();
                _pkSets[childCan] = childSet;
            }
            if (childSet.Count >= _maxRowsPerTable) continue;
            var qual = Qual(fk.ChildTable);
            foreach (var batch in Batch(parentPkList, 1000))
            {
                ct.ThrowIfCancellationRequested();
                var where = $"{Esc(fk.ChildFkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
                var sql = $"SELECT {Esc(fk.ChildPkCol)} FROM {qual} WHERE {where} ORDER BY {Esc(fk.ChildPkCol)} DESC LIMIT {_maxRowsPerTable}";
                await using var cmd = new MySqlCommand(sql, cn);
                await using var rdr = await cmd.ExecuteReaderAsync(ct);
                bool added = false;
                while (await rdr.ReadAsync(ct) && childSet.Count < _maxRowsPerTable)
                    added |= childSet.Add(rdr[0].ToString()!);
                if (added && !_processedTables.Contains(fk.ChildTable))
                {
                    _queue.Enqueue(fk.ChildTable);
                    _processedTables.Add(fk.ChildTable);
                }
            }
        }
    }

    private async Task<List<string>> GetAllTablesAsync(MySqlConnection cn, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME", cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await rdr.ReadAsync(ct)) tables.Add(rdr.GetString(0));
        return tables;
    }

    private async Task<HashSet<string>> GetReachableTablesAsync(MySqlConnection cn, CancellationToken ct)
    {
        var allTables = await GetAllTablesAsync(cn, ct);
        var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTables)
            adj[t] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string sql = """
            SELECT TABLE_SCHEMA, TABLE_NAME, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE REFERENCED_TABLE_SCHEMA IS NOT NULL AND REFERENCED_TABLE_NAME IS NOT NULL
            AND TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_SCHEMA = DATABASE()
            """;
        await using var cmd = new MySqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var child = rdr.GetString(1);
            var parent = rdr.GetString(3);
            adj[child].Add(parent);
            if (adj.ContainsKey(parent)) adj[parent].Add(child);
        }
        var rootCan = Canon(_rootTable);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootCan };
        var queue = new Queue<string>();
        queue.Enqueue(rootCan);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (adj.TryGetValue(current, out var neighbors))
                foreach (var n in neighbors)
                    if (visited.Add(n)) queue.Enqueue(n);
        }
        return visited;
    }

    private async Task DumpFullTable(string table, MySqlConnection cn, StreamWriter writer, CancellationToken ct)
    {
        var qual = Qual(table);
        var sql = $"SELECT * FROM {qual} LIMIT {_maxRowsPerTable}";
        await using var cmd = new MySqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var cols = Enumerable.Range(0, rdr.FieldCount).Select(i => Esc(rdr.GetName(i))).ToArray();
        while (await rdr.ReadAsync(ct))
        {
            var vals = new string[rdr.FieldCount];
            for (int i = 0; i < rdr.FieldCount; i++) vals[i] = Lit(rdr.GetValue(i));
            await writer.WriteLineAsync(
                $"INSERT INTO {qual} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");
            TotalOut++;
            if (TotalOut % 250 == 0) Report($"   ... {TotalOut:N0} rows written", table);
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var cn = new MySqlConnection(_connStr);
        await cn.OpenAsync(ct);
        await using var writer = new StreamWriter(_outFile, false, Encoding.UTF8);

        Report($"▶ Starting MySQL subset → {_outFile}");

        _rootPkCol = await GetPkColAsync(cn, _rootTable, ct);
        _pkSets[Canon(_rootTable)] = new HashSet<string> { _rootPkVal };
        _queue = new Queue<string>();
        _queue.Enqueue(_rootTable);
        _processedTables = new HashSet<string> { _rootTable };

        while (_queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var table = _queue.Dequeue();
            var can = Canon(table);
            if (_excludedTables.Contains(can)) continue;
            if (!_pkSets.TryGetValue(can, out var pkSet) || pkSet.Count == 0) continue;
            var pkList = pkSet.OrderByDescending(v => v, StringComparer.Ordinal).Take(_maxRowsPerTable).ToList();
            pkSet.Clear();
            Report($"▶ {table} ... exporting up to {_maxRowsPerTable:N0} rows (have {pkList.Count})", table);
            var pkCol = table == _rootTable ? _rootPkCol : await GetPkColAsync(cn, table, ct);
            await DumpRows(pkList, pkCol, table, can, cn, writer, ct);
            await HarvestChildKeys(table, pkList, cn, ct);
            _tablesProcessed++;
            Report($"✓ {table} done ({TotalOut:N0} total rows)", table);
        }

        Report("▶ Pass 2: exporting unrelated tables...");
        var allTables = await GetAllTablesAsync(cn, ct);
        var reachable = await GetReachableTablesAsync(cn, ct);
        var alreadyProcessed = new HashSet<string>(_processedTables.Select(Canon), StringComparer.OrdinalIgnoreCase);
        foreach (var table in allTables)
        {
            ct.ThrowIfCancellationRequested();
            var can = Canon(table);
            if (reachable.Contains(can) || alreadyProcessed.Contains(can) || _excludedTables.Contains(can)) continue;
            Report($"▶ {table} (unrelated) ... exporting up to {_maxRowsPerTable:N0} rows", table);
            await DumpFullTable(table, cn, writer, ct);
            _tablesProcessed++;
            Report($"✓ {table} done ({TotalOut:N0} total rows)", table);
        }
        Report($"✓ Complete. {TotalOut:N0} INSERTs written to {_outFile}");
    }
}
