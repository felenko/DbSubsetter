namespace DbSubsetter.Core;

public static class SchemaExplorerFactory
{
    public static ISchemaExplorer Create(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer => new SchemaExplorer(),
        DatabaseProvider.SQLite => new SchemaExplorerSqlite(),
        DatabaseProvider.MySql => new SchemaExplorerMySql(),
        DatabaseProvider.Postgres => new SchemaExplorerPostgres(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };
}
