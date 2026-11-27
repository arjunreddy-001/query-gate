using DbAccessManager.Services;

namespace DbAccessManager.Components.Pages.QueryWorkspace;

public sealed record IndexedResultRow(int RowNumber, object?[] Values);

public static class QueryResultProcessor
{
    public static List<IndexedResultRow> ProcessRows(
        ResultSetDto rs,
        string? gridFilter,
        int? sortCol,
        bool sortAsc)
    {
        IEnumerable<object?[]> rows = rs.Rows;
        if (!string.IsNullOrWhiteSpace(gridFilter))
        {
            var f = gridFilter.Trim();
            rows = rows.Where(row => row.Any(cell => cell is not null &&
                Convert.ToString(cell, System.Globalization.CultureInfo.InvariantCulture)?.Contains(f, StringComparison.OrdinalIgnoreCase) == true));
        }

        if (sortCol.HasValue && sortCol.Value < rs.Columns.Count)
        {
            var idx = sortCol.Value;
            rows = sortAsc
                ? rows.OrderBy(r => r[idx], CellComparer.Instance)
                : rows.OrderByDescending(r => r[idx], CellComparer.Instance);
        }

        var result = new List<IndexedResultRow>();
        var rowNum = 1;
        foreach (var r in rows)
        {
            result.Add(new IndexedResultRow(rowNum++, r));
        }
        return result;
    }

    private sealed class CellComparer : IComparer<object?>
    {
        public static readonly CellComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return -1;
            if (y is null) return 1;

            if (x is IComparable cx && y is IComparable cy && x.GetType() == y.GetType())
            {
                return cx.CompareTo(cy);
            }

            var sx = Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture);
            var sy = Convert.ToString(y, System.Globalization.CultureInfo.InvariantCulture);

            if (double.TryParse(sx, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dx) &&
                double.TryParse(sy, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dy))
            {
                return dx.CompareTo(dy);
            }

            return string.Compare(sx, sy, StringComparison.OrdinalIgnoreCase);
        }
    }
}
