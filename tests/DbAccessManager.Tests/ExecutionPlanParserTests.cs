using DbAccessManager.Services;
using Xunit;

namespace DbAccessManager.Tests;

public class ExecutionPlanParserTests
{
    private const string SampleShowPlanXml = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6" Build="16.0.1000.6">
          <BatchSequence>
            <Batch>
              <Statements>
                <StmtSimple StatementText="SELECT OrderDate, TotalAmount FROM Orders WHERE CustomerId = 42" StatementSubTreeCost="0.0456" StatementEstRows="25" StatementOptmLevel="FULL">
                  <QueryPlan CompileTime="14">
                    <MissingIndexes>
                      <MissingIndexGroup Impact="85.4">
                        <MissingIndex Schema="[dbo]" Table="[Orders]">
                          <ColumnGroup Usage="EQUALITY">
                            <Column Name="[CustomerId]" />
                          </ColumnGroup>
                          <ColumnGroup Usage="INCLUDE">
                            <Column Name="[OrderDate]" />
                            <Column Name="[TotalAmount]" />
                          </ColumnGroup>
                        </MissingIndex>
                      </MissingIndexGroup>
                    </MissingIndexes>
                    <RelOp NodeId="0" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="25" EstimatedTotalSubtreeCost="0.0456">
                      <OutputList />
                      <IndexScan>
                        <Object Table="[Orders]" Index="[PK_Orders]" />
                      </IndexScan>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements>
            </Batch>
          </BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void Parse_EmptyOrInvalidXml_Returns_EmptyPlan()
    {
        var plan = ExecutionPlanParser.Parse("");
        Assert.Equal(0, plan.TotalCost);
        Assert.Empty(plan.MissingIndexes);
        Assert.Empty(plan.Operators);

        var planInvalid = ExecutionPlanParser.Parse("not-an-xml");
        Assert.Equal(0, planInvalid.TotalCost);
        Assert.Empty(planInvalid.MissingIndexes);
    }

    [Fact]
    public void Parse_ValidShowplanXml_Extracts_Cost_Rows_And_OptimizationLevel()
    {
        var plan = ExecutionPlanParser.Parse(SampleShowPlanXml);

        Assert.Equal(0.0456, plan.TotalCost);
        Assert.Equal("FULL", plan.OptimizationLevel);
        Assert.Equal(25, plan.EstimatedRows);
        Assert.Equal(14, plan.CompileTimeMs);
    }

    [Fact]
    public void Parse_Extracts_MissingIndexes_And_Generates_CreateIndex_DDL()
    {
        var plan = ExecutionPlanParser.Parse(SampleShowPlanXml);

        Assert.Single(plan.MissingIndexes);
        var mi = plan.MissingIndexes[0];
        Assert.Equal(85.4, mi.Impact);
        Assert.Equal("[dbo].[Orders]", mi.Table);
        Assert.Contains("[CustomerId]", mi.EqualityColumns);
        Assert.Contains("[OrderDate]", mi.IncludedColumns);
        Assert.Contains("[TotalAmount]", mi.IncludedColumns);

        // Check generated DDL
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Orders_CustomerId] ON [dbo].[Orders] ([CustomerId])", mi.CreateIndexStatement);
        Assert.Contains("INCLUDE ([OrderDate], [TotalAmount]);", mi.CreateIndexStatement);
    }

    [Fact]
    public void Parse_Extracts_RelOp_Operators_With_Cost_Percentages_And_Scan_Warnings()
    {
        var plan = ExecutionPlanParser.Parse(SampleShowPlanXml);

        Assert.Single(plan.Operators);
        var op = plan.Operators[0];
        Assert.Equal(0, op.NodeId);
        Assert.Equal("Clustered Index Scan", op.PhysicalOp);
        Assert.Equal(0.0456, op.SubtreeCost);
        Assert.Equal(100, op.CostPercentage);
        Assert.Equal(25, op.EstimatedRows);
        Assert.Equal("[Orders].[PK_Orders]", op.TargetObject);
        Assert.True(op.IsScan);
        Assert.True(op.IsWarning);
    }
}
