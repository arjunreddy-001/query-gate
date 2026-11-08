using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Text;
using DbAccessManager.Data;
using DbAccessManager.Security;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace DbAccessManager.Services;

public sealed class PermissionService(IDbContextFactory<ApplicationDbContext> factory)
{
    private readonly PermissionResolver _resolver = new();

    public async Task<List<PermissionRule>> GetRulesAsync(ApplicationDbContext db, string userId, Guid databaseId)
    {
        var roleIds = await db.UserRoles.Where(r => r.UserId == userId).Select(r => r.RoleId).ToListAsync();
        var roleNames = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name ?? "");
        var grants = await db.PermissionGrants.AsNoTracking()
            .Where(g => g.DatabaseId == databaseId && (g.UserId == userId || (g.RoleId != null && roleIds.Contains(g.RoleId))))
            .ToListAsync();
        return grants.Select(g => new PermissionRule(
            g.PermissionCode, g.UserId != null, g.Effect == PermissionEffect.Allow,
            g.RoleId is null ? null : roleNames.GetValueOrDefault(g.RoleId), g.StartsAt, g.EndsAt)).ToList();
    }

    public async Task<List<EffectivePermission>> GetEffectiveAsync(string userId, Guid databaseId)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rules = await GetRulesAsync(db, userId, databaseId);
        var now = DateTimeOffset.UtcNow;
        var codes = Perm.Catalog.Concat(rules.Select(r => r.PermissionCode).Where(c => c.StartsWith(Perm.SysProcPrefix, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return codes.Select(c => _resolver.Resolve(c, rules, now)).ToList();
    }

    public async Task<bool> HasAsync(string userId, Guid databaseId, string code)
    {
        await using var db = await factory.CreateDbContextAsync();
        var rules = await GetRulesAsync(db, userId, databaseId);
        return _resolver.Resolve(code, rules, DateTimeOffset.UtcNow).IsAllowed;
    }

    public async Task<List<ManagedDatabase>> GetAccessibleDatabasesAsync(string userId, bool includeDisabled = false)
    {
        await using var db = await factory.CreateDbContextAsync();
        var all = await db.ManagedDatabases.AsNoTracking().OrderBy(d => d.Name).ToListAsync();
        var result = new List<ManagedDatabase>();
        foreach (var d in all)
        {
            if (!includeDisabled && !d.IsActive) continue;
            var rules = await GetRulesAsync(db, userId, d.Id);
            if (Perm.Catalog.Any(c => _resolver.Resolve(c, rules, DateTimeOffset.UtcNow).IsAllowed)) result.Add(d);
        }
        return result;
    }

    public bool Resolve(string code, IEnumerable<PermissionRule> rules) => _resolver.Resolve(code, rules, DateTimeOffset.UtcNow).IsAllowed;
}

public sealed class ResultSetDto
{
    public List<string> Columns { get; set; } = [];
    public List<object?[]> Rows { get; set; } = [];
    public bool Truncated { get; set; }
    public bool IsMasked { get; set; }
    public List<string> MaskedColumns { get; set; } = [];
}

public sealed class ExecutionResult
{
    public Guid RunId { get; set; }
    public string Outcome { get; set; } = "Success";
    public List<ResultSetDto> ResultSets { get; } = [];
    public List<string> Messages { get; } = [];
    public string? Error { get; set; }
    public int? ErrorLine { get; set; }
    public List<string> Denials { get; } = [];
    public List<string> UnfilteredWrites { get; } = [];
    public long DurationMs { get; set; }
    public long RowsAffected { get; set; }
    public long RowsReturned { get; set; }
    public long AuditId { get; set; }
    public bool IsMasked { get; set; }
    public List<string> MaskedColumns { get; } = [];
    public ExecutionPlanDto? ExecutionPlan { get; set; }
    public bool Ok => Outcome == "Success";
}

public sealed class ExecutionOptions
{
    public string SqlText { get; init; } = "";
    public Guid DatabaseId { get; init; }
    public int? TimeoutSeconds { get; init; }
    public bool DryRun { get; init; }
    public bool ConfirmUnfilteredWrites { get; init; }
    public string Executor { get; init; } = "User";
    public Guid? LinkedRequestId { get; init; }
    public bool ApprovedScript { get; init; }
    public bool Export { get; init; }
    public Guid? RunId { get; init; }
}

public sealed class ExecutionService(
    IDbContextFactory<ApplicationDbContext> factory,
    IServiceScopeFactory scopes,
    SqlGate gate,
    PermissionService permissions,
    AuditService audit,
    CryptoService crypto,
    SettingsService settings,
    ILogger<ExecutionService> logger)
{
    private readonly ConcurrentDictionary<Guid, (string UserId, CancellationTokenSource Cts)> _running = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userSlots = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _dbSlots = new();


    public async Task<OpResult> AuthorizeExportAsync(Actor actor, Guid databaseId, long rows, string sql, string format = "Csv", string? databaseName = null)
    {
        var action = $"Export{format}";
        if (string.IsNullOrWhiteSpace(databaseName) && databaseId != Guid.Empty)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                databaseName = await db.ManagedDatabases.Where(d => d.Id == databaseId).Select(d => d.Name).FirstOrDefaultAsync();
            }
            catch
            {
                // Non-critical database name lookup
            }
        }

        if (!await permissions.HasAsync(actor.UserId, databaseId, Perm.Export))
        {
            await audit.TryWriteAsync(actor, "Export", action, "Denied", "Missing permission: EXPORT", databaseId, databaseName);
            return OpResult.Fail("You do not have the EXPORT permission on this database.");
        }
        try
        {
            await audit.WriteAsync(new AuditLog
            {
                UserId = actor.UserId,
                UserName = actor.UserName,
                ClientIp = actor.Ip,
                UserAgent = AuditService.Truncate(actor.UserAgent, 512),
                DatabaseId = databaseId,
                DatabaseName = databaseName,
                Category = "Export",
                Action = action,
                SqlText = sql,
                Outcome = "Success",
                RowsReturned = rows,
                Executor = "User"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit write failed; refusing export");
            return OpResult.Fail("Auditing is unavailable, so the export was blocked.");
        }
        return OpResult.Success();
    }
    public bool Cancel(Guid runId, string userId, bool isAdmin = false)
    {
        if (_running.TryGetValue(runId, out var run) && (run.UserId == userId || isAdmin))
        {
            run.Cts.Cancel();
            return true;
        }
        return false;
    }

    public int CancelAllFor(string userId)
    {
        var count = 0;
        foreach (var (id, run) in _running.Where(r => r.Value.UserId == userId).ToList())
        {
            run.Cts.Cancel();
            count++;
        }
        return count;
    }

    public async Task<ExecutionResult> ExecuteAsync(Actor actor, ExecutionOptions options, CancellationToken ct = default)
    {
        var result = new ExecutionResult { RunId = options.RunId ?? Guid.NewGuid() };
        var isSystem = options.Executor == "System";
        var stopwatch = Stopwatch.StartNew();

        await using var db = await factory.CreateDbContextAsync(ct);
        var database = await db.ManagedDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == options.DatabaseId, ct);

        async Task<ExecutionResult> Deny(string outcome, string reason, GateAnalysis? analysis = null)
        {
            result.Outcome = outcome;
            result.Error = reason;
            result.Denials.Add(reason);
            await audit.TryWriteAsync(NewAudit(actor, options, database, analysis, outcome, reason));
            return result;
        }

        if (database is null) return await Deny("Denied", "Database not found.");

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(actor.UserId);
        if (!isSystem && (user is null || !user.CanSignIn(DateTimeOffset.UtcNow))) return await Deny("Denied", "Your account is inactive or expired.");
        if (!database.IsActive) return await Deny("Denied", "This database is disabled.");

        var analysis = gate.Analyze(options.SqlText);
        if (analysis.HasHardDenial)
        {
            foreach (var d in analysis.ParseErrors.Concat(analysis.HardDenials)) result.Denials.Add(d);
            return await Deny("Denied", string.Join(" ", result.Denials), analysis);
        }

        var rules = await permissions.GetRulesAsync(db, actor.UserId, database.Id);
        var missing = new List<string>();
        foreach (var req in analysis.Requirements)
        {
            if (options.ApprovedScript && !Perm.IsDangerous(req)) continue;
            if (!permissions.Resolve(req, rules)) missing.Add(req);
        }
        if (options.Export && !permissions.Resolve(Perm.Export, rules)) missing.Add(Perm.Export);
        if (missing.Count > 0)
        {
            result.Denials.AddRange(missing.Select(m => $"Missing permission: {m}"));
            return await Deny("Denied", "Missing permission: " + string.Join(", ", missing), analysis);
        }

        if (analysis.UnfilteredWrites.Count > 0 && !options.ConfirmUnfilteredWrites && !options.ApprovedScript)
        {
            result.UnfilteredWrites.AddRange(analysis.UnfilteredWrites);
            result.Outcome = "NeedsConfirmation";
            result.Error = "UPDATE/DELETE without WHERE requires confirmation.";
            return result;
        }

        var maxTimeout = await settings.GetIntAsync("MaxTimeoutSeconds");
        var defaultTimeout = database.DefaultTimeoutSeconds > 0 ? database.DefaultTimeoutSeconds : await settings.GetIntAsync("DefaultTimeoutSeconds");
        var timeout = Math.Min(maxTimeout, options.TimeoutSeconds ?? user?.TimeoutOverrideSeconds ?? defaultTimeout);
        var maxRows = database.MaxRows > 0 ? database.MaxRows : await settings.GetIntAsync("DefaultMaxRows");

        var perUser = Math.Max(1, await settings.GetIntAsync("MaxConcurrentPerUser"));
        var perDb = Math.Max(1, await settings.GetIntAsync("MaxConcurrentPerDatabase"));
        var userSlot = _userSlots.GetOrAdd(actor.UserId, _ => new SemaphoreSlim(perUser));
        var dbSlot = _dbSlots.GetOrAdd(database.Id, _ => new SemaphoreSlim(perDb));
        if (!await userSlot.WaitAsync(0, ct)) return await Deny("Denied", "Too many concurrent queries for your account.", analysis);
        if (!await dbSlot.WaitAsync(0, ct))
        {
            userSlot.Release();
            return await Deny("Denied", "Too many concurrent queries on this database.", analysis);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running[result.RunId] = (actor.UserId, cts);
        long auditId;
        try
        {
            // Fail-closed: no audit row, no execution.
            var entry = NewAudit(actor, options, database, analysis, "Running", null);
            entry.RequestId = result.RunId.ToString();
            try { auditId = await audit.WriteAsync(entry); }
            catch (Exception ex)
            {
                logger.LogError(ex, "Audit write failed; refusing execution");
                result.Outcome = "AuditFailure";
                result.Error = "Auditing is unavailable, so the query was not executed.";
                return result;
            }

            try
            {
                await RunAsync(database, options, analysis, result, timeout, maxRows, cts.Token);
                result.Outcome = "Success";

                var canUnmask = permissions.Resolve(Perm.Unmask, rules);
                if (!canUnmask)
                {
                    var (maskRules, maskEnabled) = await DataMaskingService.GetConfigAsync(factory);
                    if (maskEnabled)
                    {
                        DataMaskingService.ApplyMasking(result, maskRules);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result.Outcome = "Cancelled";
                result.Error = "Query cancelled.";
            }
            catch (SqlException ex)
            {
                result.Outcome = ex.Number == -2 ? "Timeout" : "Error";
                result.Error = ex.Number == -2 ? $"Query timed out after {timeout}s." : ex.Message;
                result.ErrorLine = ex.LineNumber > 0 ? ex.LineNumber : null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Query execution failed");
                result.Outcome = "Error";
                result.Error = ex.Message;
            }

            stopwatch.Stop();
            result.DurationMs = stopwatch.ElapsedMilliseconds;
            try
            {
                await using var db2 = await factory.CreateDbContextAsync();
                var row = await db2.AuditLogs.FindAsync(auditId);
                if (row is not null)
                {
                    row.Outcome = result.Outcome;
                    row.DurationMs = result.DurationMs;
                    row.RowsAffected = result.RowsAffected;
                    row.RowsReturned = result.RowsReturned;
                    row.Details = result.Error is null
                        ? (result.IsMasked ? $"[Masked: {string.Join(", ", result.MaskedColumns)}]" : null)
                        : AuditService.Truncate(result.Error, 2000);
                    await db2.SaveChangesAsync();
                }
                if (!isSystem)
                {
                    db2.QueryHistory.Add(new Data.QueryHistory
                    {
                        UserId = actor.UserId, DatabaseId = database.Id, SqlText = options.SqlText,
                        Outcome = result.Outcome, DurationMs = result.DurationMs
                    });
                    await db2.SaveChangesAsync();
                    var limit = await settings.GetIntAsync("HistoryLimit");
                    var stale = await db2.QueryHistory.Where(h => h.UserId == actor.UserId)
                        .OrderByDescending(h => h.Id).Skip(limit).Select(h => h.Id).ToListAsync();
                    if (stale.Count > 0)
                    {
                        await db2.QueryHistory.Where(h => stale.Contains(h.Id)).ExecuteDeleteAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to finalize audit row {AuditId}", auditId);
            }

            result.AuditId = auditId;
            return result;
        }
        finally
        {
            _running.TryRemove(result.RunId, out _);
            userSlot.Release();
            dbSlot.Release();
        }
    }

    public async Task<ExecutionResult> GetEstimatedPlanAsync(Actor actor, Guid databaseId, string sql, int? timeoutSeconds = null, CancellationToken ct = default)
    {
        var result = new ExecutionResult { RunId = Guid.NewGuid() };
        var stopwatch = Stopwatch.StartNew();

        await using var db = await factory.CreateDbContextAsync(ct);
        var database = await db.ManagedDatabases.AsNoTracking().FirstOrDefaultAsync(d => d.Id == databaseId, ct);
        if (database is null)
        {
            result.Outcome = "Denied";
            result.Error = "Database not found.";
            return result;
        }

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(actor.UserId);
        if (user is null || !user.CanSignIn(DateTimeOffset.UtcNow))
        {
            result.Outcome = "Denied";
            result.Error = "Your account is inactive or expired.";
            return result;
        }
        if (!database.IsActive)
        {
            result.Outcome = "Denied";
            result.Error = "This database is disabled.";
            return result;
        }

        var analysis = gate.Analyze(sql);
        if (analysis.HasHardDenial)
        {
            foreach (var d in analysis.ParseErrors.Concat(analysis.HardDenials)) result.Denials.Add(d);
            result.Outcome = "Denied";
            result.Error = string.Join(" ", result.Denials);
            await audit.TryWriteAsync(NewAudit(actor, new ExecutionOptions { DatabaseId = databaseId, SqlText = sql }, database, analysis, "Denied", result.Error));
            return result;
        }

        var rules = await permissions.GetRulesAsync(db, actor.UserId, database.Id);
        if (!permissions.Resolve(Perm.Select, rules))
        {
            result.Outcome = "Denied";
            result.Error = "Missing permission: SELECT (required for Explain Plan).";
            result.Denials.Add(result.Error);
            await audit.TryWriteAsync(NewAudit(actor, new ExecutionOptions { DatabaseId = databaseId, SqlText = sql }, database, analysis, "Denied", result.Error));
            return result;
        }

        var maxTimeout = await settings.GetIntAsync("MaxTimeoutSeconds");
        var defaultTimeout = database.DefaultTimeoutSeconds > 0 ? database.DefaultTimeoutSeconds : await settings.GetIntAsync("DefaultTimeoutSeconds");
        var timeout = Math.Min(maxTimeout, timeoutSeconds ?? user?.TimeoutOverrideSeconds ?? defaultTimeout);

        var perUser = Math.Max(1, await settings.GetIntAsync("MaxConcurrentPerUser"));
        var perDb = Math.Max(1, await settings.GetIntAsync("MaxConcurrentPerDatabase"));
        var userSlot = _userSlots.GetOrAdd(actor.UserId, _ => new SemaphoreSlim(perUser));
        var dbSlot = _dbSlots.GetOrAdd(database.Id, _ => new SemaphoreSlim(perDb));
        if (!await userSlot.WaitAsync(0, ct))
        {
            result.Outcome = "Denied";
            result.Error = "Too many concurrent queries for your account.";
            return result;
        }
        if (!await dbSlot.WaitAsync(0, ct))
        {
            userSlot.Release();
            result.Outcome = "Denied";
            result.Error = "Too many concurrent queries on this database.";
            return result;
        }

        var xmlBuilder = new StringBuilder();
        try
        {
            var connectionString = crypto.Decrypt(database.EncryptedConnectionString);
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(ct);

            await using (var cmdOn = new SqlCommand("SET SHOWPLAN_XML ON;", connection) { CommandTimeout = timeout })
            {
                await cmdOn.ExecuteNonQueryAsync(ct);
            }

            try
            {
                foreach (var batch in analysis.Batches)
                {
                    await using var cmd = new SqlCommand(batch, connection) { CommandTimeout = timeout };
                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                    {
                        if (reader.FieldCount > 0 && !reader.IsDBNull(0))
                        {
                            xmlBuilder.Append(reader.GetString(0));
                        }
                    }
                }
                result.Outcome = "Success";
            }
            finally
            {
                try
                {
                    await using var cmdOff = new SqlCommand("SET SHOWPLAN_XML OFF;", connection) { CommandTimeout = timeout };
                    await cmdOff.ExecuteNonQueryAsync(CancellationToken.None);
                }
                catch { }
            }
        }
        catch (OperationCanceledException)
        {
            result.Outcome = "Cancelled";
            result.Error = "Explain plan cancelled.";
        }
        catch (SqlException ex)
        {
            result.Outcome = ex.Number == -2 ? "Timeout" : "Error";
            result.Error = ex.Number == -2 ? $"Explain plan timed out after {timeout}s." : ex.Message;
            result.ErrorLine = ex.LineNumber > 0 ? ex.LineNumber : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Explain plan failed");
            result.Outcome = "Error";
            result.Error = ex.Message;
        }
        finally
        {
            userSlot.Release();
            dbSlot.Release();
        }

        stopwatch.Stop();
        result.DurationMs = stopwatch.ElapsedMilliseconds;

        var rawXml = xmlBuilder.ToString();
        if (result.Ok && !string.IsNullOrWhiteSpace(rawXml))
        {
            result.ExecutionPlan = ExecutionPlanParser.Parse(rawXml);
            result.Messages.Add($"Estimated plan generated in {result.DurationMs}ms (Subtree Cost: {result.ExecutionPlan.TotalCost:F4}).");
        }

        var auditEntry = new AuditLog
        {
            UserId = actor.UserId,
            UserName = actor.UserName,
            ClientIp = actor.Ip,
            UserAgent = AuditService.Truncate(actor.UserAgent, 512),
            DatabaseId = database.Id,
            DatabaseName = database.Name,
            Category = "Query",
            Action = "ExplainPlan",
            SqlText = sql,
            StatementTypes = AuditService.Truncate(string.Join(",", analysis.StatementTypes), 512),
            Outcome = result.Outcome,
            DurationMs = result.DurationMs,
            Details = result.Error is null
                ? (result.ExecutionPlan is not null ? $"Cost: {result.ExecutionPlan.TotalCost:F4}, Operators: {result.ExecutionPlan.Operators.Count}" : null)
                : AuditService.Truncate(result.Error, 2000),
            Executor = "User"
        };
        await audit.TryWriteAsync(auditEntry);

        return result;
    }

    private async Task RunAsync(ManagedDatabase database, ExecutionOptions options, GateAnalysis analysis, ExecutionResult result, int timeout, int maxRows, CancellationToken ct)
    {
        var connectionString = crypto.Decrypt(database.EncryptedConnectionString);
        await using var connection = new SqlConnection(connectionString);
        connection.InfoMessage += (_, e) => result.Messages.Add(e.Message);
        await connection.OpenAsync(ct);

        SqlTransaction? tx = options.DryRun ? connection.BeginTransaction() : null;
        try
        {
            foreach (var batch in analysis.Batches)
            {
                await using var cmd = new SqlCommand(batch, connection, tx) { CommandTimeout = timeout };
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                do
                {
                    if (reader.FieldCount > 0)
                    {
                        var set = new ResultSetDto();
                        for (var i = 0; i < reader.FieldCount; i++) set.Columns.Add(reader.GetName(i));
                        while (await reader.ReadAsync(ct))
                        {
                            if (set.Rows.Count >= maxRows) { set.Truncated = true; break; }
                            var values = new object?[reader.FieldCount];
                            reader.GetValues(values);
                            for (var i = 0; i < values.Length; i++) values[i] = Normalize(values[i]);
                            set.Rows.Add(values);
                        }
                        result.RowsReturned += set.Rows.Count;
                        result.ResultSets.Add(set);
                        if (set.Truncated) { cmd.Cancel(); break; }
                    }
                } while (await reader.NextResultAsync(ct));
                if (reader.RecordsAffected > 0) result.RowsAffected += reader.RecordsAffected;
            }
            if (tx is not null)
            {
                await tx.RollbackAsync(CancellationToken.None);
                result.Messages.Add("Dry run: all changes were rolled back.");
            }
        }
        catch
        {
            if (tx is not null) try { await tx.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        }
    }

    private static object? Normalize(object? value) => value switch
    {
        DBNull => null,
        byte[] bytes => "0x" + Convert.ToHexString(bytes.Length > 64 ? bytes[..64] : bytes) + (bytes.Length > 64 ? "…" : ""),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff"),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
        TimeSpan ts => ts.ToString(),
        Guid g => g.ToString(),
        decimal or double or float or int or long or short or byte or bool or string => value,
        _ => value?.ToString()
    };

    private static AuditLog NewAudit(Actor actor, ExecutionOptions options, ManagedDatabase? database, GateAnalysis? analysis, string outcome, string? details) => new()
    {
        UserId = actor.UserId,
        UserName = actor.UserName,
        ClientIp = actor.Ip,
        UserAgent = AuditService.Truncate(actor.UserAgent, 512),
        DatabaseId = database?.Id ?? options.DatabaseId,
        DatabaseName = database?.Name,
        Category = options.Export ? "Export" : "Query",
        Action = options.DryRun ? "DryRun" : "Execute",
        SqlText = options.SqlText,
        StatementTypes = analysis is null ? null : AuditService.Truncate(string.Join(",", analysis.StatementTypes), 512),
        Outcome = outcome,
        Details = details,
        LinkedRequestId = options.LinkedRequestId,
        Executor = options.Executor
    };
}




