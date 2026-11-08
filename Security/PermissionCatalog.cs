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
    public const string SysProcPrefix = "SYSPROC:";

    public static readonly string[] Catalog =
    [
        Select, Insert, Update, Delete, Execute, Create, Alter, Drop, Truncate,
        SpHelpText, SpHelp, ViewDefinition, DynamicSql, LinkedServer, XpCmdShell, Export
    ];

    public static readonly HashSet<string> Writes = new(StringComparer.OrdinalIgnoreCase)
    {
        Insert, Update, Delete, Execute, Create, Alter, Drop, Truncate, DynamicSql, LinkedServer, XpCmdShell
    };

    public static readonly HashSet<string> Dangerous = new(StringComparer.OrdinalIgnoreCase)
    {
        DynamicSql, LinkedServer, XpCmdShell
    };

    public static bool IsDangerous(string code) =>
        Dangerous.Contains(code) || code.StartsWith(SysProcPrefix, StringComparison.OrdinalIgnoreCase);

    public static bool IsValid(string code) =>
        Catalog.Contains(code, StringComparer.OrdinalIgnoreCase) ||
        (code.StartsWith(SysProcPrefix, StringComparison.OrdinalIgnoreCase) && code.Length > SysProcPrefix.Length);

    public static string Label(string code) => code switch
    {
        SpHelpText => "sp_helptext",
        SpHelp => "sp_help",
        ViewDefinition => "VIEW DEFINITION",
        DynamicSql => "Dynamic SQL",
        LinkedServer => "OPENROWSET / linked server",
        XpCmdShell => "xp_cmdshell",
        _ => code
    };
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Developer = "Developer";
    public const string Tester = "Tester";
    public const string Auditor = "Auditor";

}
