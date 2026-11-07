namespace DbAccessManager.Services;

public static class Csv
{
    // Quotes the value and neutralises spreadsheet formula injection.
    public static string Cell(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
