namespace DbAccessManager.Security;

public enum PermissionSource
{
    User,
    Role,
    Default
}

public sealed record PermissionRule(
    string PermissionCode,
    bool IsUserRule,
    bool IsAllowed,
    string? RoleName = null,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null,
    string? Schema = null,
    string? ObjectName = null);

public sealed record EffectivePermission(
    string PermissionCode,
    bool IsAllowed,
    PermissionSource Source,
    IReadOnlyList<string> Roles,
    string? Schema = null,
    string? ObjectName = null,
    IReadOnlyList<string>? AllowedScopes = null,
    IReadOnlyList<string>? DeniedScopes = null)
{
    public IReadOnlyList<string> AllowedScopes { get; init; } = AllowedScopes ?? [];
    public IReadOnlyList<string> DeniedScopes { get; init; } = DeniedScopes ?? [];
}

public sealed class PermissionResolver
{
    public EffectivePermission Resolve(
        string permissionCode,
        IEnumerable<PermissionRule> rules,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionCode);
        ArgumentNullException.ThrowIfNull(rules);

        var activeRules = rules
            .Where(rule =>
                string.Equals(rule.PermissionCode, permissionCode, StringComparison.OrdinalIgnoreCase) &&
                (!rule.StartsAt.HasValue || rule.StartsAt.Value <= now) &&
                (!rule.EndsAt.HasValue || rule.EndsAt.Value > now))
            .ToArray();

        var userRules = activeRules.Where(rule => rule.IsUserRule).ToArray();
        var roleRules = activeRules.Where(rule => !rule.IsUserRule).ToArray();

        var roles = roleRules
            .Where(rule => rule.RoleName is not null)
            .Select(rule => rule.RoleName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var applicableRules = userRules.Length > 0 ? userRules : roleRules;
        if (applicableRules.Length == 0)
        {
            return new EffectivePermission(
                permissionCode,
                false,
                PermissionSource.Default,
                Array.Empty<string>(),
                AllowedScopes: [],
                DeniedScopes: []);
        }

        var source = userRules.Length > 0 ? PermissionSource.User : PermissionSource.Role;
        var allowedScopes = new List<string>();
        var deniedScopes = new List<string>();

        foreach (var r in applicableRules)
        {
            var scopeStr = ObjectScope.Format(r.Schema, r.ObjectName);
            if (r.IsAllowed)
            {
                if (!allowedScopes.Contains(scopeStr, StringComparer.OrdinalIgnoreCase))
                    allowedScopes.Add(scopeStr);
            }
            else
            {
                if (!deniedScopes.Contains(scopeStr, StringComparer.OrdinalIgnoreCase))
                    deniedScopes.Add(scopeStr);
            }
        }

        var hasDbWideDeny = deniedScopes.Contains("*");
        var hasDbWideAllow = allowedScopes.Contains("*");
        var isAllowed = !hasDbWideDeny && (hasDbWideAllow || allowedScopes.Count > 0);

        return new EffectivePermission(
            permissionCode,
            isAllowed,
            source,
            roles,
            AllowedScopes: allowedScopes,
            DeniedScopes: deniedScopes);
    }

    public EffectivePermission ResolveObject(
        string permissionCode,
        string? schema,
        string objectName,
        IEnumerable<PermissionRule> rules,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        ArgumentNullException.ThrowIfNull(rules);

        var activeRules = rules
            .Where(rule =>
                string.Equals(rule.PermissionCode, permissionCode, StringComparison.OrdinalIgnoreCase) &&
                (!rule.StartsAt.HasValue || rule.StartsAt.Value <= now) &&
                (!rule.EndsAt.HasValue || rule.EndsAt.Value > now))
            .ToArray();

        var userResult = EvaluateTier(activeRules.Where(r => r.IsUserRule), schema, objectName);
        if (userResult.HasValue)
        {
            return new EffectivePermission(
                permissionCode,
                userResult.Value.IsAllowed,
                PermissionSource.User,
                Array.Empty<string>(),
                schema,
                objectName);
        }

        var roleRules = activeRules.Where(r => !r.IsUserRule).ToArray();
        var roleResult = EvaluateTier(roleRules, schema, objectName);
        if (roleResult.HasValue)
        {
            var roles = roleRules
                .Where(rule => rule.RoleName is not null)
                .Select(rule => rule.RoleName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new EffectivePermission(
                permissionCode,
                roleResult.Value.IsAllowed,
                PermissionSource.Role,
                roles,
                schema,
                objectName);
        }

        return new EffectivePermission(
            permissionCode,
            false,
            PermissionSource.Default,
            Array.Empty<string>(),
            schema,
            objectName);
    }

    private static (bool IsAllowed, int Tier)? EvaluateTier(IEnumerable<PermissionRule> rules, string? schema, string objectName)
    {
        var matched = new List<(PermissionRule Rule, int Tier)>();

        foreach (var rule in rules)
        {
            var ruleHasSchema = !string.IsNullOrWhiteSpace(rule.Schema) && rule.Schema != "*";
            var ruleHasObj = !string.IsNullOrWhiteSpace(rule.ObjectName) && rule.ObjectName != "*";

            if (ruleHasObj)
            {
                // Tier 1: Specific object
                var objMatch = string.Equals(rule.ObjectName, objectName, StringComparison.OrdinalIgnoreCase);
                var schemaMatch = !ruleHasSchema || string.IsNullOrWhiteSpace(schema) ||
                                  string.Equals(rule.Schema, schema, StringComparison.OrdinalIgnoreCase);

                if (objMatch && schemaMatch)
                {
                    matched.Add((rule, 1));
                }
            }
            else if (ruleHasSchema)
            {
                // Tier 2: Schema-level
                if (!string.IsNullOrWhiteSpace(schema) && string.Equals(rule.Schema, schema, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add((rule, 2));
                }
            }
            else
            {
                // Tier 3: Database-wide
                matched.Add((rule, 3));
            }
        }

        if (matched.Count == 0) return null;

        // Group by highest priority tier (lowest tier number: 1 is highest priority)
        var bestTier = matched.Min(m => m.Tier);
        var tierRules = matched.Where(m => m.Tier == bestTier).Select(m => m.Rule).ToArray();

        // In the best matching tier: Deny wins
        var allowed = !tierRules.Any(r => !r.IsAllowed);
        return (allowed, bestTier);
    }
}
