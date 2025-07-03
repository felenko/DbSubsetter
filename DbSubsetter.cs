using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;


namespace DbSubsetter
{
    internal class DbSubsetter(string connStr, string rootTable, string rootPkVal, string outFile, TaskScheduler uiScheduler)
    {

        private const int MAX_ROWS_PER_TABLE = 1000;
        private string connStr = connStr;
        private string rootTable = rootTable;
        private string rootPkVal = rootPkVal;
        private string outFile = outFile;
        private TaskScheduler uiContext = uiScheduler;


        private Queue<string> queue;
        private HashSet<string> processedTables;
        private string rootPkCol;
        private object syncobject = new object();

        protected void Log(string message)
        {
            lock (this.syncobject)
            {
                Serilog.Log.Logger.Information(message);
            }
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

        /* ───────────── STATE ──────────── */
        private ConcurrentDictionary<string, HashSet<string>> pkSets =
            new ConcurrentDictionary<string, HashSet<string>>();
        private ConcurrentDictionary<string, byte> visitedRows = new ConcurrentDictionary<string, byte>();
        public long totalOut = 0;
        

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

            if (bucket.Count > 0)
            {
                yield return bucket;
            }
        }

        async Task DumpRows(List<string> pkList1, string s, string table1, string can1, SqlConnection cn,
            StreamWriter writer)
        {
            foreach (var batch in Batch(pkList1, 1000))
            {
                string where = $"{Esc(s)} IN ({string.Join(", ", batch.Select(Lit))})";
                string sql = $"SELECT TOP ({MAX_ROWS_PER_TABLE})* FROM {table1} WHERE {where} ORDER BY {Esc(s)} DESC;";

                await using var cmd = new SqlCommand(sql, cn);
                await using var rdr = await cmd.ExecuteReaderAsync();

                var cols = Enumerable.Range(0, rdr.FieldCount)
                    .Select(i => Esc(rdr.GetName(i))).ToArray();

                while (await rdr.ReadAsync())
                {
                    string pkVal = rdr.GetValue(rdr.GetOrdinal(s)).ToString()!;
                    string rowKey = $"{can1}|{pkVal}";
                    if (!this.visitedRows.TryAdd(rowKey, 0))
                    {
                        continue; // already emitted
                    }

                    var vals = new string[rdr.FieldCount];
                    for (int i = 0; i < rdr.FieldCount; i++)
                        vals[i] = Lit(rdr.GetValue(i));

                    await writer.WriteLineAsync(
                        $"INSERT INTO {table1} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", vals)});");

                    if (++this.totalOut % 250 == 0)
                    {
                        Log($"   … {this.totalOut:N0} rows written");
                    }
                }
            }
        }
        /* ───────────── MAIN RUN ───────── */

        async Task HarverstChildKeys(string s1, List<string> list1, SqlConnection cn)
        {
            /* 2️⃣ harvest child PKs */
            var fks = await this.GetReferencingFksAsync(cn, s1);
            foreach (var fk in fks)
            {
                string childCan = Canon(fk.ChildTable);
                if (!this.pkSets.TryGetValue(childCan, out var childSet))
                {
                    childSet = new HashSet<string>();
                    this.pkSets[childCan] = childSet;
                }

                if (childSet.Count >= MAX_ROWS_PER_TABLE)
                {
                    continue;
                }

                var batches = Batch(list1, 1000);
                foreach (var batch in batches)
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
                            if (!this.processedTables.Contains(fk.ChildTable))
                            {
                                this.queue.Enqueue(fk.ChildTable); // schedule for
                                this.processedTables.Add(fk.ChildTable); // next run
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                    }
                }
            }
        }

        async Task ProcessTable(string s, List<string> list, string can1, SqlConnection cn, StreamWriter writer)
        {
            Log($"▶ {s} … exporting up to {MAX_ROWS_PER_TABLE:N0} rows (have {list.Count})");

            string pkCol = s == this.rootTable ? this.rootPkCol : await this.GetPkColAsync(cn, s);

            /* 1️⃣ dump rows */
            await this.DumpRows(list, pkCol, s, can1, cn, writer);

            await this.HarverstChildKeys(s, list, cn);
        }

        public async Task ProcessTablesFromRoot()
        {
            try
            {
                
                await using var cn = new SqlConnection(this.connStr);
                await cn.OpenAsync();
                await using var writer = new StreamWriter(this.outFile, false, Encoding.UTF8);

                Log($"▶ Starting subset → {this.outFile}");

                this.rootPkCol = await this.GetPkColAsync(cn, this.rootTable);
                this.pkSets[Canon(this.rootTable)] = new HashSet<string>
                {
                    this.rootPkVal
                };

                this.queue = new Queue<string>();
                this.queue.Enqueue(this.rootTable); // real name for SQL
                this.processedTables = new HashSet<string>();
                this.processedTables.Add(this.rootTable);

                while (this.queue.Count > 0)
                {
                    string table = this.queue.Dequeue();
                    string can = Canon(table);

                    if (!this.pkSets.TryGetValue(can, out var pkSet) || pkSet.Count == 0)
                    {
                        continue;
                    }

                    var pkList = pkSet
                        .OrderByDescending(v => v, StringComparer.Ordinal)
                        .Take(MAX_ROWS_PER_TABLE)
                        .ToList();
                    pkSet.Clear(); // free memory

                    await this.ProcessTable(table, pkList, can, cn, writer);
                }
            }
            catch (Exception ex)
            {

            }
        }

    }
}
