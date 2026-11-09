using DbAccessManager.Data;
using DbAccessManager.Security;
using Xunit;

namespace DbAccessManager.Tests;

public sealed class ObjectLevelPermissionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly PermissionResolver _resolver = new();
    private readonly SqlGate _gate = new();

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("*", null, null)]
    [InlineData("dbo.*", "dbo", null)]
    [InlineData("sales.*", "sales", null)]
    [InlineData("dbo.Customers", "dbo", "Customers")]
    [InlineData("[dbo].[Customers]", "dbo", "Customers")]
    [InlineData("`public`.`users`", "public", "users")]
    [InlineData("Orders", null, "Orders")]
    public void ObjectScope_Parse_ExtractsSchemaAndObject(string? input, string? expectedSchema, string? expectedObject)
    {
        var (schema, obj) = ObjectScope.Parse(input);
        Assert.Equal(expectedSchema, schema);
        Assert.Equal(expectedObject, obj);
    }

    [Theory]
    [InlineData(null, null, "*")]
    [InlineData("dbo", null, "dbo.*")]
    [InlineData("sales", "*", "sales.*")]
    [InlineData("dbo", "Orders", "dbo.Orders")]
    [InlineData(null, "Orders", "Orders")]
    public void ObjectScope_Format_FormatsCorrectly(string? schema, string? obj, string expected)
    {
        var result = ObjectScope.Format(schema, obj);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolveObject_InheritsDatabaseWideAllow()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", false, true, "Developer", Schema: null, ObjectName: null)
        };

        var result = _resolver.ResolveObject("SELECT", "dbo", "Customers", rules, Now);

        Assert.True(result.IsAllowed);
        Assert.Equal(PermissionSource.Role, result.Source);
    }

    [Fact]
    public void ResolveObject_ObjectDenyOverridesDatabaseWideAllow()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", false, true, "Developer", Schema: null, ObjectName: null),
            new PermissionRule("SELECT", false, false, "Developer", Schema: "dbo", ObjectName: "Salaries")
        };

        var allowed = _resolver.ResolveObject("SELECT", "dbo", "Customers", rules, Now);
        var denied = _resolver.ResolveObject("SELECT", "dbo", "Salaries", rules, Now);

        Assert.True(allowed.IsAllowed);
        Assert.False(denied.IsAllowed);
    }

    [Fact]
    public void ResolveObject_SchemaLevelGrantAllowsSchemaObjectsOnly()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", false, true, "Developer", Schema: "sales", ObjectName: null)
        };

        var salesOrders = _resolver.ResolveObject("SELECT", "sales", "Orders", rules, Now);
        var hrEmployees = _resolver.ResolveObject("SELECT", "hr", "Employees", rules, Now);

        Assert.True(salesOrders.IsAllowed);
        Assert.False(hrEmployees.IsAllowed);
    }

    [Fact]
    public void ResolveObject_SpecificObjectGrantWithoutDatabaseWideAccess()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", true, true, Schema: "dbo", ObjectName: "Customers")
        };

        var customers = _resolver.ResolveObject("SELECT", "dbo", "Customers", rules, Now);
        var orders = _resolver.ResolveObject("SELECT", "dbo", "Orders", rules, Now);

        Assert.True(customers.IsAllowed);
        Assert.False(orders.IsAllowed);
        Assert.Equal(PermissionSource.User, customers.Source);
    }

    [Fact]
    public void ResolveObject_UserRuleOverridesRoleRule()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", false, false, "Restricted", Schema: "dbo", ObjectName: "Confidential"),
            new PermissionRule("SELECT", true, true, Schema: "dbo", ObjectName: "Confidential")
        };

        var result = _resolver.ResolveObject("SELECT", "dbo", "Confidential", rules, Now);

        Assert.True(result.IsAllowed);
        Assert.Equal(PermissionSource.User, result.Source);
    }

    [Fact]
    public void ObjectScope_ParseList_ParsesMultipleScopesCorrectly()
    {
        var input = "dbo.Customers, sales.*, Orders, *";
        var parsed = ObjectScope.ParseList(input);

        Assert.Equal(4, parsed.Count);
        Assert.Contains("dbo.Customers", parsed);
        Assert.Contains("sales.*", parsed);
        Assert.Contains("Orders", parsed);
        Assert.Contains("*", parsed);
    }

    [Fact]
    public void Resolve_PopulatesAllowedAndDeniedScopes()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", false, true, "Developer", Schema: "dbo", ObjectName: "Customers"),
            new PermissionRule("SELECT", false, true, "Developer", Schema: "sales", ObjectName: null),
            new PermissionRule("SELECT", false, false, "Developer", Schema: "dbo", ObjectName: "Salaries")
        };

        var effective = _resolver.Resolve("SELECT", rules, Now);

        Assert.True(effective.IsAllowed);
        Assert.Contains("dbo.Customers", effective.AllowedScopes);
        Assert.Contains("sales.*", effective.AllowedScopes);
        Assert.Contains("dbo.Salaries", effective.DeniedScopes);
    }

    [Fact]
    public void Resolve_DatabaseWideAllow_HasWildcardScope()
    {
        var rules = new[]
        {
            new PermissionRule("SELECT", false, true, "Developer", Schema: null, ObjectName: null)
        };

        var effective = _resolver.Resolve("SELECT", rules, Now);

        Assert.True(effective.IsAllowed);
        Assert.Contains("*", effective.AllowedScopes);
    }

    [Theory]
    [InlineData("*", "dbo", "Customers", true)]
    [InlineData("sales.*", "sales", "Orders", true)]
    [InlineData("sales.*", "dbo", "Orders", false)]
    [InlineData("dbo.Customers", "dbo", "Customers", true)]
    [InlineData("dbo.Customers", null, "Customers", true)]
    [InlineData("public.Customers", null, "Customers", true)]
    [InlineData("sales.Customers", null, "Customers", false)]
    [InlineData("dbo.*", null, "Customers", true)]
    [InlineData("sales.*", null, "Customers", false)]
    [InlineData("dbo.Customers", "dbo", "Orders", false)]
    [InlineData("Customers", "dbo", "Customers", true)]
    [InlineData("Customers", null, "Customers", true)]
    [InlineData("*.passwords", "dbo", "passwords", true)]
    [InlineData("*.passwords", "security", "passwords", true)]
    [InlineData("*.passwords", "security", "users", false)]
    public void ObjectScope_Matches_EvaluatesCorrectly(string pattern, string? schema, string objectName, bool expected)
    {
        var result = ObjectScope.Matches(pattern, schema, objectName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ObjectScope_MatchesAny_ReturnsTrueIfAnyPatternMatches()
    {
        var patterns = new[] { "sales.*", "dbo.Customers", "*.secrets" };

        Assert.True(ObjectScope.MatchesAny(patterns, "sales", "Invoices"));
        Assert.True(ObjectScope.MatchesAny(patterns, "dbo", "Customers"));
        Assert.True(ObjectScope.MatchesAny(patterns, "admin", "secrets"));
        Assert.False(ObjectScope.MatchesAny(patterns, "dbo", "Products"));
    }

    [Fact]
    public void SqlGate_ExtractsSelectTables_SqlServer()
    {
        var sql = "SELECT c.Id, o.Total FROM dbo.Customers c INNER JOIN dbo.Orders o ON c.Id = o.CustomerId;";
        var analysis = _gate.Analyze(sql, DatabaseEngine.SqlServer);

        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Select && o.ObjectName == "Customers");
        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Select && o.ObjectName == "Orders");
    }

    [Fact]
    public void SqlGate_ExtractsInsertUpdateDeleteTargets_SqlServer()
    {
        var sql = @"
            INSERT INTO dbo.Archive (Id) SELECT Id FROM dbo.OldOrders;
            UPDATE dbo.Inventory SET Qty = 10 WHERE ItemId = 1;
            DELETE FROM dbo.Logs WHERE CreatedAt < GETDATE();
        ";
        var analysis = _gate.Analyze(sql, DatabaseEngine.SqlServer);

        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Insert && o.ObjectName == "Archive");
        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Select && o.ObjectName == "OldOrders");
        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Update && o.ObjectName == "Inventory");
        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Delete && o.ObjectName == "Logs");
    }

    [Fact]
    public void SqlGate_IgnoresCtesAndTempTables_SqlServer()
    {
        var sql = @"
            WITH MyCte AS (SELECT 1 AS Val)
            SELECT Val INTO #TempResults FROM MyCte;
            SELECT * FROM #TempResults;
        ";
        var analysis = _gate.Analyze(sql, DatabaseEngine.SqlServer);

        // MyCte and #TempResults should not be listed as permanent object requirements
        Assert.DoesNotContain(analysis.ObjectRequirements, o => o.ObjectName == "MyCte");
        Assert.DoesNotContain(analysis.ObjectRequirements, o => o.ObjectName.StartsWith("#"));
    }

    [Fact]
    public void SqlGate_ExtractsExecuteProcedure_SqlServer()
    {
        var sql = "EXEC dbo.usp_GenerateReport @Year = 2026;";
        var analysis = _gate.Analyze(sql, DatabaseEngine.SqlServer);

        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Execute && o.ObjectName == "usp_GenerateReport");
    }

    [Fact]
    public void SqlGate_ExtractsObjects_PostgreSql()
    {
        var sql = "SELECT * FROM public.orders JOIN public.customers ON orders.customer_id = customers.id; UPDATE inventory.stock SET qty = 5 WHERE id = 1;";
        var analysis = _gate.Analyze(sql, DatabaseEngine.PostgreSql);

        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Select && o.ObjectName == "orders");
        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Select && o.ObjectName == "customers");
        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Update && o.ObjectName == "stock");
    }

    [Fact]
    public void SqlGate_ExtractsObjects_PostgreSql_WithCommentsAndStrings()
    {
        var sql = "SELECT 'FROM fake_table' AS col FROM /* comment */ public.secret_records WHERE id = 1;";
        var analysis = _gate.Analyze(sql, DatabaseEngine.PostgreSql);

        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Select && o.ObjectName == "secret_records");
        Assert.DoesNotContain(analysis.ObjectRequirements, o => o.ObjectName == "fake_table");
    }

    [Fact]
    public void SqlGate_ExtractsObjects_MySql()
    {
        var sql = "SELECT * FROM `orders` WHERE `total` > 100; DELETE FROM `temp_tokens` WHERE `expired` = 1;";
        var analysis = _gate.Analyze(sql, DatabaseEngine.MySql);

        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Select && o.ObjectName == "orders");
        Assert.Contains(analysis.ObjectRequirements, o => o.Permission == Perm.Delete && o.ObjectName == "temp_tokens");
    }

    [Fact]
    public void ObjectScope_HasConflict_AllowsAllObjectsWithSpecificExclusions()
    {
        // User includes all objects (*) and chooses to exclude specific objects
        var included = "*";
        var excluded = "dbo.Products, dbo.Salaries";

        var conflict = ObjectScope.HasConflict(included, excluded, out var conflictingObj);

        Assert.False(conflict);
        Assert.Equal("", conflictingObj);
    }

    [Fact]
    public void ObjectScope_HasConflict_DetectsDirectObjectConflict()
    {
        var included = "dbo.Products, dbo.Orders";
        var excluded = "dbo.Products";

        var conflict = ObjectScope.HasConflict(included, excluded, out var conflictingObj);

        Assert.True(conflict);
        Assert.Equal("dbo.Products", conflictingObj);
    }

    [Fact]
    public void ObjectScope_HasConflict_DetectsUnqualifiedObjectConflict()
    {
        var included = "Products";
        var excluded = "dbo.Products";

        var conflict = ObjectScope.HasConflict(included, excluded, out var conflictingObj);

        Assert.True(conflict);
        Assert.Equal("dbo.Products", conflictingObj);
    }

    [Fact]
    public void ObjectScope_HasConflict_DetectsSchemaWildcardConflict()
    {
        var included = "sales.*, hr.*";
        var excluded = "sales.*";

        var conflict = ObjectScope.HasConflict(included, excluded, out var conflictingObj);

        Assert.True(conflict);
        Assert.Equal("sales.*", conflictingObj);
    }

    [Fact]
    public void ObjectScope_HasConflict_AllowsSchemaInclusionWithSpecificTableExclusion()
    {
        var included = "sales.*";
        var excluded = "sales.InternalNotes";

        var conflict = ObjectScope.HasConflict(included, excluded, out var conflictingObj);

        Assert.False(conflict);
        Assert.Equal("", conflictingObj);
    }

    [Fact]
    public void ObjectScope_ParseList_WithDefaultToAllFalse_ReturnsEmptyForEmptyInput()
    {
        var empty = ObjectScope.ParseList("", defaultToAll: false);
        var nullInput = ObjectScope.ParseList((string?)null, defaultToAll: false);

        Assert.Empty(empty);
        Assert.Empty(nullInput);
    }
}
