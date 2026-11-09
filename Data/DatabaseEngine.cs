namespace DbAccessManager.Data;

public enum DatabaseEngine
{
    SqlServer = 0,
    PostgreSql = 1,
    MySql = 2,
    MariaDb = 3
}

public static class DatabaseEngineInfo
{
    public static readonly DatabaseEngine[] AllEngines =
    [
        DatabaseEngine.SqlServer,
        DatabaseEngine.PostgreSql,
        DatabaseEngine.MySql,
        DatabaseEngine.MariaDb
    ];

    public static string GetDisplayName(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.SqlServer => "SQL Server",
        DatabaseEngine.PostgreSql => "PostgreSQL",
        DatabaseEngine.MySql => "MySQL",
        DatabaseEngine.MariaDb => "MariaDB",
        _ => engine.ToString()
    };

    public static int GetDefaultPort(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.SqlServer => 1433,
        DatabaseEngine.PostgreSql => 5432,
        DatabaseEngine.MySql => 3306,
        DatabaseEngine.MariaDb => 3306,
        _ => 0
    };

    public static string GetSampleConnectionString(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.SqlServer => "Server=localhost,1433;Database=mydb;User Id=sa;Password=your_password;TrustServerCertificate=True;",
        DatabaseEngine.PostgreSql => "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=your_password",
        DatabaseEngine.MySql => "Server=localhost;Port=3306;Database=mydb;User ID=root;Password=your_password;",
        DatabaseEngine.MariaDb => "Server=localhost;Port=3306;Database=mydb;User ID=root;Password=your_password;",
        _ => ""
    };

    public static string GetBadgeClass(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.SqlServer => "badge-engine engine-sqlserver",
        DatabaseEngine.PostgreSql => "badge-engine engine-postgres",
        DatabaseEngine.MySql => "badge-engine engine-mysql",
        DatabaseEngine.MariaDb => "badge-engine engine-mariadb",
        _ => "badge-engine"
    };

    public static DatabaseEngine ParseOrDefault(string? value, DatabaseEngine fallback = DatabaseEngine.SqlServer)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (Enum.TryParse<DatabaseEngine>(value, true, out var result)) return result;
        return value.ToLowerInvariant() switch
        {
            "postgres" or "postgresql" or "pgsql" or "npgsql" => DatabaseEngine.PostgreSql,
            "mysql" => DatabaseEngine.MySql,
            "mariadb" or "maria" => DatabaseEngine.MariaDb,
            "mssql" or "sqlserver" or "sql_server" => DatabaseEngine.SqlServer,
            _ => fallback
        };
    }
}
