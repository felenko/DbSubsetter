using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Text;

namespace DbSubsetter.Core;

/// <summary>Subsets a SQLite database to a SQL file (INSERT statements). File mode only.</summary>
public class SubsetEngineSqlite
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

    public SubsetEngineSqlite(string connStr, string rootTable, string rootPkVal, string outFile,
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
        => tbl.Replace("\"", "").Replace("[", "").Replace("]", "").ToLowerInvariant();

    static string Esc(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";

    static string Lit(object? v)
    {
        if (v is null or DBNull) return "NULL";
        if (v is byte[] bytes) return "X'" + BitConverter.ToString(bytes).Replace("-", "") + "'";
        if (v is int or long or short or byte) return v.ToString()!;
        if (v is float or double or decimal) return ((IFormattable)v).ToString(null, System.Globalization.CultureInfo.InvariantCulture)!;
        if (v is string s) return "'" + s.Replace("'", "''") + "'";
        if (v is char c) return "'" + c.ToString().Replace("'", "''") + "'";
        if (v is bool b) return b ? "1" : "0";
        if (v is DateTime dt) return "'" + dt.ToString("yyyy-MM-dd HH:mm:ss.fff") + "'";
        if (v is DateTimeOffset dto) return "'" + dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + "'";
        if (v is Guid g) return "'" + g + "'";
        if (v is TimeSpan ts) return "'" + ts + "'";
        if (v is IFormattable f) return "'" + (f.ToString(null, System.Globalization.CultureInfo.InvariantCulture) ?? "").Replace("'", "''") + "'";
        return "'" + (v.ToString() ?? "").Replace("'", "''") + "'";
    }

    private async Task<string> GetPkColAsync(SqliteConnection cn, string table, CancellationToken ct)
    {
        var quoted = Esc(table);
        await using var cmd = new SqliteCommand($"PRAGMA table_info({quoted})", cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            if (rdr.GetInt64(5) >= 1) // pk column
                return rdr.GetString(1);
        }
        throw new InvalidOperationException($"No PK on {table}");
    }

    /// <summary>PRAGMA foreign_key_list(childTable) returns rows where column 2 = referenced table. So we scan all tables.</summary>
    private async Task<List<FkInfo>> GetReferencingFksAsync(SqliteConnection cn, string parentTbl, CancellationToken ct)
    {
        var allTables = await GetAllTablesAsync(cn, ct);
        var list = new List<FkInfo>();
        var parentCan = Canon(parentTbl);
        foreach (var childTable in allTables)
        {
            await using var cmd = new SqliteCommand($"PRAGMA foreign_key_list({Esc(childTable)})", cn);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var refTable = rdr.GetString(2); // referenced table
                if (Canon(refTable) != parentCan) continue;
                var childFkCol = rdr.GetString(3); // "from" = column in child
                var childPkCol = await GetPkColAsync(cn, childTable, ct);
                list.Add(new FkInfo(childTable, childPkCol, childFkCol));
                break; // one FK from this child to parent
            }
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
        SqliteConnection cn, StreamWriter writer, CancellationToken ct)
    {
        foreach (var batch in Batch(pkList, 1000))
        {
            ct.ThrowIfCancellationRequested();
            var where = $"{Esc(pkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
            var sql = $"SELECT * FROM {Esc(table)} WHERE {where} ORDER BY {Esc(pkCol)} DESC LIMIT {_maxRowsPerTable}";
            await using var cmd = new SqliteCommand(sql, cn);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            var cols = Enumerable.Range(0, rdr.FieldCount).Select(i => Esc(rdr.GetName(i))).ToArray();
            while (await rdr.ReadAsync(ct))
            {
                var pkVal = rdr.GetValue(rdr.GetOrdinal(pkCol)).ToString()!;
                if (!_visitedRows.TryAdd($"{canon}|{pkVal}", 0)) continue;
                var vals = new string[rdr.FieldCount];
                for (int i = 0; i < rdr.FieldCount; i++) vals[i] = Lit(rdr.GetValue(i));
                await writer.WriteLineAsync(
                    $"INSERT INTO {Esc(table)} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");
                TotalOut++;
                if (TotalOut % 250 == 0) Report($"   ... {TotalOut:N0} rows written", table);
            }
        }
    }

    private async Task HarvestChildKeys(string parentTable, List<string> parentPkList,
        SqliteConnection cn, CancellationToken ct)
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
            foreach (var batch in Batch(parentPkList, 1000))
            {
                ct.ThrowIfCancellationRequested();
                var where = $"{Esc(fk.ChildFkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
                var sql = $"SELECT {Esc(fk.ChildPkCol)} FROM {Esc(fk.ChildTable)} WHERE {where} ORDER BY {Esc(fk.ChildPkCol)} DESC LIMIT {_maxRowsPerTable}";
                await using var cmd = new SqliteCommand(sql, cn);
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

    private async Task<List<string>> GetAllTablesAsync(SqliteConnection cn, CancellationToken ct)
    {
        await using var cmd = new SqliteCommand(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name", cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await rdr.ReadAsync(ct)) tables.Add(rdr.GetString(0));
        return tables;
    }

    private async Task<HashSet<string>> GetReachableTablesAsync(SqliteConnection cn, CancellationToken ct)
    {
        var allTables = await GetAllTablesAsync(cn, ct);
        var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allTables)
        {
            adj[t] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var cmd = new SqliteCommand($"PRAGMA foreign_key_list({Esc(t)})", cn);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                var refTable = rdr.GetString(2);
                adj[t].Add(refTable);
                if (!adj.ContainsKey(refTable)) adj[refTable] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                adj[refTable].Add(t);
            }
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

    private async Task DumpFullTable(string table, SqliteConnection cn, StreamWriter writer, CancellationToken ct)
    {
        var sql = $"SELECT * FROM {Esc(table)} LIMIT {_maxRowsPerTable}";
        await using var cmd = new SqliteCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var cols = Enumerable.Range(0, rdr.FieldCount).Select(i => Esc(rdr.GetName(i))).ToArray();
        while (await rdr.ReadAsync(ct))
        {
            var vals = new string[rdr.FieldCount];
            for (int i = 0; i < rdr.FieldCount; i++) vals[i] = Lit(rdr.GetValue(i));
            await writer.WriteLineAsync(
                $"INSERT INTO {Esc(table)} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");
            TotalOut++;
            if (TotalOut % 250 == 0) Report($"   ... {TotalOut:N0} rows written", table);
        }
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var cn = new SqliteConnection(_connStr);
        await cn.OpenAsync(ct);
        await using var writer = new StreamWriter(_outFile, false, Encoding.UTF8);

        Report($"▶ Starting SQLite subset → {_outFile}");

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
