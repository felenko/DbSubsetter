namespace DbSubsetter.Core;

public record ColumnInfo(
    string Name,
    string DataType,
    bool IsPrimaryKey,
    string? FkReferencedTable,
    string? FkReferencedColumn);
