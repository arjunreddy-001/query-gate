using DbAccessManager.Data;
using DbAccessManager.Security;
using DbAccessManager.Services;
using DbAccessManager.Services.Ai;
using Xunit;

namespace DbAccessManager.Tests;

public class ErDiagramAndAiTests
{
    [Fact]
    public void OpenAiCompatible_CleanSqlOutput_Removes_Markdown_Code_Fences()
    {
        var raw1 = "```sql\nSELECT * FROM Users;\n```";
        Assert.Equal("SELECT * FROM Users;", OpenAiCompatibleAiClient.CleanSqlOutput(raw1));

        var raw2 = "```\nSELECT Id, Name FROM Customers\n```";
        Assert.Equal("SELECT Id, Name FROM Customers", OpenAiCompatibleAiClient.CleanSqlOutput(raw2));

        var raw3 = "SELECT 1;";
        Assert.Equal("SELECT 1;", OpenAiCompatibleAiClient.CleanSqlOutput(raw3));
    }

    [Fact]
    public void SqlGate_Validates_AI_Generated_Sql_Safeguards()
    {
        var gate = new SqlGate();

        // Safe query
        var safe = gate.Analyze("SELECT Id, Email FROM Users WHERE IsActive = 1", DatabaseEngine.SqlServer);
        Assert.True(safe.Parsed);
        Assert.Empty(safe.UnfilteredWrites);
        Assert.DoesNotContain("DROP", safe.StatementTypes);

        // Dangerous unfiltered UPDATE
        var unsafeUpdate = gate.Analyze("UPDATE Users SET IsActive = 0", DatabaseEngine.SqlServer);
        Assert.True(unsafeUpdate.Parsed);
        Assert.NotEmpty(unsafeUpdate.UnfilteredWrites);

        // Destructive DDL
        var ddl = gate.Analyze("DROP TABLE Orders", DatabaseEngine.SqlServer);
        Assert.True(ddl.Parsed);
        Assert.Contains(ddl.StatementTypes, t => t.Contains("DROP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ErDiagramData_Contract_Initializes_Correctly()
    {
        var nodes = new List<ErTableNode>
        {
            new("dbo", "Orders", "dbo.Orders", new List<ErColumnDto>
            {
                new("Id", "int", true, false, false),
                new("CustomerId", "int", false, true, false)
            }),
            new("dbo", "Customers", "dbo.Customers", new List<ErColumnDto>
            {
                new("Id", "int", true, false, false),
                new("Name", "nvarchar(100)", false, false, false)
            })
        };

        var edges = new List<ErRelationshipEdge>
        {
            new("dbo.Orders", "CustomerId", "dbo.Customers", "Id", "FK_Orders_Customers")
        };

        var diagram = new ErDiagramData(nodes, edges, "dbo.Orders", 1, false);

        Assert.Equal(2, diagram.Nodes.Count);
        Assert.Single(diagram.Edges);
        Assert.Equal("dbo.Orders", diagram.FocusTable);
        Assert.Equal(1, diagram.Hops);
        Assert.False(diagram.IsFullSchema);

        var ordersNode = diagram.Nodes.First(n => n.Name == "Orders");
        Assert.True(ordersNode.Columns.First(c => c.Name == "Id").IsPrimaryKey);
        Assert.True(ordersNode.Columns.First(c => c.Name == "CustomerId").IsForeignKey);
    }
}
