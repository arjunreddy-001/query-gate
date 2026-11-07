using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbAccessManager.Security;

public sealed record GatedStatement(string Type, string? Permission, string Preview);

public sealed class GateAnalysis
{
    public bool Parsed { get; init; }
    public List<string> Batches { get; } = [];
    public List<int> BatchOffsets { get; } = [];
    public List<GatedStatement> Statements { get; } = [];
    public HashSet<string> Requirements { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> HardDenials { get; } = [];
    public List<string> ParseErrors { get; } = [];
    public List<string> UnfilteredWrites { get; } = [];

    public bool HasHardDenial => HardDenials.Count > 0 || ParseErrors.Count > 0;
    public IEnumerable<string> StatementTypes => Statements.Select(s => s.Type).Distinct();
    public bool IsReadOnly => !Requirements.Any(Perm.Writes.Contains);
}

public sealed class SqlGate
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

    public GateAnalysis Analyze(string sql)
    {
        var analysis = new GateAnalysis { Parsed = true };
        if (string.IsNullOrWhiteSpace(sql))
        {
            analysis.ParseErrors.Add("The script is empty.");
            return analysis;
        }

        foreach (var batch in SplitBatches(sql))
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

    public static IReadOnlyList<(string Text, int LineOffset)> SplitBatches(string sql)
    {
        var batches = new List<(string, int)>();
        var current = new System.Text.StringBuilder();
        var startLine = 0;
        var lineNumber = 0;
        var inBlockComment = false;
        var inString = false;

        foreach (var rawLine in sql.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine;
            var isSeparator = !inBlockComment && !inString &&
                System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*GO(\s+\d+)?\s*(--.*)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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

    public string Format(string sql, bool upperKeywords, int indentSize)
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

    private sealed class GateVisitor(GateAnalysis analysis) : TSqlFragmentVisitor
    {
        public override void Visit(TSqlStatement node)
        {
            var type = node.GetType().Name;
            var preview = Preview(node);
            string? permission = null;

            switch (node)
            {
                case SelectStatement select:
                    permission = Perm.Select;
                    Require(Perm.Select);
                    if (select.Into is not null) Require(Perm.Create);
                    break;
                case InsertStatement:
                    permission = Perm.Insert;
                    Require(Perm.Insert);
                    break;
                case UpdateStatement update:
                    permission = Perm.Update;
                    Require(Perm.Update);
                    if (update.UpdateSpecification.WhereClause is null) analysis.UnfilteredWrites.Add($"UPDATE without WHERE: {preview}");
                    break;
                case DeleteStatement delete:
                    permission = Perm.Delete;
                    Require(Perm.Delete);
                    if (delete.DeleteSpecification.WhereClause is null) analysis.UnfilteredWrites.Add($"DELETE without WHERE: {preview}");
                    break;
                case MergeStatement:
                    permission = "MERGE";
                    Require(Perm.Insert);
                    Require(Perm.Update);
                    Require(Perm.Delete);
                    break;
                case TruncateTableStatement:
                    permission = Perm.Truncate;
                    Require(Perm.Truncate);
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

            if (!isSystem) return;

            switch (procName.ToLowerInvariant())
            {
                case "sp_helptext": Require(Perm.SpHelpText); break;
                case "sp_help": Require(Perm.SpHelp); break;
                case "sp_executesql": Require(Perm.DynamicSql); break;
                case "xp_cmdshell": Require(Perm.XpCmdShell); break;
                default: Require(Perm.SysProcPrefix + procName.ToLowerInvariant()); break;
            }
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
