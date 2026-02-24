using Npgsql;
using System.Collections.Concurrent;
using System.Text;

namespace DbSubsetter.Core;

/// <summary>Subsets a PostgreSQL database to a SQL file (INSERT statements). File mode only.</summary>
public class SubsetEnginePostgres
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

    public SubsetEnginePostgres(string connStr, string rootTable, string rootPkVal, string outFile,
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
        => tbl.Replace("\"", "").Split('.').LastOrDefault()?.ToLowerInvariant() ?? tbl.ToLowerInvariant();

    /// <summary>Double-quote identifier (schema.table → "schema"."table"). Same as SchemaExplorerPostgres.QuoteId.</summary>
    static string Esc(string id)
    {
        var parts = id.Replace("\"", "").Split('.');
        return string.Join(".", parts.Select(p => "\"" + p.Replace("\"", "\"\"") + "\""));
    }

    static string Lit(object? v)
    {
        if (v is null or DBNull) return "NULL";
        if (v is byte[] bytes) return "'\\x" + BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant() + "'";
        if (v is int or long or short or byte) return v.ToString()!;
        if (v is float or double or decimal) return ((IFormattable)v).ToString(null, System.Globalization.CultureInfo.InvariantCulture)!;
        if (v is string s) return "'" + s.Replace("'", "''") + "'";
        if (v is char c) return "'" + c.ToString().Replace("'", "''") + "'";
        if (v is bool b) return b ? "TRUE" : "FALSE";
        if (v is DateTime dt) return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.fff") + "'";
        if (v is DateTimeOffset dto) return "'" + dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + "'";
        if (v is Guid g) return "'" + g + "'";
        if (v is TimeSpan ts) return "'" + ts + "'";
        if (v is IFormattable f) return "'" + (f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "").Replace("'", "''") + "'";
        return "'" + (v.ToString() ?? "").Replace("'", "''") + "'";
    }

    static (string? schema, string name) ParseTable(string table)
    {
        var parts = table.Replace("\"", "").Split('.');
        return parts.Length == 2 ? (parts[0], parts[1]) : (null, table.Replace("\"", ""));
    }

    private async Task<string> GetPkColAsync(NpgsqlConnection cn, string table, CancellationToken ct)
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
        await using var cmd = new NpgsqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("schema", (object?)schema ?? DBNull.Value);
        var o = await cmd.ExecuteScalarAsync(ct);
        if (o != null) return o.ToString()!;
        throw new InvalidOperationException($"No PK on {table}");
    }

    private async Task<List<FkInfo>> GetReferencingFksAsync(NpgsqlConnection cn, string parentTbl, CancellationToken ct)
    {
        var (parentSchema, parentName) = ParseTable(parentTbl);
        const string sql = """
            SELECT tc.table_schema, tc.table_name, kcu.column_name
            FROM information_schema.referential_constraints rc
            JOIN information_schema.table_constraints tc ON tc.constraint_schema = rc.constraint_schema AND tc.constraint_name = rc.constraint_name AND tc.constraint_type = 'FOREIGN KEY'
            JOIN information_schema.table_constraints parent_pk ON parent_pk.constraint_schema = rc.unique_constraint_schema AND parent_pk.constraint_name = rc.unique_constraint_name AND parent_pk.constraint_type = 'PRIMARY KEY'
            JOIN information_schema.key_column_usage kcu ON kcu.constraint_schema = rc.constraint_schema AND kcu.constraint_name = rc.constraint_name AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name
            WHERE parent_pk.table_schema = COALESCE(@ps, 'public') AND parent_pk.table_name = @pt
            ORDER BY tc.table_schema, tc.table_name, kcu.ordinal_position
            """;
        await using var cmd = new NpgsqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("ps", (object?)parentSchema ?? DBNull.Value);
        cmd.Parameters.AddWithValue("pt", parentName);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var byChild = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await rdr.ReadAsync(ct))
        {
            var childSchema = rdr.GetString(0);
            var childName = rdr.GetString(1);
            var childFkCol = rdr.GetString(2);
            var childKey = (childSchema == (parentSchema ?? "public") ? childName : $"{childSchema}.{childName}");
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
        NpgsqlConnection cn, StreamWriter writer, CancellationToken ct)
    {
        var qual = Esc(table);
        foreach (var batch in Batch(pkList, 1000))
        {
            ct.ThrowIfCancellationRequested();
            var where = $"{Esc(pkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
            var sql = $"SELECT * FROM {qual} WHERE {where} ORDER BY {Esc(pkCol)} DESC LIMIT {_maxRowsPerTable}";
            await using var cmd = new NpgsqlCommand(sql, cn);
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
        NpgsqlConnection cn, CancellationToken ct)
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
            var qual = Esc(fk.ChildTable);
            foreach (var batch in Batch(parentPkList, 1000))
            {
                ct.ThrowIfCancellationRequested();
                var where = $"{Esc(fk.ChildFkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
                var sql = $"SELECT {Esc(fk.ChildPkCol)} FROM {qual} WHERE {where} ORDER BY {Esc(fk.ChildPkCol)} DESC LIMIT {_maxRowsPerTable}";
                await using var cmd = new NpgsqlCommand(sql, cn);
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

    private async Task<List<string>> GetAllTablesAsync(NpgsqlConnection cn, CancellationToken ct)
    {
        const string sql = """
            SELECT table_schema || '.' || table_name
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema') AND table_type = 'BASE TABLE'
            ORDER BY table_schema, table_name
            """;
        await using var cmd = new NpgsqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await rdr.ReadAsync(ct)) tables.Add(rdr.GetString(0));
        return tables;
    }

    private async Task<HashSet<string>> GetReachableTablesAsync(NpgsqlConnection cn, CancellationToken ct)
    {
        var allTables = await GetAllTablesAsync(cn, ct);
        var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTables)
            adj[t] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string sql = """
            SELECT tc.table_schema || '.' || tc.table_name AS child, ccu.table_schema || '.' || ccu.table_name AS parent
            FROM information_schema.referential_constraints rc
            JOIN information_schema.table_constraints tc ON tc.constraint_schema = rc.constraint_schema AND tc.constraint_name = rc.constraint_name AND tc.constraint_type = 'FOREIGN KEY'
            JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_schema = rc.unique_constraint_schema AND ccu.constraint_name = rc.unique_constraint_name
            """;
        await using var cmd = new NpgsqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            var child = rdr.GetString(0);
            var parent = rdr.GetString(1);
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

    private async Task DumpFullTable(string table, NpgsqlConnection cn, StreamWriter writer, CancellationToken ct)
    {
        var qual = Esc(table);
        var sql = $"SELECT * FROM {qual} LIMIT {_maxRowsPerTable}";
        await using var cmd = new NpgsqlCommand(sql, cn);
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
        await using var cn = new NpgsqlConnection(_connStr);
        await cn.OpenAsync(ct);
        await using var writer = new StreamWriter(_outFile, false, Encoding.UTF8);

        Report($"▶ Starting PostgreSQL subset → {_outFile}");

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
