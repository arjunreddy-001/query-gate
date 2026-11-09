using System.Text;
using System.Text.RegularExpressions;
using DbAccessManager.Data;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbAccessManager.Security;

public sealed record GatedStatement(string Type, string? Permission, string Preview);

public sealed record ObjectRequirement(string Permission, string? Schema, string ObjectName)
{
    public string TargetDisplay => ObjectScope.Format(Schema, ObjectName);
    public override string ToString() => $"{Permission} on {TargetDisplay}";
}

public sealed class GateAnalysis
{
    public bool Parsed { get; init; }
    public List<string> Batches { get; } = [];
    public List<int> BatchOffsets { get; } = [];
    public List<GatedStatement> Statements { get; } = [];
    public HashSet<string> Requirements { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ObjectRequirement> ObjectRequirements { get; } = [];
    public List<string> HardDenials { get; } = [];
    public List<string> ParseErrors { get; } = [];
    public List<string> UnfilteredWrites { get; } = [];

    public bool HasHardDenial => HardDenials.Count > 0 || ParseErrors.Count > 0;
    public IEnumerable<string> StatementTypes => Statements.Select(s => s.Type).Distinct();
    public bool IsReadOnly => !Requirements.Any(Perm.Writes.Contains);

    public void AddObjectRequirement(string permission, string? schema, string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName)) return;
        var (s, obj) = ObjectScope.Parse(string.IsNullOrEmpty(schema) ? objectName : $"{schema}.{objectName}");
        if (string.IsNullOrEmpty(obj)) return;
        if (obj.StartsWith('#') || obj.StartsWith('@')) return;

        if (!ObjectRequirements.Any(o =>
            string.Equals(o.Permission, permission, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Schema, s, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.ObjectName, obj, StringComparison.OrdinalIgnoreCase)))
        {
            ObjectRequirements.Add(new ObjectRequirement(permission, s, obj));
        }
    }
}

public sealed partial class SqlGate
{
    private static readonly HashSet<string> HarmlessTypes = new(StringComparer.Ordinal)
    {
        nameof(DeclareVariableStatement), nameof(SetVariableStatement), nameof(PrintStatement),
        nameof(IfStatement), nameof(WhileStatement), nameof(BeginEndBlockStatement), nameof(ReturnStatement),
        nameof(TryCatchStatement), nameof(BreakStatement), nameof(ContinueStatement),
        nameof(BeginTransactionStatement), nameof(CommitTransactionStatement), nameof(RollbackTransactionStatement),
        nameof(SaveTransactionStatement), nameof(SetTransactionIsolationLevelStatement), nameof(PredicateSetStatement),
        nameof(SetRowCountStatement), nameof(RaiseErrorStatement), nameof(ThrowStatement), nameof(LabelStatement),
        nameof(GoToStatement), nameof(BeginEndAtomicBlockStatement), nameof(DeclareCursorStatement),
        nameof(OpenCursorStatement), nameof(FetchCursorStatement), nameof(CloseCursorStatement),
        nameof(DeallocateCursorStatement), nameof(DeclareTableVariableStatement), nameof(SetTextSizeStatement)
    };

    private static readonly string[] ForbiddenSecurityMarkers =
        ["Login", "User", "Role", "Server", "Credential", "Certificate", "AsymmetricKey", "SymmetricKey", "MasterKey",
         "Endpoint", "Audit", "Database", "Application", "Availability", "Resource", "Event", "Trigger", "Linked", "Assembly"];

    public GateAnalysis Analyze(string sql) => Analyze(sql, DatabaseEngine.SqlServer);

    public GateAnalysis Analyze(string sql, DatabaseEngine engine)
    {
        var analysis = new GateAnalysis { Parsed = true };
        if (string.IsNullOrWhiteSpace(sql))
        {
            analysis.ParseErrors.Add("The script is empty.");
            return analysis;
        }

        if (engine == DatabaseEngine.SqlServer)
        {
            return AnalyzeSqlServer(sql, analysis);
        }

        return AnalyzeStandardSql(sql, engine, analysis);
    }

    private GateAnalysis AnalyzeSqlServer(string sql, GateAnalysis analysis)
    {
        foreach (var batch in SplitBatches(sql, DatabaseEngine.SqlServer))
        {
            analysis.Batches.Add(batch.Text);
            analysis.BatchOffsets.Add(batch.LineOffset);
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(batch.Text);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    analysis.ParseErrors.Add($"Line {error.Line + batch.LineOffset}, column {error.Column}: {error.Message}");
                }
                continue;
            }

            var visitor = new GateVisitor(analysis);
            fragment.Accept(visitor);
        }

        if (analysis.Statements.Count == 0 && !analysis.HasHardDenial)
        {
            analysis.ParseErrors.Add("No executable statements were found.");
        }

        return analysis;
    }

    private GateAnalysis AnalyzeStandardSql(string sql, DatabaseEngine engine, GateAnalysis analysis)
    {
        var statements = SplitStatements(sql, engine);
        if (statements.Count == 0)
        {
            analysis.ParseErrors.Add("The script is empty.");
            return analysis;
        }

        foreach (var (stmtText, lineOffset) in statements)
        {
            var trimmed = stmtText.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            analysis.Batches.Add(trimmed);
            analysis.BatchOffsets.Add(lineOffset);

            AnalyzeSingleDialectStatement(trimmed, engine, analysis);
        }

        if (analysis.Statements.Count == 0 && !analysis.HasHardDenial)
        {
            analysis.ParseErrors.Add("No executable statements were found.");
        }

        return analysis;
    }

    private static void AnalyzeSingleDialectStatement(string sql, DatabaseEngine engine, GateAnalysis analysis)
    {
        var preview = sql.Length > 120 ? sql[..120] + "…" : sql;
        var stripped = StripCommentsAndStrings(sql, out var hasStrings);
        var tokens = TokenizeKeywords(stripped);

        if (tokens.Count == 0) return;

        var first = tokens[0].ToUpperInvariant();
        var second = tokens.Count > 1 ? tokens[1].ToUpperInvariant() : "";

        // Check Hard Denials
        if (first is "GRANT" or "REVOKE" or "DENY")
        {
            analysis.HardDenials.Add($"Permission statements are not allowed: {preview}");
            analysis.Statements.Add(new GatedStatement(first, null, preview));
            return;
        }

        if (first is "USE")
        {
            analysis.HardDenials.Add("USE is not allowed; each tab is bound to one database.");
            analysis.Statements.Add(new GatedStatement("USE", null, preview));
            return;
        }

        // Security objects creation/modification
        if (first is "CREATE" or "ALTER" or "DROP" or "RENAME")
        {
            var target = second;
            if (ForbiddenSecurityMarkers.Any(m => target.Contains(m, StringComparison.OrdinalIgnoreCase)) ||
                target is "USER" or "ROLE" or "LOGIN" or "SERVER" or "DATABASE" or "SCHEMA" or "EVENT" or "TRIGGER")
            {
                analysis.HardDenials.Add($"Statement type {first} {target} is not allowed.");
                analysis.Statements.Add(new GatedStatement($"{first}_{target}", null, preview));
                return;
            }

            var perm = first switch
            {
                "CREATE" => Perm.Create,
                "ALTER" => Perm.Alter,
                "DROP" => Perm.Drop,
                _ => Perm.Alter
            };
            analysis.Requirements.Add(perm);
            analysis.Statements.Add(new GatedStatement(first, perm, preview));
            return;
        }

        switch (first)
        {
            case "SELECT" or "WITH":
                analysis.Requirements.Add(Perm.Select);
                if (tokens.Any(t => t.Equals("INTO", StringComparison.OrdinalIgnoreCase)))
                {
                    analysis.Requirements.Add(Perm.Create);
                }
                analysis.Statements.Add(new GatedStatement("SELECT", Perm.Select, preview));
                break;

            case "INSERT" or "REPLACE":
                analysis.Requirements.Add(Perm.Insert);
                analysis.Statements.Add(new GatedStatement(first, Perm.Insert, preview));
                break;

            case "UPDATE":
                analysis.Requirements.Add(Perm.Update);
                if (!HasWhereClause(tokens))
                {
                    analysis.UnfilteredWrites.Add($"UPDATE without WHERE: {preview}");
                }
                analysis.Statements.Add(new GatedStatement("UPDATE", Perm.Update, preview));
                break;

            case "DELETE":
                analysis.Requirements.Add(Perm.Delete);
                if (!HasWhereClause(tokens))
                {
                    analysis.UnfilteredWrites.Add($"DELETE without WHERE: {preview}");
                }
                analysis.Statements.Add(new GatedStatement("DELETE", Perm.Delete, preview));
                break;

            case "TRUNCATE":
                analysis.Requirements.Add(Perm.Truncate);
                analysis.Statements.Add(new GatedStatement("TRUNCATE", Perm.Truncate, preview));
                break;

            case "CALL" or "EXEC" or "EXECUTE" or "DO" or "PERFORM":
                analysis.Requirements.Add(Perm.Execute);
                analysis.Statements.Add(new GatedStatement("EXECUTE", Perm.Execute, preview));
                break;

            case "SHOW" or "DESCRIBE" or "DESC" or "EXPLAIN":
                analysis.Requirements.Add(Perm.Select);
                analysis.Statements.Add(new GatedStatement(first, Perm.Select, preview));
                break;

            case "SET" or "BEGIN" or "COMMIT" or "ROLLBACK" or "START" or "SAVEPOINT":
                // Harmless session/transaction control
                analysis.Statements.Add(new GatedStatement(first, null, preview));
                break;

            default:
                analysis.HardDenials.Add($"Statement type {first} is not allowed.");
                analysis.Statements.Add(new GatedStatement(first, null, preview));
                break;
        }

        ExtractStandardSqlObjects(sql, first, analysis);
    }

    private static void ExtractStandardSqlObjects(string sql, string first, GateAnalysis analysis)
    {
        switch (first)
        {
            case "INSERT" or "REPLACE":
            {
                var match = Regex.Match(sql, @"\b(?:INSERT\s+INTO|REPLACE(?:\s+INTO)?)\s+([a-zA-Z0-9_`""\[\]]+(?:\.[a-zA-Z0-9_`""\[\]]+)?)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var (schema, obj) = ObjectScope.Parse(match.Groups[1].Value);
                    if (obj != null) analysis.AddObjectRequirement(Perm.Insert, schema, obj);
                }
                break;
            }
            case "UPDATE":
            {
                var match = Regex.Match(sql, @"\bUPDATE\s+([a-zA-Z0-9_`""\[\]]+(?:\.[a-zA-Z0-9_`""\[\]]+)?)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var (schema, obj) = ObjectScope.Parse(match.Groups[1].Value);
                    if (obj != null) analysis.AddObjectRequirement(Perm.Update, schema, obj);
                }
                break;
            }
            case "DELETE":
            {
                var match = Regex.Match(sql, @"\bDELETE(?:\s+FROM)?\s+([a-zA-Z0-9_`""\[\]]+(?:\.[a-zA-Z0-9_`""\[\]]+)?)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var (schema, obj) = ObjectScope.Parse(match.Groups[1].Value);
                    if (obj != null) analysis.AddObjectRequirement(Perm.Delete, schema, obj);
                }
                break;
            }
            case "TRUNCATE":
            {
                var match = Regex.Match(sql, @"\bTRUNCATE(?:\s+TABLE)?\s+([a-zA-Z0-9_`""\[\]]+(?:\.[a-zA-Z0-9_`""\[\]]+)?)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var (schema, obj) = ObjectScope.Parse(match.Groups[1].Value);
                    if (obj != null) analysis.AddObjectRequirement(Perm.Truncate, schema, obj);
                }
                break;
            }
            case "CALL" or "EXEC" or "EXECUTE":
            {
                var match = Regex.Match(sql, @"\b(?:CALL|EXEC|EXECUTE)\s+([a-zA-Z0-9_`""\[\]]+(?:\.[a-zA-Z0-9_`""\[\]]+)?)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var (schema, obj) = ObjectScope.Parse(match.Groups[1].Value);
                    if (obj != null) analysis.AddObjectRequirement(Perm.Execute, schema, obj);
                }
                break;
            }
            case "CREATE" or "ALTER" or "DROP":
            {
                var match = Regex.Match(sql, @"\b(?:CREATE|ALTER|DROP)\s+TABLE(?:\s+IF(?:\s+NOT)?\s+EXISTS)?\s+([a-zA-Z0-9_`""\[\]]+(?:\.[a-zA-Z0-9_`""\[\]]+)?)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var perm = first == "CREATE" ? Perm.Create : first == "ALTER" ? Perm.Alter : Perm.Drop;
                    var (schema, obj) = ObjectScope.Parse(match.Groups[1].Value);
                    if (obj != null) analysis.AddObjectRequirement(perm, schema, obj);
                }
                break;
            }
        }

        // Extract SELECT sources from FROM / JOIN
        var matches = Regex.Matches(sql, @"\b(?:FROM|JOIN)\s+([a-zA-Z0-9_`""\[\]]+(?:\.[a-zA-Z0-9_`""\[\]]+)?)", RegexOptions.IgnoreCase);
        foreach (Match m in matches)
        {
            var raw = m.Groups[1].Value;
            var (schema, obj) = ObjectScope.Parse(raw);
            if (obj != null && !obj.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                analysis.AddObjectRequirement(Perm.Select, schema, obj);
            }
        }
    }

    private static bool HasWhereClause(List<string> tokens) =>
        tokens.Any(t => t.Equals("WHERE", StringComparison.OrdinalIgnoreCase));

    private static List<string> TokenizeKeywords(string text)
    {
        var tokens = new List<string>();
        var matches = Regex.Matches(text, @"\b[a-zA-Z_][a-zA-Z0-9_]*\b");
        foreach (Match m in matches)
        {
            tokens.Add(m.Value);
        }
        return tokens;
    }

    private static string StripCommentsAndStrings(string sql, out bool hasStrings)
    {
        hasStrings = false;
        var sb = new StringBuilder(sql.Length);
        var inString = false;
        var inIdentifier = false;
        var inBlockComment = false;
        var inLineComment = false;
        var inDollarString = false;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (inLineComment)
            {
                if (c == '\n') { inLineComment = false; sb.Append(' '); }
                continue;
            }

            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; sb.Append(' '); }
                continue;
            }

            if (inDollarString)
            {
                if (c == '$' && next == '$') { inDollarString = false; i++; sb.Append(' '); }
                continue;
            }

            if (inString)
            {
                if (c == '\'')
                {
                    if (next == '\'') { i++; }
                    else { inString = false; sb.Append(' '); }
                }
                continue;
            }

            if (inIdentifier)
            {
                if (c is '"' or '`' or ']')
                {
                    inIdentifier = false;
                    sb.Append(' ');
                }
                continue;
            }

            // Start of comment
            if (c == '-' && next == '-')
            {
                inLineComment = true;
                i++;
                continue;
            }
            if (c == '#')
            {
                inLineComment = true;
                continue;
            }
            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            // Start of string
            if (c == '\'')
            {
                inString = true;
                hasStrings = true;
                continue;
            }

            // Start of dollar quote in PostgreSQL ($$)
            if (c == '$' && next == '$')
            {
                inDollarString = true;
                hasStrings = true;
                i++;
                continue;
            }

            // Start of quoted identifier
            if (c is '"' or '`' or '[')
            {
                inIdentifier = true;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    public static IReadOnlyList<(string Text, int LineOffset)> SplitBatches(string sql) =>
        SplitBatches(sql, DatabaseEngine.SqlServer);

    public static IReadOnlyList<(string Text, int LineOffset)> SplitBatches(string sql, DatabaseEngine engine)
    {
        if (engine == DatabaseEngine.SqlServer)
        {
            return SplitSqlServerBatches(sql);
        }

        return SplitStatements(sql, engine);
    }

    private static IReadOnlyList<(string Text, int LineOffset)> SplitSqlServerBatches(string sql)
    {
        var batches = new List<(string, int)>();
        var current = new StringBuilder();
        var startLine = 0;
        var lineNumber = 0;
        var inBlockComment = false;
        var inString = false;

        foreach (var rawLine in sql.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine;
            var isSeparator = !inBlockComment && !inString &&
                Regex.IsMatch(line, @"^\s*GO(\s+\d+)?\s*(--.*)?$", RegexOptions.IgnoreCase);

            if (isSeparator)
            {
                Flush();
                startLine = lineNumber + 1;
            }
            else
            {
                current.AppendLine(line);
                UpdateLexState(line, ref inBlockComment, ref inString);
            }

            lineNumber++;
        }

        Flush();
        return batches;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(current.ToString()))
            {
                batches.Add((current.ToString(), startLine));
            }
            current.Clear();
        }
    }

    private static IReadOnlyList<(string Text, int LineOffset)> SplitStatements(string sql, DatabaseEngine engine)
    {
        var statements = new List<(string, int)>();
        var current = new StringBuilder();
        var inString = false;
        var inBlockComment = false;
        var inLineComment = false;
        var inDollarString = false;
        var inBacktick = false;
        var inDoubleQuote = false;

        var startLine = 0;
        var currentLine = 0;

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';

            if (c == '\n') currentLine++;

            if (inLineComment)
            {
                current.Append(c);
                if (c == '\n') inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                current.Append(c);
                if (c == '*' && next == '/') { current.Append(next); inBlockComment = false; i++; }
                continue;
            }

            if (inDollarString)
            {
                current.Append(c);
                if (c == '$' && next == '$') { current.Append(next); inDollarString = false; i++; }
                continue;
            }

            if (inString)
            {
                current.Append(c);
                if (c == '\'')
                {
                    if (next == '\'') { current.Append(next); i++; }
                    else inString = false;
                }
                continue;
            }

            if (inBacktick)
            {
                current.Append(c);
                if (c == '`')
                {
                    if (next == '`') { current.Append(next); i++; }
                    else inBacktick = false;
                }
                continue;
            }

            if (inDoubleQuote)
            {
                current.Append(c);
                if (c == '"')
                {
                    if (next == '"') { current.Append(next); i++; }
                    else inDoubleQuote = false;
                }
                continue;
            }

            // Not inside any literal or comment:
            if (c == '-' && next == '-')
            {
                inLineComment = true;
                current.Append(c).Append(next);
                i++;
                continue;
            }
            if (c == '#' && (engine == DatabaseEngine.MySql || engine == DatabaseEngine.MariaDb))
            {
                inLineComment = true;
                current.Append(c);
                continue;
            }
            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                current.Append(c).Append(next);
                i++;
                continue;
            }
            if (c == '$' && next == '$' && engine == DatabaseEngine.PostgreSql)
            {
                inDollarString = true;
                current.Append(c).Append(next);
                i++;
                continue;
            }
            if (c == '\'')
            {
                inString = true;
                current.Append(c);
                continue;
            }
            if (c == '`' && (engine == DatabaseEngine.MySql || engine == DatabaseEngine.MariaDb))
            {
                inBacktick = true;
                current.Append(c);
                continue;
            }
            if (c == '"')
            {
                inDoubleQuote = true;
                current.Append(c);
                continue;
            }

            // Semicolon separator
            if (c == ';')
            {
                current.Append(c);
                var stmt = current.ToString();
                if (!string.IsNullOrWhiteSpace(stmt))
                {
                    statements.Add((stmt, startLine));
                }
                current.Clear();
                startLine = currentLine;
                continue;
            }

            current.Append(c);
        }

        var remaining = current.ToString();
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            statements.Add((remaining, startLine));
        }

        return statements;
    }

    private static void UpdateLexState(string line, ref bool inBlockComment, ref bool inString)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            var next = i + 1 < line.Length ? line[i + 1] : '\0';
            if (inBlockComment)
            {
                if (c == '*' && next == '/') { inBlockComment = false; i++; }
            }
            else if (inString)
            {
                if (c == '\'') { if (next == '\'') i++; else inString = false; }
            }
            else if (c == '-' && next == '-')
            {
                return;
            }
            else if (c == '/' && next == '*') { inBlockComment = true; i++; }
            else if (c == '\'') inString = true;
        }
    }

    public string Format(string sql, bool upperKeywords = true, int indentSize = 4) =>
        Format(sql, DatabaseEngine.SqlServer, upperKeywords, indentSize);

    public string Format(string sql, DatabaseEngine engine, bool upperKeywords = true, int indentSize = 4)
    {
        if (engine == DatabaseEngine.SqlServer)
        {
            var parser = new TSql160Parser(true);
            using var reader = new StringReader(sql);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"Cannot format: {errors[0].Message} (line {errors[0].Line}).");
            }

            var generator = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
            {
                KeywordCasing = upperKeywords ? KeywordCasing.Uppercase : KeywordCasing.Lowercase,
                IndentationSize = Math.Clamp(indentSize, 1, 8),
                AlignClauseBodies = false,
                IncludeSemicolons = false,
                NewLineBeforeFromClause = true,
                NewLineBeforeWhereClause = true,
                NewLineBeforeOrderByClause = true,
                NewLineBeforeGroupByClause = true,
                NewLineBeforeJoinClause = true
            });
            generator.GenerateScript(fragment, out var script);
            return script;
        }

        // Standard SQL keyword formatting for Postgres / MySQL / MariaDB
        return FormatGenericSql(sql, upperKeywords);
    }

    private static string FormatGenericSql(string sql, bool upperKeywords)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;
        var keywords = new[]
        {
            "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET",
            "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "JOIN", "LEFT JOIN",
            "RIGHT JOIN", "INNER JOIN", "FULL JOIN", "CROSS JOIN", "ON", "AND", "OR",
            "CREATE TABLE", "ALTER TABLE", "DROP TABLE", "TRUNCATE TABLE", "EXPLAIN", "SHOW"
        };

        var result = sql;
        foreach (var kw in keywords)
        {
            var pattern = @"\b" + Regex.Escape(kw).Replace(@"\ ", @"\s+") + @"\b";
            result = Regex.Replace(result, pattern, upperKeywords ? kw : kw.ToLowerInvariant(), RegexOptions.IgnoreCase);
        }

        return result;
    }

    private sealed class GateVisitor(GateAnalysis analysis) : TSqlFragmentVisitor
    {
        private readonly HashSet<NamedTableReference> _writeTargets = [];
        private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(CommonTableExpression node)
        {
            if (node.ExpressionName?.Value is string cteName)
            {
                _cteNames.Add(cteName);
            }
        }

        public override void Visit(TSqlStatement node)
        {
            if (node is StatementWithCtesAndXmlNamespaces stmtWithCtes && stmtWithCtes.WithCtesAndXmlNamespaces?.CommonTableExpressions != null)
            {
                foreach (var cte in stmtWithCtes.WithCtesAndXmlNamespaces.CommonTableExpressions)
                {
                    if (cte.ExpressionName?.Value is string name) _cteNames.Add(name);
                }
            }

            var type = node.GetType().Name;
            var preview = Preview(node);
            string? permission = null;

            switch (node)
            {
                case SelectStatement select:
                    permission = Perm.Select;
                    Require(Perm.Select);
                    if (select.Into is not null)
                    {
                        Require(Perm.Create);
                        RequireObject(Perm.Create, select.Into);
                    }
                    break;
                case InsertStatement insert:
                    permission = Perm.Insert;
                    Require(Perm.Insert);
                    if (insert.InsertSpecification?.Target is NamedTableReference insTarget)
                    {
                        _writeTargets.Add(insTarget);
                        RequireObject(Perm.Insert, insTarget.SchemaObject);
                    }
                    break;
                case UpdateStatement update:
                    permission = Perm.Update;
                    Require(Perm.Update);
                    if (update.UpdateSpecification.WhereClause is null) analysis.UnfilteredWrites.Add($"UPDATE without WHERE: {preview}");
                    if (update.UpdateSpecification?.Target is NamedTableReference updTarget)
                    {
                        _writeTargets.Add(updTarget);
                        RequireObject(Perm.Update, updTarget.SchemaObject);
                    }
                    break;
                case DeleteStatement delete:
                    permission = Perm.Delete;
                    Require(Perm.Delete);
                    if (delete.DeleteSpecification.WhereClause is null) analysis.UnfilteredWrites.Add($"DELETE without WHERE: {preview}");
                    if (delete.DeleteSpecification?.Target is NamedTableReference delTarget)
                    {
                        _writeTargets.Add(delTarget);
                        RequireObject(Perm.Delete, delTarget.SchemaObject);
                    }
                    break;
                case MergeStatement merge:
                    permission = "MERGE";
                    Require(Perm.Insert);
                    Require(Perm.Update);
                    Require(Perm.Delete);
                    if (merge.MergeSpecification?.Target is NamedTableReference mrgTarget)
                    {
                        _writeTargets.Add(mrgTarget);
                        RequireObject(Perm.Insert, mrgTarget.SchemaObject);
                        RequireObject(Perm.Update, mrgTarget.SchemaObject);
                        RequireObject(Perm.Delete, mrgTarget.SchemaObject);
                    }
                    break;
                case TruncateTableStatement truncate:
                    permission = Perm.Truncate;
                    Require(Perm.Truncate);
                    RequireObject(Perm.Truncate, truncate.TableName);
                    break;
                case ExecuteStatement:
                    permission = Perm.Execute;
                    break;
                case GrantStatement or DenyStatement or RevokeStatement:
                    analysis.HardDenials.Add($"Permission statements are not allowed: {preview}");
                    break;
                case UseStatement:
                    analysis.HardDenials.Add("USE is not allowed; each tab is bound to one database.");
                    break;
                default:
                    ClassifyOther(node, type, preview, ref permission);
                    break;
            }

            analysis.Statements.Add(new GatedStatement(Friendly(type), permission, preview));
        }

        public override void Visit(NamedTableReference node)
        {
            if (!_writeTargets.Contains(node))
            {
                RequireObject(Perm.Select, node.SchemaObject);
            }
        }

        public override void Visit(AlterTableStatement node)
        {
            RequireObject(Perm.Alter, node.SchemaObjectName);
        }

        public override void Visit(DropTableStatement node)
        {
            if (node.Objects != null)
            {
                foreach (var obj in node.Objects)
                {
                    RequireObject(Perm.Drop, obj);
                }
            }
        }

        private void ClassifyOther(TSqlStatement node, string type, string preview, ref string? permission)
        {
            if (HarmlessTypes.Contains(type)) return;

            var isCreate = type.StartsWith("Create", StringComparison.Ordinal);
            var isAlter = type.StartsWith("Alter", StringComparison.Ordinal);
            var isDrop = type.StartsWith("Drop", StringComparison.Ordinal);
            if (isCreate || isAlter || isDrop)
            {
                var objectName = type[(isCreate || isDrop ? (isCreate ? 6 : 4) : 5)..];
                if (ForbiddenSecurityMarkers.Any(marker => objectName.Contains(marker, StringComparison.Ordinal)))
                {
                    analysis.HardDenials.Add($"Statement type {Friendly(type)} is not allowed.");
                    return;
                }

                permission = isCreate ? Perm.Create : isAlter ? Perm.Alter : Perm.Drop;
                Require(permission);
                return;
            }

            analysis.HardDenials.Add($"Statement type {Friendly(type)} is not allowed.");
        }

        public override void Visit(ExecuteSpecification node)
        {
            Require(Perm.Execute);

            if (node.ExecutableEntity is ExecutableStringList)
            {
                Require(Perm.DynamicSql);
                return;
            }

            if (node.ExecutableEntity is not ExecutableProcedureReference procedure)
            {
                analysis.HardDenials.Add("Unsupported EXEC form.");
                return;
            }

            var reference = procedure.ProcedureReference;
            if (reference?.ProcedureVariable is not null)
            {
                Require(Perm.DynamicSql);
                return;
            }

            var name = reference?.ProcedureReference?.Name;
            if (name is null)
            {
                analysis.HardDenials.Add("Unsupported EXEC form.");
                return;
            }

            var procName = name.BaseIdentifier.Value;
            var schema = name.SchemaIdentifier?.Value;
            var isSystem = (schema is null || schema.Equals("sys", StringComparison.OrdinalIgnoreCase) || schema.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                && (procName.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) || procName.StartsWith("xp_", StringComparison.OrdinalIgnoreCase));

            if (!isSystem)
            {
                RequireObject(Perm.Execute, name);
                return;
            }

            switch (procName.ToLowerInvariant())
            {
                case "sp_helptext": Require(Perm.SpHelpText); break;
                case "sp_help": Require(Perm.SpHelp); break;
                case "sp_executesql": Require(Perm.DynamicSql); break;
                case "xp_cmdshell": Require(Perm.XpCmdShell); break;
                default: Require(Perm.SysProcPrefix + procName.ToLowerInvariant()); break;
            }
        }

        private void RequireObject(string permission, SchemaObjectName? name)
        {
            if (name is null) return;
            var baseName = name.BaseIdentifier?.Value;
            if (string.IsNullOrWhiteSpace(baseName)) return;
            if (baseName.StartsWith('#') || baseName.StartsWith('@')) return;
            if (_cteNames.Contains(baseName)) return;

            var schema = name.SchemaIdentifier?.Value;
            analysis.AddObjectRequirement(permission, schema, baseName);
        }

        public override void Visit(SchemaObjectName node)
        {
            if (node.ServerIdentifier is not null) Require(Perm.LinkedServer);
            if (node.DatabaseIdentifier is not null)
            {
                analysis.HardDenials.Add($"Cross-database reference '{node.DatabaseIdentifier.Value}.' is not allowed; each tab is bound to one database.");
            }
        }

        public override void Visit(OpenRowsetTableReference node) => Require(Perm.LinkedServer);
        public override void Visit(OpenQueryTableReference node) => Require(Perm.LinkedServer);
        public override void Visit(AdHocTableReference node) => Require(Perm.LinkedServer);
        public override void Visit(BulkOpenRowset node) => Require(Perm.LinkedServer);
        public override void Visit(InternalOpenRowset node) => Require(Perm.LinkedServer);

        public override void Visit(FunctionCall node)
        {
            var name = node.FunctionName?.Value;
            if (name is not null && (name.Equals("OPENROWSET", StringComparison.OrdinalIgnoreCase) ||
                                     name.Equals("OPENQUERY", StringComparison.OrdinalIgnoreCase) ||
                                     name.Equals("OPENDATASOURCE", StringComparison.OrdinalIgnoreCase)))
            {
                Require(Perm.LinkedServer);
            }
        }

        private void Require(string permission) => analysis.Requirements.Add(permission);

        private static string Friendly(string type) =>
            type.EndsWith("Statement", StringComparison.Ordinal) ? type[..^"Statement".Length].ToUpperInvariant() : type.ToUpperInvariant();

        private static string Preview(TSqlFragment node)
        {
            var tokens = node.ScriptTokenStream;
            if (tokens is null || node.FirstTokenIndex < 0) return node.GetType().Name;
            var text = string.Concat(Enumerable.Range(node.FirstTokenIndex, Math.Min(node.LastTokenIndex - node.FirstTokenIndex + 1, 40))
                .Select(i => tokens[i].Text));
            return text.Length > 120 ? text[..120] + "…" : text;
        }
    }
}
