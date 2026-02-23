using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Data;
using System.Text;

namespace DbSubsetter.Core;

public class SubsetEngine
{
    private readonly string _connStr;
    private readonly string _rootTable;
    private readonly string _rootPkVal;
    private readonly string? _destConnStr;
    private readonly string? _outFile;
    private readonly int _maxRowsPerTable;
    private readonly IProgress<SubsetProgress>? _progress;
    private readonly HashSet<string> _excludedTables;
    private readonly SchemaScripter _scripter = new();
    private readonly List<string> _fkScripts = new();

    private Queue<string> _queue = new();
    private HashSet<string> _processedTables = new();
    private string _rootPkCol = string.Empty;
    private int _tablesProcessed;

    private readonly ConcurrentDictionary<string, HashSet<string>> _pkSets = new();
    private readonly ConcurrentDictionary<string, byte> _visitedRows = new();

    public long TotalOut { get; private set; }

    public SubsetEngine(string connStr, string rootTable, string rootPkVal,
        string? destConnStr = null, string? outFile = null,
        int maxRowsPerTable = 1000, IProgress<SubsetProgress>? progress = null,
        IReadOnlyCollection<string>? excludedTables = null)
    {
        if (destConnStr is null && outFile is null)
            throw new ArgumentException("Either destConnStr or outFile must be provided.");
        _connStr = connStr;
        _rootTable = rootTable;
        _rootPkVal = rootPkVal;
        _destConnStr = destConnStr;
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
        => tbl.Replace("[", "").Replace("]", "").ToLowerInvariant();

    static string Esc(string id) => $"[{id.Replace("]", "]]")}]";

    static string Lit(object? v) => v switch
    {
        null or DBNull => "NULL",
        string s => $"N'{s.Replace("'", "''")}'",
        char c => $"N'{c}'",
        bool b => b ? "1" : "0",
        byte[] bytes => "0x" + BitConverter.ToString(bytes).Replace("-", ""),
        DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.fff}'",
        DateTimeOffset dto => $"'{dto:yyyy-MM-dd HH:mm:ss.fff zzz}'",
        Guid g => $"'{g}'",
        TimeSpan ts => $"'{ts}'",
        _ when v is IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture)!,
        _ => $"N'{v.ToString()!.Replace("'", "''")}'"
    };

    async Task<string> GetPkColAsync(SqlConnection cn, string table, CancellationToken ct)
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
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@tbl", table);
        return (string?)await cmd.ExecuteScalarAsync(ct)
               ?? throw new InvalidOperationException($"No PK on {table}");
    }

    async Task<List<FkInfo>> GetReferencingFksAsync(SqlConnection cn, string parentTbl, CancellationToken ct)
    {
        const string sql = """
            DECLARE @p int = OBJECT_ID(@parent);
            SELECT DISTINCT
                   childTab   = QUOTENAME(OBJECT_SCHEMA_NAME(ct.object_id)) + '.' + QUOTENAME(ct.name),
                   childPkCol = pkc.name,
                   childFkCol = fkcol.name
            FROM sys.foreign_keys        fk
            JOIN sys.tables              ct    ON ct.object_id = fk.parent_object_id
            JOIN sys.foreign_key_columns fkc   ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns             fkcol ON fkcol.object_id = ct.object_id
                                                 AND fkcol.column_id = fkc.parent_column_id
            JOIN sys.indexes             pk    ON pk.object_id = ct.object_id AND pk.is_primary_key = 1
            JOIN sys.index_columns       pkic  ON pkic.object_id = ct.object_id
                                                 AND pkic.index_id  = pk.index_id
                                                 AND pkic.key_ordinal = 1
            JOIN sys.columns             pkc   ON pkc.object_id = ct.object_id
                                                 AND pkc.column_id = pkic.column_id
            WHERE fk.referenced_object_id = @p;
            """;
        await using var cmd = new SqlCommand(sql, cn);
        cmd.Parameters.AddWithValue("@parent", parentTbl);
        var list = new List<FkInfo>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
            list.Add(new FkInfo(rdr.GetString(0), rdr.GetString(1), rdr.GetString(2)));
        return list;
    }

    static IEnumerable<List<string>> Batch(IEnumerable<string> src, int size)
    {
        var bucket = new List<string>(size);
        foreach (var s in src)
        {
            bucket.Add(s);
            if (bucket.Count == size)
            {
                yield return bucket;
                bucket = new List<string>(size);
            }
        }
        if (bucket.Count > 0)
            yield return bucket;
    }

    async Task HarvestChildKeys(string parentTable, List<string> parentPkList,
        SqlConnection cn, CancellationToken ct)
    {
        var fks = await GetReferencingFksAsync(cn, parentTable, ct);
        foreach (var fk in fks)
        {
            ct.ThrowIfCancellationRequested();

            string childCan = Canon(fk.ChildTable);

            if (_excludedTables.Contains(childCan))
                continue;

            if (!_pkSets.TryGetValue(childCan, out var childSet))
            {
                childSet = new HashSet<string>();
                _pkSets[childCan] = childSet;
            }

            if (childSet.Count >= _maxRowsPerTable)
                continue;

            foreach (var batch in Batch(parentPkList, 1000))
            {
                ct.ThrowIfCancellationRequested();

                string where = $"{Esc(fk.ChildFkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
                string sql = $"""
                    SELECT TOP ({_maxRowsPerTable}) {Esc(fk.ChildPkCol)}
                    FROM   {fk.ChildTable}
                    WHERE  {where}
                    ORDER  BY {Esc(fk.ChildPkCol)} DESC;
                    """;

                await using var cmd = new SqlCommand(sql, cn);
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

    // ── File-mode helpers ────────────────────────────────────────────────────

    async Task DumpRows(List<string> pkList, string pkCol, string table, string canon,
        SqlConnection cn, StreamWriter writer, CancellationToken ct)
    {
        foreach (var batch in Batch(pkList, 1000))
        {
            ct.ThrowIfCancellationRequested();
            string where = $"{Esc(pkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
            string sql = $"SELECT * FROM {table} WHERE {where} ORDER BY {Esc(pkCol)} DESC;";
            await using var cmd = new SqlCommand(sql, cn);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            var cols = Enumerable.Range(0, rdr.FieldCount).Select(i => Esc(rdr.GetName(i))).ToArray();
            while (await rdr.ReadAsync(ct))
            {
                string pkVal = rdr.GetValue(rdr.GetOrdinal(pkCol)).ToString()!;
                if (!_visitedRows.TryAdd($"{canon}|{pkVal}", 0)) continue;
                var vals = new string[rdr.FieldCount];
                for (int i = 0; i < rdr.FieldCount; i++) vals[i] = Lit(rdr.GetValue(i));
                await writer.WriteLineAsync(
                    $"INSERT INTO {table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");
                TotalOut++;
                if (TotalOut % 250 == 0) Report($"   ... {TotalOut:N0} rows written", table);
            }
        }
    }

    async Task DumpFullTable(string table, SqlConnection cn, StreamWriter writer, CancellationToken ct)
    {
        string sql = $"SELECT TOP ({_maxRowsPerTable}) * FROM {table};";
        await using var cmd = new SqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var cols = Enumerable.Range(0, rdr.FieldCount).Select(i => Esc(rdr.GetName(i))).ToArray();
        while (await rdr.ReadAsync(ct))
        {
            var vals = new string[rdr.FieldCount];
            for (int i = 0; i < rdr.FieldCount; i++) vals[i] = Lit(rdr.GetValue(i));
            await writer.WriteLineAsync(
                $"INSERT INTO {table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");
            TotalOut++;
            if (TotalOut % 250 == 0) Report($"   ... {TotalOut:N0} rows written", table);
        }
    }

    // ── DB-mode helpers ──────────────────────────────────────────────────────

    async Task BulkCopyRowsAsync(List<string> pkList, string pkCol, string table, string canon,
        SqlConnection srcCn, SqlConnection destCn, CancellationToken ct)
    {
        var colNames = await _scripter.GetBulkColumnListAsync(srcCn, table, ct);
        var colList = string.Join(", ", colNames.Select(c => Esc(c)));

        foreach (var batch in Batch(pkList, 1000))
        {
            ct.ThrowIfCancellationRequested();

            string where = $"{Esc(pkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
            string sql = $"SELECT {colList} FROM {table} WHERE {where} ORDER BY {Esc(pkCol)} DESC;";

            await using var cmd = new SqlCommand(sql, srcCn);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);

            using var bcp = new SqlBulkCopy(destCn,
                SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, null);
            bcp.DestinationTableName = table;
            bcp.BatchSize = 200;
            bcp.BulkCopyTimeout = 0;

            foreach (var col in colNames)
                bcp.ColumnMappings.Add(col, col);

            var filteringReader = new DeduplicatingDataReader(rdr, canon, pkCol, _visitedRows);
            await bcp.WriteToServerAsync(filteringReader, ct);
            TotalOut += filteringReader.RowsAccepted;

            if (TotalOut % 250 == 0 && TotalOut > 0)
                Report($"   ... {TotalOut:N0} rows copied", table);
        }
    }

    async Task BulkCopyFullTableAsync(string table, SqlConnection srcCn, SqlConnection destCn, CancellationToken ct)
    {
        var colNames = await _scripter.GetBulkColumnListAsync(srcCn, table, ct);
        var colList = string.Join(", ", colNames.Select(c => Esc(c)));

        string sql = $"SELECT TOP ({_maxRowsPerTable}) {colList} FROM {table};";

        await using var cmd = new SqlCommand(sql, srcCn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);

        using var bcp = new SqlBulkCopy(destCn,
            SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls, null);
        bcp.DestinationTableName = table;
        bcp.BatchSize = 200;
        bcp.BulkCopyTimeout = 0;

        foreach (var col in colNames)
            bcp.ColumnMappings.Add(col, col);

        await bcp.WriteToServerAsync(rdr, ct);
    }

    async Task ProcessTable(string table, List<string> pkList, string canon,
        SqlConnection srcCn, SqlConnection destCn, CancellationToken ct)
    {
        Report($"▶ {table} ... exporting up to {_maxRowsPerTable:N0} rows (have {pkList.Count})", table);

        string createSql = await _scripter.GetCreateTableSqlAsync(_connStr, table, ct);
        await using (var createCmd = new SqlCommand(createSql, destCn))
            await createCmd.ExecuteNonQueryAsync(ct);

        var fkSqls = await _scripter.GetForeignKeySqlsAsync(_connStr, table, ct);
        _fkScripts.AddRange(fkSqls);

        string pkCol = table == _rootTable ? _rootPkCol : await GetPkColAsync(srcCn, table, ct);
        await BulkCopyRowsAsync(pkList, pkCol, table, canon, srcCn, destCn, ct);
        await HarvestChildKeys(table, pkList, srcCn, ct);

        _tablesProcessed++;
        Report($"✓ {table} done ({TotalOut:N0} total rows)", table);
    }

    async Task<List<string>> GetAllTablesAsync(SqlConnection cn, CancellationToken ct)
    {
        const string sql = """
            SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name)
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            ORDER BY s.name, t.name;
            """;
        await using var cmd = new SqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        var tables = new List<string>();
        while (await rdr.ReadAsync(ct))
            tables.Add(rdr.GetString(0));
        return tables;
    }

    async Task<HashSet<string>> GetReachableTablesAsync(SqlConnection cn, CancellationToken ct)
    {
        const string sql = """
            SELECT
                parentTab = QUOTENAME(OBJECT_SCHEMA_NAME(fk.referenced_object_id)) + '.' + QUOTENAME(OBJECT_NAME(fk.referenced_object_id)),
                childTab  = QUOTENAME(OBJECT_SCHEMA_NAME(fk.parent_object_id))     + '.' + QUOTENAME(OBJECT_NAME(fk.parent_object_id))
            FROM sys.foreign_keys fk;
            """;

        var adj = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = new SqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync(ct);
        while (await rdr.ReadAsync(ct))
        {
            string parent = Canon(rdr.GetString(0));
            string child = Canon(rdr.GetString(1));

            if (!adj.TryGetValue(parent, out var pSet))
                adj[parent] = pSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pSet.Add(child);

            if (!adj.TryGetValue(child, out var cSet))
                adj[child] = cSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cSet.Add(parent);
        }

        var rootCan = Canon(_rootTable);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootCan };
        var bfsQueue = new Queue<string>();
        bfsQueue.Enqueue(rootCan);

        while (bfsQueue.Count > 0)
        {
            var current = bfsQueue.Dequeue();
            if (adj.TryGetValue(current, out var neighbors))
            {
                foreach (var neighbor in neighbors)
                {
                    if (visited.Add(neighbor))
                        bfsQueue.Enqueue(neighbor);
                }
            }
        }

        return visited;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_outFile is not null)
            await RunFileMode(ct);
        else
            await RunDbMode(ct);
    }

    async Task RunFileMode(CancellationToken ct)
    {
        await using var cn = new SqlConnection(_connStr);
        await cn.OpenAsync(ct);
        await using var writer = new StreamWriter(_outFile!, false, Encoding.UTF8);

        Report($"▶ Starting subset → {_outFile}");

        _rootPkCol = await GetPkColAsync(cn, _rootTable, ct);
        _pkSets[Canon(_rootTable)] = new HashSet<string> { _rootPkVal };
        _queue = new Queue<string>();
        _queue.Enqueue(_rootTable);
        _processedTables = new HashSet<string> { _rootTable };

        while (_queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string table = _queue.Dequeue();
            string can = Canon(table);
            if (_excludedTables.Contains(can)) continue;
            if (!_pkSets.TryGetValue(can, out var pkSet) || pkSet.Count == 0) continue;
            var pkList = pkSet.OrderByDescending(v => v, StringComparer.Ordinal).Take(_maxRowsPerTable).ToList();
            pkSet.Clear();

            Report($"▶ {table} ... exporting up to {_maxRowsPerTable:N0} rows (have {pkList.Count})", table);
            string pkCol = table == _rootTable ? _rootPkCol : await GetPkColAsync(cn, table, ct);
            await DumpRows(pkList, pkCol, table, can, cn, writer, ct);
            await HarvestChildKeys(table, pkList, cn, ct);
            _tablesProcessed++;
            Report($"✓ {table} done ({TotalOut:N0} total rows)", table);
        }

        Report("▶ Pass 2: exporting unrelated tables...");
        var allTables = await GetAllTablesAsync(cn, ct);
        var reachableTables = await GetReachableTablesAsync(cn, ct);
        var alreadyProcessed = new HashSet<string>(_processedTables.Select(Canon), StringComparer.OrdinalIgnoreCase);

        foreach (var table in allTables)
        {
            ct.ThrowIfCancellationRequested();
            var can = Canon(table);
            if (reachableTables.Contains(can) || alreadyProcessed.Contains(can) || _excludedTables.Contains(can))
                continue;
            Report($"▶ {table} (unrelated) ... exporting up to {_maxRowsPerTable:N0} rows", table);
            await DumpFullTable(table, cn, writer, ct);
            _tablesProcessed++;
            Report($"✓ {table} done ({TotalOut:N0} total rows)", table);
        }

        Report($"✓ Complete. {TotalOut:N0} INSERTs written to {_outFile}");
    }

    async Task RunDbMode(CancellationToken ct)
    {
        await using var srcCn = new SqlConnection(_connStr);
        await srcCn.OpenAsync(ct);
        await using var destCn = new SqlConnection(_destConnStr);
        await destCn.OpenAsync(ct);

        Report("▶ Starting DB-to-DB subset");

        // === Pass 1: BFS from root row following FK relationships ===
        _rootPkCol = await GetPkColAsync(srcCn, _rootTable, ct);
        _pkSets[Canon(_rootTable)] = new HashSet<string> { _rootPkVal };

        _queue = new Queue<string>();
        _queue.Enqueue(_rootTable);
        _processedTables = new HashSet<string> { _rootTable };

        while (_queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            string table = _queue.Dequeue();
            string can = Canon(table);

            if (_excludedTables.Contains(can))
                continue;

            if (!_pkSets.TryGetValue(can, out var pkSet) || pkSet.Count == 0)
                continue;

            var pkList = pkSet
                .OrderByDescending(v => v, StringComparer.Ordinal)
                .Take(_maxRowsPerTable)
                .ToList();
            pkSet.Clear();

            await ProcessTable(table, pkList, can, srcCn, destCn, ct);
        }

        // === Pass 2: Dump unrelated tables (no FK path from root) ===
        Report("▶ Pass 2: exporting unrelated tables...");

        var allTables = await GetAllTablesAsync(srcCn, ct);
        var reachableTables = await GetReachableTablesAsync(srcCn, ct);

        var alreadyProcessed = new HashSet<string>(
            _processedTables.Select(Canon), StringComparer.OrdinalIgnoreCase);

        foreach (var table in allTables)
        {
            ct.ThrowIfCancellationRequested();

            var can = Canon(table);

            if (reachableTables.Contains(can))
                continue;
            if (alreadyProcessed.Contains(can))
                continue;
            if (_excludedTables.Contains(can))
                continue;

            Report($"▶ {table} (unrelated) ... exporting up to {_maxRowsPerTable:N0} rows", table);

            string createSql = await _scripter.GetCreateTableSqlAsync(_connStr, table, ct);
            await using (var createCmd = new SqlCommand(createSql, destCn))
                await createCmd.ExecuteNonQueryAsync(ct);

            var fkSqls = await _scripter.GetForeignKeySqlsAsync(_connStr, table, ct);
            _fkScripts.AddRange(fkSqls);

            await BulkCopyFullTableAsync(table, srcCn, destCn, ct);

            _tablesProcessed++;
            Report($"✓ {table} done ({TotalOut:N0} total rows)", table);
        }

        // === Pass 3: Apply FK constraints ===
        Report("▶ Pass 3: applying FK constraints...");

        foreach (var fkSql in _fkScripts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await using var fkCmd = new SqlCommand(fkSql, destCn);
                await fkCmd.ExecuteNonQueryAsync(ct);
            }
            catch (Exception ex)
            {
                Report($"FK warning: {ex.Message}");
            }
        }

        Report($"✓ Complete. {TotalOut:N0} rows copied.");
    }

    private sealed class DeduplicatingDataReader : IDataReader
    {
        private readonly SqlDataReader _inner;
        private readonly string _canon;
        private readonly string _pkCol;
        private readonly ConcurrentDictionary<string, byte> _visited;

        public long RowsAccepted { get; private set; }

        public DeduplicatingDataReader(SqlDataReader inner, string canon, string pkCol,
            ConcurrentDictionary<string, byte> visited)
        {
            _inner = inner;
            _canon = canon;
            _pkCol = pkCol;
            _visited = visited;
        }

        public bool Read()
        {
            while (_inner.Read())
            {
                string pkVal = _inner.GetValue(_inner.GetOrdinal(_pkCol)).ToString()!;
                string rowKey = $"{_canon}|{pkVal}";
                if (_visited.TryAdd(rowKey, 0))
                {
                    RowsAccepted++;
                    return true;
                }
            }
            return false;
        }

        public void Dispose() { /* caller owns _inner */ }

        public object this[int i] => _inner[i];
        public object this[string name] => _inner[name];
        public int Depth => _inner.Depth;
        public bool IsClosed => _inner.IsClosed;
        public int RecordsAffected => _inner.RecordsAffected;
        public int FieldCount => _inner.FieldCount;
        public void Close() => _inner.Close();
        public bool GetBoolean(int i) => _inner.GetBoolean(i);
        public byte GetByte(int i) => _inner.GetByte(i);
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length)
            => _inner.GetBytes(i, fieldOffset, buffer, bufferoffset, length);
        public char GetChar(int i) => _inner.GetChar(i);
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length)
            => _inner.GetChars(i, fieldoffset, buffer, bufferoffset, length);
        public IDataReader GetData(int i) => _inner.GetData(i);
        public string GetDataTypeName(int i) => _inner.GetDataTypeName(i);
        public DateTime GetDateTime(int i) => _inner.GetDateTime(i);
        public decimal GetDecimal(int i) => _inner.GetDecimal(i);
        public double GetDouble(int i) => _inner.GetDouble(i);
        public Type GetFieldType(int i) => _inner.GetFieldType(i);
        public float GetFloat(int i) => _inner.GetFloat(i);
        public Guid GetGuid(int i) => _inner.GetGuid(i);
        public short GetInt16(int i) => _inner.GetInt16(i);
        public int GetInt32(int i) => _inner.GetInt32(i);
        public long GetInt64(int i) => _inner.GetInt64(i);
        public string GetName(int i) => _inner.GetName(i);
        public int GetOrdinal(string name) => _inner.GetOrdinal(name);
        public DataTable? GetSchemaTable() => _inner.GetSchemaTable();
        public string GetString(int i) => _inner.GetString(i);
        public object GetValue(int i) => _inner.GetValue(i);
        public int GetValues(object[] values) => _inner.GetValues(values);
        public bool IsDBNull(int i) => _inner.IsDBNull(i);
        public bool NextResult() => _inner.NextResult();
    }
}
