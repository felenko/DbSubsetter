using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Text;

namespace DbSubsetter.Core;

public class SubsetEngine
{
    private readonly string _connStr;
    private readonly string _rootTable;
    private readonly string _rootPkVal;
    private readonly string _outFile;
    private readonly int _maxRowsPerTable;
    private readonly IProgress<SubsetProgress>? _progress;

    private Queue<string> _queue = new();
    private HashSet<string> _processedTables = new();
    private string _rootPkCol = string.Empty;
    private int _tablesProcessed;

    private readonly ConcurrentDictionary<string, HashSet<string>> _pkSets = new();
    private readonly ConcurrentDictionary<string, byte> _visitedRows = new();

    public long TotalOut { get; private set; }

    public SubsetEngine(string connStr, string rootTable, string rootPkVal, string outFile,
        int maxRowsPerTable = 1000, IProgress<SubsetProgress>? progress = null)
    {
        _connStr = connStr;
        _rootTable = rootTable;
        _rootPkVal = rootPkVal;
        _outFile = outFile;
        _maxRowsPerTable = maxRowsPerTable;
        _progress = progress;
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

    async Task DumpRows(List<string> pkList, string pkCol, string table, string canon,
        SqlConnection cn, StreamWriter writer, CancellationToken ct)
    {
        foreach (var batch in Batch(pkList, 1000))
        {
            ct.ThrowIfCancellationRequested();

            string where = $"{Esc(pkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
            string sql = $"SELECT TOP ({_maxRowsPerTable})* FROM {table} WHERE {where} ORDER BY {Esc(pkCol)} DESC;";

            await using var cmd = new SqlCommand(sql, cn);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);

            var cols = Enumerable.Range(0, rdr.FieldCount)
                .Select(i => Esc(rdr.GetName(i))).ToArray();

            while (await rdr.ReadAsync(ct))
            {
                string pkVal = rdr.GetValue(rdr.GetOrdinal(pkCol)).ToString()!;
                string rowKey = $"{canon}|{pkVal}";
                if (!_visitedRows.TryAdd(rowKey, 0))
                    continue;

                var vals = new string[rdr.FieldCount];
                for (int i = 0; i < rdr.FieldCount; i++)
                    vals[i] = Lit(rdr.GetValue(i));

                await writer.WriteLineAsync(
                    $"INSERT INTO {table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");

                TotalOut++;
                if (TotalOut % 250 == 0)
                    Report($"   ... {TotalOut:N0} rows written", table);
            }
        }
    }

    async Task HarvestChildKeys(string parentTable, List<string> parentPkList,
        SqlConnection cn, CancellationToken ct)
    {
        var fks = await GetReferencingFksAsync(cn, parentTable, ct);
        foreach (var fk in fks)
        {
            ct.ThrowIfCancellationRequested();

            string childCan = Canon(fk.ChildTable);
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

    async Task ProcessTable(string table, List<string> pkList, string canon,
        SqlConnection cn, StreamWriter writer, CancellationToken ct)
    {
        Report($"▶ {table} ... exporting up to {_maxRowsPerTable:N0} rows (have {pkList.Count})", table);

        string pkCol = table == _rootTable ? _rootPkCol : await GetPkColAsync(cn, table, ct);

        await DumpRows(pkList, pkCol, table, canon, cn, writer, ct);
        await HarvestChildKeys(table, pkList, cn, ct);

        _tablesProcessed++;
        Report($"✓ {table} done ({TotalOut:N0} total rows)", table);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await using var cn = new SqlConnection(_connStr);
        await cn.OpenAsync(ct);
        await using var writer = new StreamWriter(_outFile, false, Encoding.UTF8);

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

            if (!_pkSets.TryGetValue(can, out var pkSet) || pkSet.Count == 0)
                continue;

            var pkList = pkSet
                .OrderByDescending(v => v, StringComparer.Ordinal)
                .Take(_maxRowsPerTable)
                .ToList();
            pkSet.Clear();

            await ProcessTable(table, pkList, can, cn, writer, ct);
        }

        Report($"✓ Complete. {TotalOut:N0} INSERTs written to {_outFile}");
    }
}
