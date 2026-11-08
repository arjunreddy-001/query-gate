using System.IO.Compression;
using System.Text;
using DbAccessManager.Services;
using Xunit;

namespace DbAccessManager.Tests;

public class ExcelExportTests
{
    [Fact]
    public void ToXlsx_ProducesValidZipWithRequiredSpreadsheetParts()
    {
        var cols = new[] { "Id", "Name", "Score", "IsActive", "Created" };
        var rows = new List<object?[]>
        {
            new object?[] { 1, "Alice & Co <Demo>", 98.5, true, new DateTime(2026, 11, 8, 12, 0, 0) },
            new object?[] { 2, null, -14.2, false, null }
        };

        var bytes = ExportFormatters.ToXlsx(cols, rows, "Test Sheet");
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        var contentTypes = zip.GetEntry("[Content_Types].xml");
        Assert.NotNull(contentTypes);

        var workbook = zip.GetEntry("xl/workbook.xml");
        Assert.NotNull(workbook);

        var worksheet = zip.GetEntry("xl/worksheets/sheet1.xml");
        Assert.NotNull(worksheet);

        using var wsReader = new StreamReader(worksheet.Open(), Encoding.UTF8);
        var wsXml = wsReader.ReadToEnd();

        Assert.Contains("Alice &amp; Co &lt;Demo&gt;", wsXml);
        Assert.Contains("<v>98.5</v>", wsXml);
        Assert.Contains("<v>1</v>", wsXml);
    }

    [Fact]
    public void ToXlsb_ProducesValidZipWithBinaryRecords()
    {
        var cols = new[] { "Code", "Amount", "Flag" };
        var rows = new List<object?[]>
        {
            new object?[] { "TEST-01", 1234.56, true },
            new object?[] { "TEST-02", null, false }
        };

        var bytes = ExportFormatters.ToXlsb(cols, rows, "BinarySheet");
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);

        using var ms = new MemoryStream(bytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("xl/workbook.bin"));
        Assert.NotNull(zip.GetEntry("xl/styles.bin"));
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.bin"));
    }
}
