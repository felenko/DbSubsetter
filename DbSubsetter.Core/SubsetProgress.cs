namespace DbSubsetter.Core;

public record SubsetProgress(
    string Message,
    string CurrentTable,
    long TotalRows,
    int TablesProcessed,
    int TablesQueued);
