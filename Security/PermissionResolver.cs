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
    DateTimeOffset? EndsAt = null);

public sealed record EffectivePermission(
    string PermissionCode,
    bool IsAllowed,
    PermissionSource Source,
    IReadOnlyList<string> Roles);

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
        if (userRules.Length > 0)
        {
            return new EffectivePermission(
                permissionCode,
                !userRules.Any(rule => !rule.IsAllowed),
                PermissionSource.User,
                Array.Empty<string>());
        }

        var roleRules = activeRules.Where(rule => !rule.IsUserRule).ToArray();
        if (roleRules.Length > 0)
        {
            var roles = roleRules
                .Where(rule => rule.RoleName is not null)
                .Select(rule => rule.RoleName!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new EffectivePermission(
                permissionCode,
                !roleRules.Any(rule => !rule.IsAllowed),
                PermissionSource.Role,
                roles);
        }

        return new EffectivePermission(
            permissionCode,
            false,
            PermissionSource.Default,
            Array.Empty<string>());
    }
}
