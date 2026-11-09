using DbAccessManager.Data;

namespace DbAccessManager.Services.Providers;

public sealed class MariaDbDatabaseProvider : MySqlDatabaseProvider
{
    public override DatabaseEngine Engine => DatabaseEngine.MariaDb;
    public override string DisplayName => "MariaDB";
}
