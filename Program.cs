using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;
using Serilog;
using System.Diagnostics;
using System.Text;

string connStr = args[0];
var rootTable = "[dbo].[Divisions]";

var rootPkVal = "14870";
var outFile = "subset.sql";
// Program.cs  –  .NET 8 single-file console app
// Build:  dotnet build -c Release
// Run  :  dotnet run -- "<conn>" "dbo.Division" 123 "subset.sql"
/* ───────────── SETTINGS ───────── */

/* ───────────── SETTINGS ───────── */
const int MAX_ROWS_PER_TABLE = 1_000;
Log.Logger = new LoggerConfiguration()
    .WriteTo.Async(a => a.Console())
    .CreateLogger();

/* ───────────── HELPERS ────────── */


Stopwatch sw = Stopwatch.StartNew();
SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
var dbSubsetter = new DbSubsetter.DbSubsetter(connStr, rootTable, rootPkVal, outFile, TaskScheduler.FromCurrentSynchronizationContext());
await dbSubsetter.ProcessTablesFromRoot();

Console.WriteLine($"Finished. in {sw.Elapsed:m:ss} {dbSubsetter.totalOut:N0} INSERTs written to {outFile}");


//if (args.Length != 3)
//{
//    Console.Error.WriteLine("Usage: dotnet run -- <conn> <rootTable> <rootPkValue>");
//    return;
//}

/* ───────────── RECORD ─────────── */
record FkInfo(string ChildTable, string ChildPkCol, string ChildFkCol);