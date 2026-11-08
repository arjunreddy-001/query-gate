using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace DbAccessManager.Services;

public sealed class ExecutionPlanDto
{
    public double TotalCost { get; set; }
    public string OptimizationLevel { get; set; } = "FULL";
    public double EstimatedRows { get; set; }
    public int CompileTimeMs { get; set; }
    public List<PlanMissingIndexDto> MissingIndexes { get; } = [];
    public List<PlanOperatorDto> Operators { get; } = [];
    public string RawXml { get; set; } = "";
}

public sealed class PlanMissingIndexDto
{
    public double Impact { get; set; }
    public string Table { get; set; } = "";
    public List<string> EqualityColumns { get; } = [];
    public List<string> InequalityColumns { get; } = [];
    public List<string> IncludedColumns { get; } = [];
    public string CreateIndexStatement { get; set; } = "";
}

public sealed class PlanOperatorDto
{
    public int NodeId { get; set; }
    public string PhysicalOp { get; set; } = "";
    public string LogicalOp { get; set; } = "";
    public double SubtreeCost { get; set; }
    public double OperatorCost { get; set; }
    public double CostPercentage { get; set; }
    public double EstimatedRows { get; set; }
    public string? TargetObject { get; set; }
    public bool IsScan { get; set; }
    public bool IsWarning { get; set; }
    public string? WarningMessage { get; set; }
}

public static class ExecutionPlanParser
{
    public static ExecutionPlanDto Parse(string xml)
    {
        var dto = new ExecutionPlanDto { RawXml = xml };
        if (string.IsNullOrWhiteSpace(xml)) return dto;

        try
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root is null) return dto;

            // Find StmtSimple
            var stmt = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "StmtSimple");
            if (stmt is not null)
            {
                var costAttr = stmt.Attribute("StatementSubTreeCost")?.Value;
                if (double.TryParse(costAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var totalCost))
                {
                    dto.TotalCost = totalCost;
                }

                dto.OptimizationLevel = stmt.Attribute("StatementOptmLevel")?.Value ?? "FULL";

                var rowsAttr = stmt.Attribute("StatementEstRows")?.Value;
                if (double.TryParse(rowsAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var estRows))
                {
                    dto.EstimatedRows = estRows;
                }
            }

            var queryPlan = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "QueryPlan");
            if (queryPlan is not null)
            {
                var compileTimeAttr = queryPlan.Attribute("CompileTime")?.Value;
                if (int.TryParse(compileTimeAttr, out var ct)) dto.CompileTimeMs = ct;

                ParseMissingIndexes(queryPlan, dto);
                ParseRelOps(queryPlan, dto);
            }
        }
        catch
        {
            // Gracefully handle partial/non-standard XML
        }

        return dto;
    }

    private static void ParseMissingIndexes(XElement queryPlan, ExecutionPlanDto dto)
    {
        var missingIndexGroups = queryPlan.Descendants().Where(e => e.Name.LocalName == "MissingIndexGroup");
        foreach (var group in missingIndexGroups)
        {
            var impactAttr = group.Attribute("Impact")?.Value;
            var impact = double.TryParse(impactAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var imp) ? imp : 0;

            var missingIndex = group.Descendants().FirstOrDefault(e => e.Name.LocalName == "MissingIndex");
            if (missingIndex is null) continue;

            var schema = missingIndex.Attribute("Schema")?.Value ?? "";
            var table = missingIndex.Attribute("Table")?.Value ?? "";
            var fullTable = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";

            var mi = new PlanMissingIndexDto
            {
                Impact = Math.Round(impact, 1),
                Table = fullTable
            };

            foreach (var colGroup in missingIndex.Descendants().Where(e => e.Name.LocalName == "ColumnGroup"))
            {
                var usage = colGroup.Attribute("Usage")?.Value ?? "";
                var cols = colGroup.Descendants()
                    .Where(e => e.Name.LocalName == "Column")
                    .Select(c => c.Attribute("Name")?.Value ?? "")
                    .Where(n => !string.IsNullOrEmpty(n));

                if (usage.Equals("EQUALITY", StringComparison.OrdinalIgnoreCase))
                    mi.EqualityColumns.AddRange(cols);
                else if (usage.Equals("INEQUALITY", StringComparison.OrdinalIgnoreCase))
                    mi.InequalityColumns.AddRange(cols);
                else if (usage.Equals("INCLUDE", StringComparison.OrdinalIgnoreCase))
                    mi.IncludedColumns.AddRange(cols);
            }

            var keyCols = mi.EqualityColumns.Concat(mi.InequalityColumns).ToList();
            var firstColClean = (keyCols.FirstOrDefault() ?? "col").Trim('[', ']').Replace(" ", "_");
            var tableClean = table.Trim('[', ']').Replace(" ", "_");
            var indexName = $"IX_{tableClean}_{firstColClean}";

            var ddl = new StringBuilder();
            ddl.Append($"CREATE NONCLUSTERED INDEX [{indexName}] ON {fullTable} ({string.Join(", ", keyCols)})");
            if (mi.IncludedColumns.Count > 0)
            {
                ddl.Append($" INCLUDE ({string.Join(", ", mi.IncludedColumns)})");
            }
            ddl.Append(';');
            mi.CreateIndexStatement = ddl.ToString();

            dto.MissingIndexes.Add(mi);
        }
    }

    private static void ParseRelOps(XElement queryPlan, ExecutionPlanDto dto)
    {
        var relOps = queryPlan.Descendants().Where(e => e.Name.LocalName == "RelOp").ToList();

        foreach (var op in relOps)
        {
            var nodeIdAttr = op.Attribute("NodeId")?.Value;
            var nodeId = int.TryParse(nodeIdAttr, out var nid) ? nid : 0;

            var physicalOp = op.Attribute("PhysicalOp")?.Value ?? "";
            var logicalOp = op.Attribute("LogicalOp")?.Value ?? "";

            var estRowsAttr = op.Attribute("EstimateRows")?.Value;
            var estRows = double.TryParse(estRowsAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var er) ? er : 0;

            var subtreeCostAttr = op.Attribute("EstimatedTotalSubtreeCost")?.Value;
            var subtreeCost = double.TryParse(subtreeCostAttr, NumberStyles.Any, CultureInfo.InvariantCulture, out var sc) ? sc : 0;

            // Direct child RelOps:
            var directChildren = op.Elements()
                .SelectMany(e => e.DescendantsAndSelf().Where(d => d.Name.LocalName == "RelOp" && d != op && !d.Ancestors().TakeWhile(a => a != op).Any(a => a.Name.LocalName == "RelOp")));

            var childCostSum = directChildren.Sum(c =>
                double.TryParse(c.Attribute("EstimatedTotalSubtreeCost")?.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var cCost) ? cCost : 0);

            var operatorCost = Math.Max(0, subtreeCost - childCostSum);
            if (operatorCost == 0 && relOps.Count == 1) operatorCost = subtreeCost;

            var costPct = dto.TotalCost > 0 ? (operatorCost / dto.TotalCost) * 100 : 0;

            var objElem = op.Descendants().FirstOrDefault(e => e.Name.LocalName == "Object");
            string? targetObj = null;
            if (objElem is not null)
            {
                var t = objElem.Attribute("Table")?.Value;
                var idx = objElem.Attribute("Index")?.Value;
                if (!string.IsNullOrEmpty(t))
                {
                    targetObj = string.IsNullOrEmpty(idx) ? t : $"{t}.{idx}";
                }
            }

            var isScan = physicalOp.Contains("Scan", StringComparison.OrdinalIgnoreCase);
            var warnings = op.Descendants().FirstOrDefault(e => e.Name.LocalName == "Warnings");

            dto.Operators.Add(new PlanOperatorDto
            {
                NodeId = nodeId,
                PhysicalOp = physicalOp,
                LogicalOp = logicalOp,
                SubtreeCost = subtreeCost,
                OperatorCost = operatorCost,
                CostPercentage = Math.Min(100, Math.Round(costPct, 1)),
                EstimatedRows = Math.Round(estRows, 0),
                TargetObject = targetObj,
                IsScan = isScan,
                IsWarning = warnings is not null || isScan,
                WarningMessage = warnings is not null ? "Operator produced execution warning" : (isScan ? "Full table or index scan" : null)
            });
        }

        dto.Operators.Sort((a, b) => b.OperatorCost.CompareTo(a.OperatorCost));
    }
}
