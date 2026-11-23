namespace DbAccessManager.Data;

public enum DatabaseEngine
{
    SqlServer = 0,
    PostgreSql = 1,
    MySql = 2,
    MariaDb = 3
}

public record ConnectionStringTemplate(string Name, string ConnectionString, string? Description = null);

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

    public static IReadOnlyList<ConnectionStringTemplate> GetTemplates(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.SqlServer =>
        [
            new("Standard (SQL Auth)", "Server=localhost,1433;Database=mydb;User Id=sa;Password=your_password;TrustServerCertificate=True;", "Standard SQL Server authentication with certificate trust"),
            new("Integrated Security (Windows)", "Server=localhost,1433;Database=mydb;Integrated Security=True;TrustServerCertificate=True;", "Windows or Active Directory domain authentication"),
            new("Azure SQL / Cloud", "Server=tcp:your-server.database.windows.net,1433;Database=mydb;User ID=your-admin;Password=your_password;Encrypt=True;TrustServerCertificate=False;", "Azure SQL Database with encryption enabled")
        ],
        DatabaseEngine.PostgreSql =>
        [
            new("Standard (Key-Value)", "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=your_password", "Standard key-value connection parameters"),
            new("SSL Required (Cloud / RDS)", "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=your_password;SSL Mode=Require;Trust Server Certificate=true", "Cloud-hosted PostgreSQL with SSL required"),
            new("With Application Name", "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=your_password;Application Name=QueryGate;Include Error Detail=true", "PostgreSQL connection with application identifier and detailed errors")
        ],
        DatabaseEngine.MySql =>
        [
            new("Standard (User ID)", "Server=localhost;Port=3306;Database=mydb;User ID=root;Password=your_password;", "Standard MySQL authentication"),
            new("SSL Preferred", "Server=localhost;Port=3306;Database=mydb;User ID=root;Password=your_password;SslMode=Preferred;", "MySQL with preferred SSL encryption"),
            new("Custom Charset (utf8mb4)", "Server=localhost;Port=3306;Database=mydb;User ID=root;Password=your_password;CharSet=utf8mb4;", "MySQL with full 4-byte UTF-8 character encoding")
        ],
        DatabaseEngine.MariaDb =>
        [
            new("Standard (User ID)", "Server=localhost;Port=3306;Database=mydb;User ID=root;Password=your_password;", "Standard MariaDB authentication"),
            new("SSL Preferred", "Server=localhost;Port=3306;Database=mydb;User ID=root;Password=your_password;SslMode=Preferred;", "MariaDB with preferred SSL encryption"),
            new("Custom Charset (utf8mb4)", "Server=localhost;Port=3306;Database=mydb;User ID=root;Password=your_password;CharSet=utf8mb4;", "MariaDB with full 4-byte UTF-8 character encoding")
        ],
        _ => []
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
