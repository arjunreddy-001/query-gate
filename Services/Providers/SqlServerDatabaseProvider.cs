using System.Data.Common;
using System.Text;
using DbAccessManager.Data;
using Microsoft.Data.SqlClient;

namespace DbAccessManager.Services.Providers;

public sealed class SqlServerDatabaseProvider : IDatabaseProvider
{
    public DatabaseEngine Engine => DatabaseEngine.SqlServer;
    public string DisplayName => "SQL Server";

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public (bool Valid, string? Error) ValidateConnectionString(string connectionString)
    {
        try
        {
            _ = new SqlConnectionStringBuilder(connectionString);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<(bool Ok, string Message)> TestAsync(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString) { ConnectTimeout = 10 };
            await using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            return (true, $"Connected to {conn.DataSource} / {conn.Database} (SQL Server {conn.ServerVersion}).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void RegisterMessageListener(DbConnection connection, Action<string> onMessage)
    {
        if (connection is SqlConnection sqlConn)
        {
            sqlConn.InfoMessage += (_, e) => onMessage(e.Message);
        }
    }

    public (string Outcome, string ErrorMessage, int? ErrorLine) TranslateException(Exception ex, int timeoutSeconds)
    {
        if (ex is SqlException sqlEx)
        {
            var isTimeout = sqlEx.Number == -2;
            var msg = isTimeout ? $"Query timed out after {timeoutSeconds}s." : sqlEx.Message;
            return (isTimeout ? "Timeout" : "Error", msg, sqlEx.LineNumber > 0 ? sqlEx.LineNumber : null);
        }
        if (ex is TimeoutException)
        {
            return ("Timeout", $"Query timed out after {timeoutSeconds}s.", null);
        }
        return ("Error", ex.Message, null);
    }

    public async Task<List<DbObjectInfo>> LoadMetadataAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var objects = new Dictionary<(string, string), DbObjectInfo>();

        await using (var cmd = new SqlCommand(@"SELECT s.name, o.name, o.type FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V','P','FN','IF','TF') AND o.is_ms_shipped = 0 ORDER BY s.name, o.name", conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var sqlType = r.GetString(2).Trim();
                var type = sqlType switch { "U" => "Table", "V" => "View", "P" => "Procedure", _ => "Function" };
                objects[(r.GetString(0), r.GetString(1))] = new DbObjectInfo(r.GetString(0), r.GetString(1), type, [])
                {
                    IsTableValuedFunction = sqlType is "IF" or "TF"
                };
            }
        }

        await using (var cmd = new SqlCommand(@"SELECT ps.name, po.name, fk.name, rs.name, ro.name, pc.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.objects po ON po.object_id = fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = po.schema_id
            JOIN sys.objects ro ON ro.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            ORDER BY ps.name, po.name, fk.name, fkc.constraint_column_id", conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if (!objects.TryGetValue((r.GetString(0), r.GetString(1)), out var source)
                    || !objects.ContainsKey((r.GetString(3), r.GetString(4)))) continue;
                var foreignKey = source.ForeignKeys.FirstOrDefault(f => f.Name == r.GetString(2));
                if (foreignKey is null)
                {
                    foreignKey = new DbForeignKeyInfo(r.GetString(2), r.GetString(3), r.GetString(4), []);
                    source.ForeignKeys.Add(foreignKey);
                }
                foreignKey.Columns.Add(new DbForeignKeyColumn(r.GetString(5), r.GetString(6)));
            }
        }

        await using (var cmd = new SqlCommand(@"SELECT s.name, o.name, c.name, t.name, c.is_nullable, c.is_identity, c.is_computed,
            CASE WHEN c.default_object_id != 0 THEN 1 ELSE 0 END AS has_default
            FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = o.schema_id JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE o.type IN ('U','V','IF','TF') AND o.is_ms_shipped = 0 ORDER BY c.column_id", conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                if (objects.TryGetValue((r.GetString(0), r.GetString(1)), out var o))
                {
                    var column = r.GetString(2);
                    var dataType = r.GetString(3);
                    var isNullable = r.GetBoolean(4);
                    var isIdentity = r.GetBoolean(5);
                    var isComputed = r.GetBoolean(6);
                    var hasDefault = r.GetInt32(7) == 1;

                    o.Columns.Add(column);
                    o.ColumnDetails.Add(new DbColumnInfo(column, dataType, isNullable, isIdentity, hasDefault, isComputed));
                }
            }
        }

        return objects.Values.ToList();
    }

    public async Task<ExecutionPlanDto?> ExplainPlanAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using (var cmdOn = new SqlCommand("SET SHOWPLAN_XML ON;", connection) { CommandTimeout = timeoutSeconds })
        {
            await cmdOn.ExecuteNonQueryAsync(ct);
        }

        var xmlBuilder = new StringBuilder();
        try
        {
            await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (reader.FieldCount > 0 && !reader.IsDBNull(0))
                {
                    xmlBuilder.Append(reader.GetString(0));
                }
            }
        }
        finally
        {
            try
            {
                await using var cmdOff = new SqlCommand("SET SHOWPLAN_XML OFF;", connection) { CommandTimeout = timeoutSeconds };
                await cmdOff.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch { }
        }

        var rawXml = xmlBuilder.ToString();
        return string.IsNullOrWhiteSpace(rawXml) ? null : ExecutionPlanParser.Parse(rawXml);
    }
}
