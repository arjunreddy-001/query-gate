namespace DbAccessManager.Security;

public static class ObjectScope
{
    public const string AllObjects = "*";

    public static (string? Schema, string? ObjectName) Parse(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return (null, null);
        var trimmed = target.Trim().Trim('[', ']', '`', '"');
        if (trimmed == "*" || string.IsNullOrWhiteSpace(trimmed)) return (null, null);

        var dotIndex = trimmed.IndexOf('.');
        if (dotIndex < 0)
        {
            return (null, CleanIdentifier(trimmed));
        }

        var schema = CleanIdentifier(trimmed[..dotIndex]);
        var obj = CleanIdentifier(trimmed[(dotIndex + 1)..]);

        var normSchema = string.IsNullOrEmpty(schema) || schema == "*" ? null : schema;
        var normObj = string.IsNullOrEmpty(obj) || obj == "*" ? null : obj;
        return (normSchema, normObj);
    }

    public static List<string> ParseList(string? input, bool defaultToAll = true)
    {
        if (string.IsNullOrWhiteSpace(input)) return defaultToAll ? [AllObjects] : [];
        var parts = input.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new List<string>();
        foreach (var p in parts)
        {
            var (s, o) = Parse(p);
            var formatted = Format(s, o);
            if (!result.Contains(formatted, StringComparer.OrdinalIgnoreCase))
                result.Add(formatted);
        }
        return result.Count == 0 ? (defaultToAll ? [AllObjects] : []) : result;
    }

    public static List<string> ParseList(IEnumerable<string>? inputs, bool defaultToAll = true)
    {
        if (inputs is null) return defaultToAll ? [AllObjects] : [];
        var result = new List<string>();
        foreach (var item in inputs)
        {
            foreach (var s in ParseList(item, defaultToAll: false))
            {
                if (!result.Contains(s, StringComparer.OrdinalIgnoreCase))
                    result.Add(s);
            }
        }
        return result.Count == 0 ? (defaultToAll ? [AllObjects] : []) : result;
    }

    public static bool HasConflict(string? included, string? excluded, out string conflictingObject) =>
        HasConflict(ParseList(included, defaultToAll: false), ParseList(excluded, defaultToAll: false), out conflictingObject);

    public static bool HasConflict(IEnumerable<string>? included, IEnumerable<string>? excluded, out string conflictingObject)
    {
        conflictingObject = "";
        if (included == null || excluded == null) return false;
        var incList = ParseList(included, defaultToAll: false);
        var excList = ParseList(excluded, defaultToAll: false);

        foreach (var exc in excList)
        {
            if (exc == AllObjects)
            {
                if (incList.Count > 0)
                {
                    conflictingObject = AllObjects;
                    return true;
                }
                continue;
            }

            var (excSchema, excObj) = Parse(exc);

            foreach (var inc in incList)
            {
                if (inc == AllObjects)
                {
                    // Including * while excluding specific objects is explicitly permitted
                    continue;
                }

                // If identical exact strings
                if (string.Equals(inc, exc, StringComparison.OrdinalIgnoreCase))
                {
                    conflictingObject = exc;
                    return true;
                }

                var (incSchema, incObj) = Parse(inc);

                // If same schema and same object (handling null and dbo as equivalent)
                if (!string.IsNullOrEmpty(incObj) && !string.IsNullOrEmpty(excObj))
                {
                    if (string.Equals(incObj, excObj, StringComparison.OrdinalIgnoreCase))
                    {
                        var s1 = string.IsNullOrEmpty(incSchema) ? "dbo" : incSchema;
                        var s2 = string.IsNullOrEmpty(excSchema) ? "dbo" : excSchema;
                        if (string.Equals(s1, s2, StringComparison.OrdinalIgnoreCase))
                        {
                            conflictingObject = exc;
                            return true;
                        }
                    }
                }
                else if (string.IsNullOrEmpty(incObj) && string.IsNullOrEmpty(excObj))
                {
                    // Both are schema wildcards e.g. sales.* and sales.*
                    if (!string.IsNullOrEmpty(incSchema) && !string.IsNullOrEmpty(excSchema) &&
                        string.Equals(incSchema, excSchema, StringComparison.OrdinalIgnoreCase))
                    {
                        conflictingObject = exc;
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public static string Format(string? schema, string? objectName)
    {
        if (string.IsNullOrEmpty(schema) && string.IsNullOrEmpty(objectName)) return AllObjects;
        if (string.IsNullOrEmpty(objectName) || objectName == "*") return $"{schema}.*";
        if (string.IsNullOrEmpty(schema) || schema == "*") return objectName;
        return $"{schema}.{objectName}";
    }

    public static bool Matches(string? pattern, string? schema, string objectName)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        var trimmed = pattern.Trim();
        if (trimmed == AllObjects) return true;

        var (pSchema, pObj) = Parse(trimmed);

        if (!string.IsNullOrEmpty(pObj))
        {
            var objMatches = string.Equals(pObj, objectName, StringComparison.OrdinalIgnoreCase);
            if (!objMatches) return false;

            return SchemaMatches(pSchema, schema);
        }

        if (!string.IsNullOrEmpty(pSchema))
        {
            return SchemaMatches(pSchema, schema);
        }

        return false;
    }

    private static bool SchemaMatches(string? patternSchema, string? targetSchema)
    {
        if (string.IsNullOrEmpty(patternSchema) || patternSchema == "*") return true;
        if (!string.IsNullOrEmpty(targetSchema))
        {
            return string.Equals(patternSchema, targetSchema, StringComparison.OrdinalIgnoreCase);
        }

        // Target schema was omitted in the query; default schemas are "dbo" (SQL Server) and "public" (PostgreSQL)
        return string.Equals(patternSchema, "dbo", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(patternSchema, "public", StringComparison.OrdinalIgnoreCase);
    }

    public static bool MatchesAny(IEnumerable<string>? patterns, string? schema, string objectName)
    {
        if (patterns == null) return false;
        foreach (var p in patterns)
        {
            if (Matches(p, schema, objectName)) return true;
        }
        return false;
    }

    private static string CleanIdentifier(string id) =>
        id.Trim().Trim('[', ']', '`', '"');
}
