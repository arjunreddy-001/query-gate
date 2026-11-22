using DbAccessManager.Data;
using DbAccessManager.Services.Providers;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace DbAccessManager.Tests;

public class LiveContainerIntegrationTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private bool _dockerAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("test_db")
                .WithUsername("test_user")
                .WithPassword("test_password_123!")
                .Build();

            await _container.StartAsync();
            _dockerAvailable = true;
        }
        catch
        {
            _dockerAvailable = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostgreSql_LiveContainer_ValidatesConnection_Metadata_And_Telemetry()
    {
        if (!_dockerAvailable || _container is null)
        {
            // If Docker daemon is unavailable in the environment, skip gracefully
            return;
        }

        var connStr = _container.GetConnectionString();
        var provider = new PostgreSqlDatabaseProvider();

        // 1. Connection string validation
        var (valid, error) = provider.ValidateConnectionString(connStr);
        Assert.True(valid, error);

        // 2. Test connection
        var (connected, message) = await provider.TestAsync(connStr);
        Assert.True(connected, message);

        // 3. Seed schema and tables in the live PostgreSQL container
        await using (var conn = new NpgsqlConnection(connStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE customers (
                    id SERIAL PRIMARY KEY,
                    name VARCHAR(100) NOT NULL,
                    email VARCHAR(200) NOT NULL
                );

                CREATE TABLE orders (
                    id SERIAL PRIMARY KEY,
                    customer_id INT NOT NULL REFERENCES customers(id),
                    amount NUMERIC(10, 2) NOT NULL,
                    created_at TIMESTAMPTZ DEFAULT NOW()
                );

                INSERT INTO customers (name, email) VALUES ('Alice', 'alice@example.com'), ('Bob', 'bob@example.com');
                INSERT INTO orders (customer_id, amount) VALUES (1, 99.50), (2, 149.00);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // 4. Load metadata through the provider
        var metadata = await provider.LoadMetadataAsync(connStr);
        Assert.NotEmpty(metadata);

        var customersTable = metadata.FirstOrDefault(m => m.Name == "customers");
        Assert.NotNull(customersTable);
        Assert.Contains("id", customersTable.Columns);
        Assert.Contains("name", customersTable.Columns);
        Assert.Contains("email", customersTable.Columns);

        var ordersTable = metadata.FirstOrDefault(m => m.Name == "orders");
        Assert.NotNull(ordersTable);
        Assert.Contains("customer_id", ordersTable.Columns);
        Assert.Contains("amount", ordersTable.Columns);

        var fk = ordersTable.ForeignKeys.FirstOrDefault(f => f.ReferencedTable == "customers");
        Assert.NotNull(fk);
        Assert.Equal("id", fk.Columns[0].ReferencedColumn);

        // 5. Collect live telemetry
        var telemetry = await provider.CollectTelemetryAsync(connStr);
        Assert.NotNull(telemetry);
        Assert.True(telemetry.ActiveConnections >= 1);
        Assert.False(string.IsNullOrWhiteSpace(telemetry.DatabaseSize));

        // 6. Generate and parse live execution plan
        var plan = await provider.ExplainPlanAsync(connStr, "SELECT c.name, o.amount FROM customers c JOIN orders o ON c.id = o.customer_id;", 30);
        Assert.NotNull(plan);
        Assert.True(plan.TotalCost > 0 || !string.IsNullOrWhiteSpace(plan.RawXml));
    }
}
