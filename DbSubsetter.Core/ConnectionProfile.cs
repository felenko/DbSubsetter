using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace DbSubsetter.Core;

public enum DatabaseProvider
{
    SqlServer,
    SQLite,
    MySql,
    Postgres
}

public class ConnectionProfile
{
    public string Name { get; set; } = string.Empty;
    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;
    /// <summary>SQL Server: server name. SQLite: full path to .db file.</summary>
    public string Server { get; set; } = string.Empty;
    /// <summary>SQL Server: database name. SQLite: unused.</summary>
    public string Database { get; set; } = string.Empty;
    public bool IntegratedSecurity { get; set; } = true;
    public string Username { get; set; } = string.Empty;

    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    public string ToConnectionString()
    {
        if (Provider == DatabaseProvider.SQLite)
        {
            var path = Server.Trim();
            if (string.IsNullOrEmpty(path)) return "";
            return $"Data Source={path}";
        }
        if (Provider == DatabaseProvider.MySql)
        {
            return $"Server={Server};Database={Database};User Id={Username};Password={Password};";
        }
        if (Provider == DatabaseProvider.Postgres)
        {
            return $"Host={Server};Database={Database};Username={Username};Password={Password};";
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            IntegratedSecurity = IntegratedSecurity,
            TrustServerCertificate = true,
            Encrypt = SqlConnectionEncryptOption.Optional
        };

        if (!IntegratedSecurity)
        {
            builder.UserID = Username;
            builder.Password = Password;
        }

        return builder.ConnectionString;
    }
}
