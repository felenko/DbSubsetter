
using Serilog;
using System.Diagnostics;
using DbSubsetter.Core;

string connStr = args[0];
var rootTable = "[dbo].[Divisions]";
var rootPkVal = "14870";
var outFile = "subset.sql";

Log.Logger = new LoggerConfiguration()
    .WriteTo.Async(a => a.Console())
    .CreateLogger();

Stopwatch sw = Stopwatch.StartNew();

var progress = new Progress<SubsetProgress>(p => Log.Information(p.Message));
var engine = new SubsetEngine(connStr, rootTable, rootPkVal, outFile, progress: progress);
await engine.RunAsync();

Console.WriteLine($"Finished in {sw.Elapsed:m\\:ss}. {engine.TotalOut:N0} INSERTs written to {outFile}");
