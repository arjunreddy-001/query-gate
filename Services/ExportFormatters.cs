using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;

namespace DbAccessManager.Services;

public static class ExportFormatters
{
    public static string ToCsv(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(Csv.Cell)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(v => Csv.Cell(FormatValue(v)))));
        }
        return sb.ToString();
    }

    public static string ToExcelXml(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, string sheetName = "Query Results")
    {
        var cleanSheet = SecurityElement.Escape(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName);
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\"?>");
        sb.AppendLine("<?mso-application progid=\"Excel.Sheet\"?>");
        sb.AppendLine("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\"");
        sb.AppendLine(" xmlns:o=\"urn:schemas-microsoft-com:office:office\"");
        sb.AppendLine(" xmlns:x=\"urn:schemas-microsoft-com:office:excel\"");
        sb.AppendLine(" xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\"");
        sb.AppendLine(" xmlns:html=\"http://www.w3.org/TR/REC-html40\">");
        sb.AppendLine(" <Styles>");
        sb.AppendLine("  <Style ss:ID=\"Default\" ss:Name=\"Normal\"><Font ss:FontName=\"Segoe UI\" ss:Size=\"10\"/></Style>");
        sb.AppendLine("  <Style ss:ID=\"Header\"><Font ss:FontName=\"Segoe UI\" ss:Size=\"10\" ss:Bold=\"1\"/><Interior ss:Color=\"#EBF2FA\" ss:Pattern=\"Solid\"/></Style>");
        sb.AppendLine(" </Styles>");
        sb.AppendLine($" <Worksheet ss:Name=\"{cleanSheet}\">");
        sb.AppendLine("  <Table>");

        // Header Row
        sb.AppendLine("   <Row ss:StyleID=\"Header\">");
        foreach (var col in columns)
        {
            var escaped = SecurityElement.Escape(col ?? "");
            sb.AppendLine($"    <Cell><Data ss:Type=\"String\">{escaped}</Data></Cell>");
        }
        sb.AppendLine("   </Row>");

        // Data Rows
        foreach (var row in rows)
        {
            sb.AppendLine("   <Row>");
            foreach (var val in row)
            {
                if (val is null or DBNull)
                {
                    sb.AppendLine("    <Cell/>");
                }
                else if (IsNumeric(val))
                {
                    var numStr = Convert.ToString(val, CultureInfo.InvariantCulture);
                    sb.AppendLine($"    <Cell><Data ss:Type=\"Number\">{numStr}</Data></Cell>");
                }
                else if (val is DateTime dt)
                {
                    sb.AppendLine($"    <Cell><Data ss:Type=\"String\">{dt:yyyy-MM-dd HH:mm:ss}</Data></Cell>");
                }
                else if (val is DateTimeOffset dto)
                {
                    sb.AppendLine($"    <Cell><Data ss:Type=\"String\">{dto:yyyy-MM-dd HH:mm:ss zzz}</Data></Cell>");
                }
                else
                {
                    var str = FormatValue(val);
                    var escaped = SecurityElement.Escape(str);
                    sb.AppendLine($"    <Cell><Data ss:Type=\"String\">{escaped}</Data></Cell>");
                }
            }
            sb.AppendLine("   </Row>");
        }

        sb.AppendLine("  </Table>");
        sb.AppendLine(" </Worksheet>");
        sb.AppendLine("</Workbook>");
        return sb.ToString();
    }

    public static string ToJson(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var list = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>(columns.Count);
            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                dict[col] = (i < row.Length && row[i] is not DBNull) ? row[i] : null;
            }
            list.Add(dict);
        }
        return JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string ToMarkdown(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", columns.Select(c => (c ?? "").Replace("|", "\\|"))) + " |");
        sb.AppendLine("| " + string.Join(" | ", columns.Select(_ => "---")) + " |");
        foreach (var row in rows)
        {
            var cells = row.Select(v =>
            {
                if (v is null or DBNull) return "NULL";
                var s = FormatValue(v);
                return s.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
            });
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
        return sb.ToString();
    }

    public static string ToSqlInsert(
        IReadOnlyList<string> columns,
        IReadOnlyList<object?[]> rows,
        string tableName = "Products",
        IReadOnlyCollection<string>? duplicateCheckColumns = null)
    {
        var cleanTable = FormatTableName(string.IsNullOrWhiteSpace(tableName) ? "Products" : tableName);
        var cols = string.Join(", ", columns.Select(c => $"[{c.Replace("]", "]]")}]"));
        var sb = new StringBuilder();

        var checkCols = (duplicateCheckColumns ?? Array.Empty<string>())
            .Where(c => columns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++) colMap[columns[i]] = i;

        foreach (var row in rows)
        {
            var vals = string.Join(", ", row.Select(FormatSqlLiteral));
            var insertStmt = $"INSERT INTO {cleanTable} ({cols}) VALUES ({vals});";

            if (checkCols.Count > 0)
            {
                var conditions = checkCols.Select(c =>
                {
                    var colIdx = colMap[c];
                    var val = colIdx < row.Length ? row[colIdx] : null;
                    if (val is null or DBNull)
                    {
                        return $"[{c.Replace("]", "]]")}] IS NULL";
                    }
                    return $"[{c.Replace("]", "]]")}] = {FormatSqlLiteral(val)}";
                });

                var whereClause = string.Join(" AND ", conditions);
                sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM {cleanTable} WHERE {whereClause})");
                sb.AppendLine("BEGIN");
                sb.AppendLine($"    {insertStmt}");
                sb.AppendLine("END");
            }
            else
            {
                sb.AppendLine(insertStmt);
            }
        }
        return sb.ToString();
    }

    public static string InferTableName(string? sql, string fallback = "Products")
    {
        if (string.IsNullOrWhiteSpace(sql)) return fallback;
        try
        {
            var parser = new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true);
            using var reader = new StringReader(sql);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count == 0 && fragment is not null)
            {
                var visitor = new PrimaryTableVisitor();
                fragment.Accept(visitor);
                if (!string.IsNullOrWhiteSpace(visitor.TableName)) return visitor.TableName;
            }
        }
        catch { /* fallback to regex on parse exception */ }

        var match = System.Text.RegularExpressions.Regex.Match(
            sql,
            @"\bFROM\s+(?:\[?[\w]+\]?\.)?\[?([a-zA-Z_][\w]*)\]?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            return match.Groups[1].Value;
        }

        return fallback;
    }

    private sealed class PrimaryTableVisitor : Microsoft.SqlServer.TransactSql.ScriptDom.TSqlFragmentVisitor
    {
        public string? TableName { get; private set; }

        public override void ExplicitVisit(Microsoft.SqlServer.TransactSql.ScriptDom.NamedTableReference node)
        {
            if (TableName is null && node.SchemaObject is not null)
            {
                TableName = node.SchemaObject.BaseIdentifier?.Value;
            }
            base.ExplicitVisit(node);
        }
    }

    private static string FormatTableName(string tableName)
    {
        var raw = tableName.Trim();
        if (raw.StartsWith('[') && raw.EndsWith(']')) return raw;
        if (raw.Contains('.'))
        {
            var parts = raw.Split('.');
            return string.Join(".", parts.Select(p => $"[{p.Trim('[', ']').Replace("]", "]]")}]"));
        }
        return $"[{raw.Replace("]", "]]")}]";
    }

    private static string FormatValue(object? value) =>
        value switch
        {
            null or DBNull => "",
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };

    private static string FormatSqlLiteral(object? value)
    {
        if (value is null or DBNull) return "NULL";
        if (value is bool b) return b ? "1" : "0";
        if (IsNumeric(value)) return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
        if (value is DateTime dt) return $"'{dt:yyyy-MM-dd HH:mm:ss}'";
        if (value is DateTimeOffset dto) return $"'{dto:yyyy-MM-dd HH:mm:ss zzz}'";
        var str = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return $"'{str.Replace("'", "''")}'";
    }

    private static bool IsNumeric(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
