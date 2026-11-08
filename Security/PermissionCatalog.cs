namespace DbAccessManager.Security;

public static class Perm
{
    public const string Select = "SELECT";
    public const string Insert = "INSERT";
    public const string Update = "UPDATE";
    public const string Delete = "DELETE";
    public const string Execute = "EXECUTE";
    public const string Create = "CREATE";
    public const string Alter = "ALTER";
    public const string Drop = "DROP";
    public const string Truncate = "TRUNCATE";
    public const string SpHelpText = "SP_HELPTEXT";
    public const string SpHelp = "SP_HELP";
    public const string ViewDefinition = "VIEW_DEFINITION";
    public const string DynamicSql = "DYNAMIC_SQL";
    public const string LinkedServer = "LINKED_SERVER";
    public const string XpCmdShell = "XP_CMDSHELL";
    public const string Export = "EXPORT";
    public const string Unmask = "UNMASK";
    public const string SysProcPrefix = "SYSPROC:";

    public static readonly string[] Catalog =
    [
        Select, Insert, Update, Delete, Execute, Create, Alter, Drop, Truncate,
        SpHelpText, SpHelp, ViewDefinition, DynamicSql, LinkedServer, XpCmdShell, Export, Unmask
    ];

    public static readonly HashSet<string> Writes = new(StringComparer.OrdinalIgnoreCase)
    {
        Insert, Update, Delete, Execute, Create, Alter, Drop, Truncate, DynamicSql, LinkedServer, XpCmdShell
    };

    public static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        DynamicSql, Unmask, LinkedServer, XpCmdShell
    };

    public static readonly HashSet<string> HighRisk = Dangerous;

    public static bool IsHighRisk(string code) =>
        HighRisk.Contains(code) || code.StartsWith(SysProcPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsDangerous(string code) => IsHighRisk(code);

    public static string HighRiskReason(string code) => code switch
    {
        DynamicSql => "High risk: Allows executing arbitrary ad-hoc T-SQL strings via sp_executesql, which can bypass query guardrails.",
        Unmask => "High risk: Bypasses data masking to expose sensitive PII and confidential records without redaction.",
        LinkedServer => "High risk: Opens connections to external servers and distributed data providers via OPENROWSET/OPENQUERY.",
        XpCmdShell => "Critical risk: Spawns an operating system command shell process directly on the database host server.",
        _ => "High-risk administrative permission with significant security implications."
    };

    public static string Normalize(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var upper = code.Trim().ToUpperInvariant().Replace(' ', '_');
        return upper switch
        {
            "VIEWDEFINITION" => ViewDefinition,
            "DYNAMICSQL" => DynamicSql,
            "XPCMDSHELL" => XpCmdShell,
            "LINKEDSERVER" => LinkedServer,
            "SPHELPTEXT" => SpHelpText,
            "SPHELP" => SpHelp,
            _ => upper
        };
    }

    public static bool IsValid(string code)
    {
        var norm = Normalize(code);
        return Catalog.Contains(norm, StringComparer.OrdinalIgnoreCase) ||
               (norm.StartsWith(SysProcPrefix, StringComparison.OrdinalIgnoreCase) && norm.Length > SysProcPrefix.Length);
    }

    public static string Label(string code) => code switch
    {
        SpHelpText => "sp_helptext",
        SpHelp => "sp_help",
        ViewDefinition => "VIEW DEFINITION",
        DynamicSql => "Dynamic SQL",
        LinkedServer => "OPENROWSET / linked server",
        XpCmdShell => "xp_cmdshell",
        Unmask => "View unmasked data",
        _ => code
    };

    public static string Description(string code) => code switch
    {
        Select => "Read rows from tables and views using SELECT queries.",
        Insert => "Add new rows to database tables using INSERT statements.",
        Update => "Modify existing row values in database tables using UPDATE statements.",
        Delete => "Remove rows from database tables using DELETE statements.",
        Execute => "Run stored procedures, functions, and commands with EXEC/EXECUTE.",
        Create => "Create new database objects like tables, views, and schemas.",
        Alter => "Modify schemas, structures, or definitions of existing database objects.",
        Drop => "Permanently delete database objects (tables, views, procedures).",
        Truncate => "Quickly remove all rows from a table using TRUNCATE TABLE.",
        SpHelpText => "Inspect stored procedure, trigger, and view source definitions via sp_helptext.",
        SpHelp => "Inspect table structures, column definitions, and object metadata via sp_help.",
        ViewDefinition => "View schema definitions and catalog metadata for database objects.",
        DynamicSql => "Execute dynamic T-SQL strings via sp_executesql or EXEC('...').",
        LinkedServer => "Access external linked servers and ad-hoc data sources via OPENROWSET/OPENQUERY.",
        XpCmdShell => "Execute operating system shell commands on the host via xp_cmdshell.",
        Export => "Download query result sets from the query workbench as CSV, XLSX, or XLSB.",
        Unmask => "View original unmasked sensitive data and PII without redaction rules applied.",
        _ when code.StartsWith(SysProcPrefix, StringComparison.OrdinalIgnoreCase) =>
            $"Execute system stored procedure {code[SysProcPrefix.Length..]}.",
        _ => "Perform operations covered by this permission grant."
    };
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Developer = "Developer";
    public const string Tester = "Tester";
    public const string Auditor = "Auditor";

}
