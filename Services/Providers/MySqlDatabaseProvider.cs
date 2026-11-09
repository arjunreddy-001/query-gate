using System.Data.Common;
using System.Text;
using System.Text.Json;
using DbAccessManager.Data;
using MySqlConnector;

namespace DbAccessManager.Services.Providers;

public class MySqlDatabaseProvider : IDatabaseProvider
{
    public virtual DatabaseEngine Engine => DatabaseEngine.MySql;
    public virtual string DisplayName => "MySQL";

    public DbConnection CreateConnection(string connectionString) => new MySqlConnection(connectionString);

    public (bool Valid, string? Error) ValidateConnectionString(string connectionString)
    {
        try
        {
            _ = new MySqlConnectionStringBuilder(connectionString);
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
            var builder = new MySqlConnectionStringBuilder(connectionString) { ConnectionTimeout = 10 };
            await using var conn = new MySqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            return (true, $"Connected to {conn.DataSource} / {conn.Database} ({DisplayName} {conn.ServerVersion}).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void RegisterMessageListener(DbConnection connection, Action<string> onMessage)
    {
        if (connection is MySqlConnection mysqlConn)
        {
            mysqlConn.InfoMessage += (_, e) =>
            {
                foreach (var err in e.Errors) onMessage(err.Message);
            };
        }
    }

    public (string Outcome, string ErrorMessage, int? ErrorLine) TranslateException(Exception ex, int timeoutSeconds)
    {
        if (ex is MySqlException mex)
        {
            var isTimeout = mex.Number == 1317 || mex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
            var msg = isTimeout ? $"Query timed out after {timeoutSeconds}s." : mex.Message;
            int? line = null;
            var match = System.Text.RegularExpressions.Regex.Match(mex.Message, @"at line (\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var l)) line = l;
            return (isTimeout ? "Timeout" : "Error", msg, line);
        }
        if (ex is TimeoutException)
        {
            return ("Timeout", $"Query timed out after {timeoutSeconds}s.", null);
        }
        return ("Error", ex.Message, null);
    }

    public async Task<List<DbObjectInfo>> LoadMetadataAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new MySqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var objects = new Dictionary<(string, string), DbObjectInfo>();

        // Tables and Views
        const string tablesSql = @"
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE()
            ORDER BY TABLE_NAME;";
        await using (var cmd = new MySqlCommand(tablesSql, conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var schema = r.GetString(0);
                var name = r.GetString(1);
                var rawType = r.GetString(2);
                var type = rawType.Equals("VIEW", StringComparison.OrdinalIgnoreCase) ? "View" : "Table";
                objects[(schema, name)] = new DbObjectInfo(schema, name, type, []);
            }
        }

        // Routines
        const string routinesSql = @"
            SELECT ROUTINE_SCHEMA, ROUTINE_NAME, ROUTINE_TYPE
            FROM information_schema.ROUTINES
            WHERE ROUTINE_SCHEMA = DATABASE()
            ORDER BY ROUTINE_NAME;";
        await using (var cmd = new MySqlCommand(routinesSql, conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var schema = r.GetString(0);
                var name = r.GetString(1);
                var routineType = r.GetString(2);
                var type = routineType.Equals("PROCEDURE", StringComparison.OrdinalIgnoreCase) ? "Procedure" : "Function";
                objects[(schema, name)] = new DbObjectInfo(schema, name, type, []);
            }
        }

        // Foreign keys
        const string fkSql = @"
            SELECT
                CONSTRAINT_NAME,
                TABLE_SCHEMA,
                TABLE_NAME,
                COLUMN_NAME,
                REFERENCED_TABLE_SCHEMA,
                REFERENCED_TABLE_NAME,
                REFERENCED_COLUMN_NAME
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE()
              AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY TABLE_NAME, CONSTRAINT_NAME, ORDINAL_POSITION;";
        await using (var cmd = new MySqlCommand(fkSql, conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var constraintName = r.GetString(0);
                var schema = r.GetString(1);
                var table = r.GetString(2);
                var col = r.GetString(3);
                var refSchema = r.GetString(4);
                var refTable = r.GetString(5);
                var refCol = r.GetString(6);

                if (!objects.TryGetValue((schema, table), out var source)) continue;
                var fk = source.ForeignKeys.FirstOrDefault(f => f.Name == constraintName);
                if (fk is null)
                {
                    fk = new DbForeignKeyInfo(constraintName, refSchema, refTable, []);
                    source.ForeignKeys.Add(fk);
                }
                fk.Columns.Add(new DbForeignKeyColumn(col, refCol));
            }
        }

        // Columns
        const string colsSql = @"
            SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE,
                   COLUMN_DEFAULT, EXTRA
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE()
            ORDER BY TABLE_NAME, ORDINAL_POSITION;";
        await using (var cmd = new MySqlCommand(colsSql, conn) { CommandTimeout = 30 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var schema = r.GetString(0);
                var table = r.GetString(1);
                if (!objects.TryGetValue((schema, table), out var o)) continue;

                var column = r.GetString(2);
                var dataType = r.GetString(3);
                var isNullable = r.GetString(4).Equals("YES", StringComparison.OrdinalIgnoreCase);
                var hasDefault = !r.IsDBNull(5);
                var extra = r.IsDBNull(6) ? "" : r.GetString(6);
                var isIdentity = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
                var isComputed = extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase);

                o.Columns.Add(column);
                o.ColumnDetails.Add(new DbColumnInfo(column, dataType, isNullable, isIdentity, hasDefault, isComputed));
            }
        }

        return objects.Values.ToList();
    }

    public async Task<ExecutionPlanDto?> ExplainPlanAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct = default)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);

        // Try EXPLAIN FORMAT=JSON first
        try
        {
            var planJson = string.Empty;
            var explainQuery = $"EXPLAIN FORMAT=JSON {sql.TrimEnd(';')};";
            await using var cmd = new MySqlCommand(explainQuery, connection) { CommandTimeout = timeoutSeconds };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct) && reader.FieldCount > 0 && !reader.IsDBNull(0))
            {
                planJson = reader.GetString(0);
            }

            if (!string.IsNullOrWhiteSpace(planJson))
            {
                var dto = new ExecutionPlanDto { RawXml = planJson };
                try
                {
                    using var doc = JsonDocument.Parse(planJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("query_block", out var qb))
                    {
                        if (qb.TryGetProperty("cost_info", out var ci) && ci.TryGetProperty("query_cost", out var qc))
                        {
                            if (double.TryParse(qc.GetString() ?? qc.GetRawText(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var cost))
                                dto.TotalCost = cost;
                        }

                        var nodeId = 1;
                        ParseMySqlBlock(qb, dto, ref nodeId);
                    }
                }
                catch
                {
                    // Fallback: raw json preserved in RawXml
                }
                return dto;
            }
        }
        catch
        {
            // Fall back to standard tabular EXPLAIN
        }

        // Fallback: standard tabular EXPLAIN
        try
        {
            var explainQuery = $"EXPLAIN {sql.TrimEnd(';')};";
            await using var cmd = new MySqlCommand(explainQuery, connection) { CommandTimeout = timeoutSeconds };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var rawText = new StringBuilder();
            var dto = new ExecutionPlanDto();
            var nodeId = 1;

            var cols = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));
            rawText.AppendLine(string.Join("\t", cols));

            while (await reader.ReadAsync(ct))
            {
                var rowVals = new List<string>();
                for (var i = 0; i < reader.FieldCount; i++)
                    rowVals.Add(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? "");

                rawText.AppendLine(string.Join("\t", rowVals));

                var table = GetColVal(reader, cols, "table");
                var selectType = GetColVal(reader, cols, "select_type") ?? "SIMPLE";
                var accessType = GetColVal(reader, cols, "type") ?? "ALL";
                var rowsStr = GetColVal(reader, cols, "rows");
                var extra = GetColVal(reader, cols, "Extra") ?? "";
                _ = double.TryParse(rowsStr, out var rows);

                var isScan = accessType.Equals("ALL", StringComparison.OrdinalIgnoreCase);
                dto.Operators.Add(new PlanOperatorDto
                {
                    NodeId = nodeId++,
                    PhysicalOp = accessType,
                    LogicalOp = selectType,
                    TargetObject = table,
                    EstimatedRows = rows,
                    IsScan = isScan,
                    IsWarning = isScan && rows > 1000,
                    WarningMessage = isScan && rows > 1000 ? $"Full table scan on {table} with ~{rows:N0} estimated rows." : null
                });
            }

            dto.RawXml = rawText.ToString();
            return dto;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetColVal(DbDataReader reader, List<string> cols, string colName)
    {
        var idx = cols.FindIndex(c => c.Equals(colName, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && !reader.IsDBNull(idx) ? reader.GetValue(idx)?.ToString() : null;
    }

    private static void ParseMySqlBlock(JsonElement element, ExecutionPlanDto dto, ref int nodeId)
    {
        if (element.TryGetProperty("table", out var tbl))
        {
            var tableName = tbl.TryGetProperty("table_name", out var tn) ? tn.GetString() : "table";
            var accessType = tbl.TryGetProperty("access_type", out var at) ? at.GetString() ?? "ALL" : "ALL";
            var rowsExamined = tbl.TryGetProperty("rows_examined_per_scan", out var re) && re.TryGetInt64(out var r) ? (double)r : 0;
            var isScan = accessType.Equals("ALL", StringComparison.OrdinalIgnoreCase);

            dto.Operators.Add(new PlanOperatorDto
            {
                NodeId = nodeId++,
                PhysicalOp = accessType,
                LogicalOp = "TABLE ACCESS",
                TargetObject = tableName,
                EstimatedRows = rowsExamined,
                IsScan = isScan,
                IsWarning = isScan && rowsExamined > 1000,
                WarningMessage = isScan && rowsExamined > 1000 ? $"Full table scan on {tableName} with ~{rowsExamined:N0} examined rows." : null
            });
        }

        if (element.TryGetProperty("nested_loop", out var nl) && nl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in nl.EnumerateArray())
            {
                ParseMySqlBlock(item, dto, ref nodeId);
            }
        }
    }
}
