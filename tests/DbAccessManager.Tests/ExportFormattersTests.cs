using System.Text.Json;
using DbAccessManager.Services;
using Xunit;

namespace DbAccessManager.Tests;

public class ExportFormattersTests
{
    private readonly string[] _columns = ["Id", "Name", "Score", "Notes"];
    private readonly List<object?[]> _rows =
    [
        [1, "Alice", 95.5, "Regular user"],
        [2, "Bob", 88.0, "=SUM(A1:A2)"],
        [3, "Charlie", 72.3, null]
    ];

    [Fact]
    public void ToCsv_Quotes_And_Neutralizes_Formula_Injection()
    {
        var csv = ExportFormatters.ToCsv(_columns, _rows);
        Assert.Contains("\"Id\",\"Name\",\"Score\",\"Notes\"", csv);
        Assert.Contains("\"1\",\"Alice\",\"95.5\",\"Regular user\"", csv);
        // Formula injection prefix '=' should be prefixed with single quote "'"
        Assert.Contains("\"'=SUM(A1:A2)\"", csv);
        Assert.Contains("\"3\",\"Charlie\",\"72.3\",\"\"", csv);
    }

    [Fact]
    public void ToExcelXml_Generates_Valid_SpreadsheetML_With_Typed_Cells()
    {
        var xml = ExportFormatters.ToExcelXml(_columns, _rows, "TestSheet");
        Assert.Contains("xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"", xml);
        Assert.Contains("<Worksheet ss:Name=\"TestSheet\">", xml);
        // Headers are strings
        Assert.Contains("<Data ss:Type=\"String\">Name</Data>", xml);
        // Numbers are typed as Number
        Assert.Contains("<Data ss:Type=\"Number\">1</Data>", xml);
        Assert.Contains("<Data ss:Type=\"Number\">95.5</Data>", xml);
        // Null cell renders empty Cell
        Assert.Contains("<Cell/>", xml);
    }

    [Fact]
    public void ToJson_Generates_Valid_Json_Array_Of_Objects()
    {
        var json = ExportFormatters.ToJson(_columns, _rows);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(3, doc.RootElement.GetArrayLength());

        var first = doc.RootElement[0];
        Assert.Equal(1, first.GetProperty("Id").GetInt32());
        Assert.Equal("Alice", first.GetProperty("Name").GetString());
        Assert.Equal(95.5, first.GetProperty("Score").GetDouble());

        var third = doc.RootElement[2];
        Assert.Equal(JsonValueKind.Null, third.GetProperty("Notes").ValueKind);
    }

    [Fact]
    public void ToMarkdown_Generates_Valid_Markdown_Table()
    {
        var md = ExportFormatters.ToMarkdown(_columns, _rows);
        var lines = md.Trim().Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Equal("| Id | Name | Score | Notes |", lines[0]);
        Assert.Equal("| --- | --- | --- | --- |", lines[1]);
        Assert.Contains("| 1 | Alice | 95.5 | Regular user |", lines[2]);
        Assert.Contains("NULL", lines[4]);
    }

    [Fact]
    public void ToSqlInsert_Generates_Correct_Insert_Statements()
    {
        var sql = ExportFormatters.ToSqlInsert(_columns, _rows, "Users");
        var lines = sql.Trim().Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Equal(3, lines.Length);
        Assert.Equal("INSERT INTO [Users] ([Id], [Name], [Score], [Notes]) VALUES (1, 'Alice', 95.5, 'Regular user');", lines[0]);
        Assert.Contains("VALUES (3, 'Charlie', 72.3, NULL);", lines[2]);
    }

    [Fact]
    public void ToSqlInsert_With_Duplicate_Check_Generates_IfNotExists_Block()
    {
        var cols = new[] { "productname", "category", "createddate" };
        var rows = new List<object?[]>
        {
            new object?[] { "pname", "catname", "2026-10-03" }
        };

        var sql = ExportFormatters.ToSqlInsert(cols, rows, "Products", ["productname", "category"]);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM [Products] WHERE [productname] = 'pname' AND [category] = 'catname')", sql);
        Assert.Contains("BEGIN", sql);
        Assert.Contains("INSERT INTO [Products] ([productname], [category], [createddate]) VALUES ('pname', 'catname', '2026-10-03');", sql);
        Assert.Contains("END", sql);
    }

    [Theory]
    [InlineData("SELECT * FROM Products", "Products")]
    [InlineData("SELECT Id, Name FROM [dbo].[Orders] WHERE Active = 1", "Orders")]
    [InlineData("SELECT p.Name, c.Title FROM Products p JOIN Categories c ON p.CatId = c.Id", "Products")]
    [InlineData("SELECT 1, 2", "FallbackTable")]
    public void InferTableName_Extracts_Primary_Table(string query, string expected)
    {
        var table = ExportFormatters.InferTableName(query, "FallbackTable");
        Assert.Equal(expected, table);
    }
}
