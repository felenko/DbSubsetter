using DbSubsetter.Core;

namespace DbSubsetter.UI;

public class TableRule
{
    public string Name { get; set; } = string.Empty;
    public bool IsIncluded { get; set; } = true;
    public string WhereClause { get; set; } = string.Empty;
}

public class SubsetProject
{
    // Source connection
    public DatabaseProvider SourceProvider { get; set; } = DatabaseProvider.SqlServer;
    public string SourceServer { get; set; } = string.Empty;
    public string SourceDatabase { get; set; } = string.Empty;
    public bool SourceIntegratedSecurity { get; set; } = true;
    public string SourceUsername { get; set; } = string.Empty;
    // Password intentionally not persisted

    // Root entry point
    public string? RootTable { get; set; }
    public string? RootPkValue { get; set; }

    // Subsetting limits
    public int MaxRowsPerTable { get; set; } = 1000;

    /// <summary>Row limit used in the Browser data preview panels.</summary>
    public int BrowserRowLimit { get; set; } = 100;

    // Table rules (include/exclude + optional WHERE filter)
    public List<TableRule> TableRules { get; set; } = new();

    // Output / destination
    public bool IsFileMode { get; set; } = true;
    public string OutputFile { get; set; } = "subset.sql";

    // Destination DB (used when IsFileMode = false)
    public string DestServer { get; set; } = string.Empty;
    public string DestDatabase { get; set; } = string.Empty;
    public bool DestIntegratedSecurity { get; set; } = true;
    public string DestUsername { get; set; } = string.Empty;
    // Destination password intentionally not persisted
}
