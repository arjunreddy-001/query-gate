using DbAccessManager.Data;

namespace DbAccessManager.Services.Providers;

public sealed class DatabaseProviderFactory
{
    private readonly Dictionary<DatabaseEngine, IDatabaseProvider> _providers;

    public DatabaseProviderFactory(IDictionary<DatabaseEngine, IDatabaseProvider>? customProviders = null)
    {
        _providers = new Dictionary<DatabaseEngine, IDatabaseProvider>
        {
            [DatabaseEngine.SqlServer] = new SqlServerDatabaseProvider(),
            [DatabaseEngine.PostgreSql] = new PostgreSqlDatabaseProvider(),
            [DatabaseEngine.MySql] = new MySqlDatabaseProvider(),
            [DatabaseEngine.MariaDb] = new MariaDbDatabaseProvider()
        };
        if (customProviders != null)
        {
            foreach (var (engine, provider) in customProviders)
            {
                _providers[engine] = provider;
            }
        }
    }

    public IDatabaseProvider GetProvider(DatabaseEngine engine) =>
        _providers.TryGetValue(engine, out var provider)
            ? provider
            : _providers[DatabaseEngine.SqlServer];

    public IEnumerable<IDatabaseProvider> GetAllProviders() => _providers.Values;
}
