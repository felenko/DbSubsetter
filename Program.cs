using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using System.Text;

//if (args.Length != 3)
//{
//    Console.Error.WriteLine("Usage: dotnet run -- <conn> <rootTable> <rootPkValue>");
//    return;
//}

var connStr = "";
var rootTable = "[dbo].[Divisions]";

var rootPkVal = "14870";
var outFile = "subset.sql";
// Program.cs  –  .NET 8 single-file console app
// Build:  dotnet build -c Release
// Run  :  dotnet run -- "<conn>" "dbo.Division" 123 "subset.sql"
/* ───────────── SETTINGS ───────── */

/* ───────────── SETTINGS ───────── */
const int MAX_ROWS_PER_TABLE = 1_000;

/* ───────────── HELPERS ────────── */
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

/* ───────────── STATE ──────────── */
var pkSets = new ConcurrentDictionary<string, HashSet<string>>();
var visitedRows = new ConcurrentDictionary<string, byte>();
long totalOut = 0;

/* ───────────── METADATA ───────── */
async Task<string> GetPkColAsync(SqlConnection cn, string table)
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
    return (string?)await cmd.ExecuteScalarAsync()
        ?? throw new InvalidOperationException($"No PK on {table}");
}

async Task<List<FkInfo>> GetReferencingFksAsync(SqlConnection cn, string parentTbl)
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
    await using var rdr = await cmd.ExecuteReaderAsync();
    while (await rdr.ReadAsync())
        list.Add(new FkInfo(rdr.GetString(0), rdr.GetString(1), rdr.GetString(2)));
    return list;
}

/* ───────────── BATCH HELPER ───── */
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
    if (bucket.Count > 0) yield return bucket;
}

/* ───────────── MAIN RUN ───────── */
await using var cn = new SqlConnection(connStr);
await cn.OpenAsync();
await using var writer = new StreamWriter(outFile, false, Encoding.UTF8);

Console.WriteLine($"▶ Starting subset → {outFile}");

string rootPkCol = await GetPkColAsync(cn, rootTable);
pkSets[Canon(rootTable)] = new HashSet<string> { rootPkVal };

var queue = new Queue<string>();
queue.Enqueue(rootTable); // real name for SQL
HashSet<string> processedTables = new HashSet<string>();
processedTables.Add(rootTable);

while (queue.Count > 0)
{
    string table = queue.Dequeue();
    string can = Canon(table);

    if (!pkSets.TryGetValue(can, out var pkSet) || pkSet.Count == 0) continue;

    var pkList = pkSet
        .OrderByDescending(v => v, StringComparer.Ordinal)
        .Take(MAX_ROWS_PER_TABLE)
        .ToList();
    pkSet.Clear();                                        // free memory

    Console.WriteLine($"▶ {table} … exporting up to {MAX_ROWS_PER_TABLE:N0} rows (have {pkList.Count})");

    string pkCol = table == rootTable ? rootPkCol : await GetPkColAsync(cn, table);

    /* 1️⃣ dump rows */
    foreach (var batch in Batch(pkList, 1000))
    {
        string where = $"{Esc(pkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
        string sql = $"SELECT TOP ({MAX_ROWS_PER_TABLE})* FROM {table} WHERE {where} ORDER BY {Esc(pkCol)} DESC;";

        await using var cmd = new SqlCommand(sql, cn);
        await using var rdr = await cmd.ExecuteReaderAsync();

        var cols = Enumerable.Range(0, rdr.FieldCount)
                             .Select(i => Esc(rdr.GetName(i))).ToArray();

        while (await rdr.ReadAsync())
        {
            string pkVal = rdr.GetValue(rdr.GetOrdinal(pkCol)).ToString()!;
            string rowKey = $"{can}|{pkVal}";
            if (!visitedRows.TryAdd(rowKey, 0)) continue; // already emitted

            var vals = new string[rdr.FieldCount];
            for (int i = 0; i < rdr.FieldCount; i++)
                vals[i] = Lit(rdr.GetValue(i));

            await writer.WriteLineAsync(
                $"INSERT INTO {table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");

            if (++totalOut % 250 == 0)
                Console.WriteLine($"   … {totalOut:N0} rows written");
        }
    }

    /* 2️⃣ harvest child PKs */
    var fks = await GetReferencingFksAsync(cn, table);
    foreach (var fk in fks)
    {
        string childCan = Canon(fk.ChildTable);
        if (!pkSets.TryGetValue(childCan, out var childSet))
        {
            childSet = new HashSet<string>();
            pkSets[childCan] = childSet;
        }
        if (childSet.Count >= MAX_ROWS_PER_TABLE) continue;

        foreach (var batch in Batch(pkList, 1000))
        {
            string where = $"{Esc(fk.ChildFkCol)} IN ({string.Join(", ", batch.Select(Lit))})";
            string sql = $"""
                SELECT TOP ({MAX_ROWS_PER_TABLE}) {Esc(fk.ChildPkCol)}
                FROM   {fk.ChildTable}
                WHERE  {where}
                ORDER  BY {Esc(fk.ChildPkCol)} DESC;
            """;

            await using var cmd = new SqlCommand(sql, cn);
            try
            {
                await using var rdr = await cmd.ExecuteReaderAsync();
            

                bool added = false;
                while (await rdr.ReadAsync() && childSet.Count < MAX_ROWS_PER_TABLE)
                added |= childSet.Add(rdr[0].ToString()!);

                if (added)
                {
                    if (!processedTables.Contains(fk.ChildTable)){
                    queue.Enqueue(fk.ChildTable);      // schedule for
                    processedTables.Add(fk.ChildTable); // next run
                    }
                }
            }
            catch (Exception ex)
            {
            }
        }
    }
}

Console.WriteLine($"✔ Finished. {totalOut:N0} INSERTs written to {outFile}");

/* ───────────── RECORD ─────────── */
record FkInfo(string ChildTable, string ChildPkCol, string ChildFkCol);