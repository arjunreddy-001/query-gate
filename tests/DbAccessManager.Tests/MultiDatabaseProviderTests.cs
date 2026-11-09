using DbAccessManager.Data;
using DbAccessManager.Services.Providers;
using Xunit;

namespace DbAccessManager.Tests;

public class MultiDatabaseProviderTests
{
    private readonly DatabaseProviderFactory _factory = new();

    [Theory]
    [InlineData(DatabaseEngine.SqlServer, "SQL Server")]
    [InlineData(DatabaseEngine.PostgreSql, "PostgreSQL")]
    [InlineData(DatabaseEngine.MySql, "MySQL")]
    [InlineData(DatabaseEngine.MariaDb, "MariaDB")]
    public void Factory_returns_correct_provider(DatabaseEngine engine, string expectedName)
    {
        var provider = _factory.GetProvider(engine);
        Assert.NotNull(provider);
        Assert.Equal(engine, provider.Engine);
        Assert.Equal(expectedName, provider.DisplayName);
    }

    [Fact]
    public void Factory_defaults_to_sqlserver_for_unknown()
    {
        var provider = _factory.GetProvider((DatabaseEngine)999);
        Assert.NotNull(provider);
        Assert.Equal(DatabaseEngine.SqlServer, provider.Engine);
    }

    [Theory]
    [InlineData(DatabaseEngine.SqlServer, 1433)]
    [InlineData(DatabaseEngine.PostgreSql, 5432)]
    [InlineData(DatabaseEngine.MySql, 3306)]
    [InlineData(DatabaseEngine.MariaDb, 3306)]
    public void Default_ports_are_standard(DatabaseEngine engine, int expectedPort)
    {
        Assert.Equal(expectedPort, DatabaseEngineInfo.GetDefaultPort(engine));
    }

    [Theory]
    [InlineData(DatabaseEngine.SqlServer, "Server=localhost,1433;Database=test;User Id=sa;Password=secret;TrustServerCertificate=True;", true)]
    [InlineData(DatabaseEngine.SqlServer, "InvalidConnectionStringWithoutKeyValue", false)]
    [InlineData(DatabaseEngine.PostgreSql, "Host=localhost;Port=5432;Database=test;Username=postgres;Password=secret", true)]
    [InlineData(DatabaseEngine.PostgreSql, "Host=localhost;InvalidKeywordParameter=123", false)]
    [InlineData(DatabaseEngine.MySql, "Server=localhost;Port=3306;Database=test;User ID=root;Password=secret;", true)]
    [InlineData(DatabaseEngine.MySql, "Server=localhost;InvalidOptionNonExistent=abc;", false)]
    [InlineData(DatabaseEngine.MariaDb, "Server=localhost;Port=3306;Database=test;User ID=root;Password=secret;", true)]
    public void Validate_connection_string_checks_format(DatabaseEngine engine, string connStr, bool expectedValid)
    {
        var provider = _factory.GetProvider(engine);
        var (valid, error) = provider.ValidateConnectionString(connStr);
        Assert.Equal(expectedValid, valid);
        if (!expectedValid)
        {
            Assert.NotNull(error);
        }
    }

    [Theory]
    [InlineData("postgres", DatabaseEngine.PostgreSql)]
    [InlineData("PostgreSQL", DatabaseEngine.PostgreSql)]
    [InlineData("mysql", DatabaseEngine.MySql)]
    [InlineData("MariaDB", DatabaseEngine.MariaDb)]
    [InlineData("sqlserver", DatabaseEngine.SqlServer)]
    [InlineData("unknown", DatabaseEngine.SqlServer)]
    public void Parse_engine_helper_parses_synonyms(string input, DatabaseEngine expected)
    {
        Assert.Equal(expected, DatabaseEngineInfo.ParseOrDefault(input));
    }

    [Fact]
    public void AllEngines_contains_all_four_database_types()
    {
        Assert.Equal(4, DatabaseEngineInfo.AllEngines.Length);
        Assert.Contains(DatabaseEngine.SqlServer, DatabaseEngineInfo.AllEngines);
        Assert.Contains(DatabaseEngine.PostgreSql, DatabaseEngineInfo.AllEngines);
        Assert.Contains(DatabaseEngine.MySql, DatabaseEngineInfo.AllEngines);
        Assert.Contains(DatabaseEngine.MariaDb, DatabaseEngineInfo.AllEngines);
    }

    [Theory]
    [InlineData(DatabaseEngine.SqlServer, "engine-sqlserver")]
    [InlineData(DatabaseEngine.PostgreSql, "engine-postgres")]
    [InlineData(DatabaseEngine.MySql, "engine-mysql")]
    [InlineData(DatabaseEngine.MariaDb, "engine-mariadb")]
    public void Badge_class_contains_engine_class(DatabaseEngine engine, string expectedClass)
    {
        var badge = DatabaseEngineInfo.GetBadgeClass(engine);
        Assert.Contains(expectedClass, badge);
    }

    [Theory]
    [InlineData(DatabaseEngine.SqlServer)]
    [InlineData(DatabaseEngine.PostgreSql)]
    [InlineData(DatabaseEngine.MySql)]
    [InlineData(DatabaseEngine.MariaDb)]
    public void Sample_connection_strings_are_valid_for_their_provider(DatabaseEngine engine)
    {
        var sample = DatabaseEngineInfo.GetSampleConnectionString(engine);
        Assert.False(string.IsNullOrWhiteSpace(sample));
        var provider = _factory.GetProvider(engine);
        var (valid, error) = provider.ValidateConnectionString(sample);
        Assert.True(valid, $"Sample for {engine} failed validation: {error}");
    }

    [Theory]
    [InlineData(DatabaseEngine.SqlServer)]
    [InlineData(DatabaseEngine.PostgreSql)]
    [InlineData(DatabaseEngine.MySql)]
    [InlineData(DatabaseEngine.MariaDb)]
    public void Translate_exception_handles_timeouts_and_errors(DatabaseEngine engine)
    {
        var provider = _factory.GetProvider(engine);
        var (timeoutOutcome, timeoutMsg, _) = provider.TranslateException(new TimeoutException("Timed out"), 30);
        Assert.Equal("Timeout", timeoutOutcome);
        Assert.Contains("30s", timeoutMsg);

        var (errOutcome, errMsg, _) = provider.TranslateException(new InvalidOperationException("Failed table"), 30);
        Assert.Equal("Error", errOutcome);
        Assert.Equal("Failed table", errMsg);
    }
}
