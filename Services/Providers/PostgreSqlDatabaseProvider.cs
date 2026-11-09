using System.Data.Common;
using System.Text.Json;
using DbAccessManager.Data;
using Npgsql;

namespace DbAccessManager.Services.Providers;

public sealed class PostgreSqlDatabaseProvider : IDatabaseProvider
{
    public DatabaseEngine Engine => DatabaseEngine.PostgreSql;
    public string DisplayName => "PostgreSQL";

    public DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

    public (bool Valid, string? Error) ValidateConnectionString(string connectionString)
    {
        try
        {
            _ = new NpgsqlConnectionStringBuilder(connectionString);
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
            var builder = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 10 };
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            return (true, $"Connected to {conn.Host}:{conn.Port} / {conn.Database} (PostgreSQL {conn.ServerVersion}).");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public void RegisterMessageListener(DbConnection connection, Action<string> onMessage)
    {
        if (connection is NpgsqlConnection npgsqlConn)
        {
            npgsqlConn.Notice += (_, e) => onMessage(e.Notice.MessageText);
        }
    }

    public (string Outcome, string ErrorMessage, int? ErrorLine) TranslateException(Exception ex, int timeoutSeconds)
    {
        if (ex is PostgresException pe)
        {
            var isTimeout = pe.SqlState == "57014"; // query_canceled
            var msg = isTimeout ? $"Query timed out after {timeoutSeconds}s." : pe.MessageText ?? pe.Message;
            var line = int.TryParse(pe.Line, out var l) && l > 0 ? (int?)l : null;
            return (isTimeout ? "Timeout" : "Error", msg, line);
        }
        if (ex is NpgsqlException nex && (nex.InnerException is TimeoutException || nex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
        {
            return ("Timeout", $"Query timed out after {timeoutSeconds}s.", null);
        }
        if (ex is TimeoutException)
        {
            return ("Timeout", $"Query timed out after {timeoutSeconds}s.", null);
        }
        return ("Error", ex.Message, null);
    }

    public async Task<List<DbObjectInfo>> LoadMetadataAsync(string connectionString, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        var objects = new Dictionary<(string, string), DbObjectInfo>();

        // Tables and Views
        const string tablesSql = @"
            SELECT table_schema, table_name, table_type
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name;";
        await using (var cmd = new NpgsqlCommand(tablesSql, conn) { CommandTimeout = 30 })
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

        // Routines (Procedures and Functions)
        const string routinesSql = @"
            SELECT routine_schema, routine_name, routine_type
            FROM information_schema.routines
            WHERE routine_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY routine_schema, routine_name;";
        await using (var cmd = new NpgsqlCommand(routinesSql, conn) { CommandTimeout = 30 })
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
                tc.constraint_name,
                tc.table_schema,
                tc.table_name,
                kcu.column_name,
                ccu.table_schema AS foreign_table_schema,
                ccu.table_name AS foreign_table_name,
                ccu.column_name AS foreign_column_name
            FROM information_schema.table_constraints AS tc
            JOIN information_schema.key_column_usage AS kcu
              ON tc.constraint_name = kcu.constraint_name
              AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage AS ccu
              ON ccu.constraint_name = tc.constraint_name
              AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY tc.table_schema, tc.table_name, tc.constraint_name;";
        await using (var cmd = new NpgsqlCommand(fkSql, conn) { CommandTimeout = 30 })
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
            SELECT table_schema, table_name, column_name, data_type, is_nullable,
                   column_default, is_identity, is_generated
            FROM information_schema.columns
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
            ORDER BY table_schema, table_name, ordinal_position;";
        await using (var cmd = new NpgsqlCommand(colsSql, conn) { CommandTimeout = 30 })
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
                var isIdentity = !r.IsDBNull(6) && r.GetString(6).Equals("YES", StringComparison.OrdinalIgnoreCase);
                var isComputed = !r.IsDBNull(7) && !r.GetString(7).Equals("NEVER", StringComparison.OrdinalIgnoreCase);

                o.Columns.Add(column);
                o.ColumnDetails.Add(new DbColumnInfo(column, dataType, isNullable, isIdentity, hasDefault, isComputed));
            }
        }

        return objects.Values.ToList();
    }

    public async Task<ExecutionPlanDto?> ExplainPlanAsync(string connectionString, string sql, int timeoutSeconds, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var planJson = string.Empty;
        var explainQuery = $"EXPLAIN (FORMAT JSON) {sql.TrimEnd(';')};";
        await using (var cmd = new NpgsqlCommand(explainQuery, connection) { CommandTimeout = timeoutSeconds })
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct) && reader.FieldCount > 0 && !reader.IsDBNull(0))
            {
                planJson = reader.GetString(0);
            }
        }

        if (string.IsNullOrWhiteSpace(planJson)) return null;

        var dto = new ExecutionPlanDto { RawXml = planJson };
        try
        {
            using var doc = JsonDocument.Parse(planJson);
            var root = doc.RootElement;
            var planElement = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0].GetProperty("Plan")
                : root.GetProperty("Plan");

            if (planElement.TryGetProperty("Total Cost", out var totalCostElem))
                dto.TotalCost = totalCostElem.GetDouble();
            if (planElement.TryGetProperty("Plan Rows", out var rowsElem))
                dto.EstimatedRows = rowsElem.GetDouble();

            var nodeId = 1;
            ParsePlanElement(planElement, dto, ref nodeId);
        }
        catch
        {
            // Fallback: raw json preserved in RawXml
        }

        return dto;
    }

    private static void ParsePlanElement(JsonElement element, ExecutionPlanDto dto, ref int nodeId)
    {
        var nodeType = element.TryGetProperty("Node Type", out var nt) ? nt.GetString() ?? "Unknown" : "Unknown";
        var relation = element.TryGetProperty("Relation Name", out var rn) ? rn.GetString() : null;
        var totalCost = element.TryGetProperty("Total Cost", out var tc) ? tc.GetDouble() : 0;
        var estRows = element.TryGetProperty("Plan Rows", out var pr) ? pr.GetDouble() : 0;
        var isScan = nodeType.Contains("Scan", StringComparison.OrdinalIgnoreCase);

        dto.Operators.Add(new PlanOperatorDto
        {
            NodeId = nodeId++,
            PhysicalOp = nodeType,
            LogicalOp = nodeType,
            SubtreeCost = totalCost,
            OperatorCost = totalCost,
            EstimatedRows = estRows,
            TargetObject = relation,
            IsScan = isScan,
            IsWarning = isScan && estRows > 1000,
            WarningMessage = isScan && estRows > 1000 ? $"Full sequential scan on {relation ?? "table"} with {estRows:N0} estimated rows." : null
        });

        if (element.TryGetProperty("Plans", out var childPlans) && childPlans.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in childPlans.EnumerateArray())
            {
                ParsePlanElement(child, dto, ref nodeId);
            }
        }
    }
}
