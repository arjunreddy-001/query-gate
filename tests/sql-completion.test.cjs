const test = require("node:test");
const assert = require("node:assert/strict");
const { analyze } = require("../wwwroot/js/sql-completion.js");

const metadata = [
  { schema: "dbo", name: "Orders", type: "Table", columns: ["Id", "CustomerId", "Status"] },
  { schema: "dbo", name: "Customers", type: "Table", columns: ["Id", "Name"] },
  { schema: "sales", name: "Order View", type: "View", columns: ["Id", "Total"] },
  { schema: "dbo", name: "DoWork", type: "Procedure", columns: [] },
  { schema: "dbo", name: "OrderItems", type: "Function", isTableValuedFunction: true, columns: ["OrderId", "Quantity"] },
];
function complete(input) {
  const offset = input.indexOf("|");
  return analyze(input.replace("|", ""), offset, metadata);
}

test("WHERE suggests scoped columns rather than unrelated objects", () => {
  const result = complete("SELECT * FROM dbo.Orders o WHERE |");
  assert.equal(result.context, "expression");
  assert.deepEqual(result.suggestions.map(x => x.label), ["Id", "CustomerId", "Status"]);
});
test("a qualified alias suggests only that source", () => {
  const result = complete("SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON o.|");
  assert.deepEqual(result.suggestions.map(x => x.label), ["Id", "CustomerId", "Status"]);
  assert.equal(result.suggestions[0].insertText, "Id");
});
test("ambiguous columns are qualified", () => {
  const result = complete("SELECT | FROM dbo.Orders o JOIN dbo.Customers c ON o.CustomerId = c.Id");
  assert.deepEqual(result.suggestions.filter(x => x.label === "Id").map(x => x.insertText), ["o.Id", "c.Id"]);
  assert.equal(result.suggestions.find(x => x.label === "Status").insertText, "Status");
});
test("FROM offers only usable sources and safely quotes identifiers", () => {
  const result = complete("SELECT * FROM |");
  assert.equal(result.context, "source");
  assert.equal(result.suggestions.find(x => x.label === "sales.Order View").insertText, "sales.[Order View]");
  assert.ok(!result.suggestions.some(x => x.label.includes("DoWork")));
});
test("subqueries see outer aliases without leaking inner aliases outwards", () => {
  const inner = complete("SELECT * FROM dbo.Orders o WHERE EXISTS (SELECT 1 FROM dbo.Customers c WHERE o.|)");
  assert.deepEqual(inner.suggestions.map(x => x.label), ["Id", "CustomerId", "Status"]);
  const outer = complete("SELECT * FROM dbo.Orders o WHERE EXISTS (SELECT 1 FROM dbo.Customers c WHERE c.Id = o.CustomerId) AND c.|");
  assert.deepEqual(outer.suggestions, []);
});
test("comments and strings suppress suggestions", () => {
  assert.equal(complete("SELECT * FROM dbo.Orders -- |").context, "none");
  assert.equal(complete("SELECT 'hello |' FROM dbo.Orders").context, "none");
});
test("separate statements do not share aliases", () => {
  const result = complete("SELECT * FROM dbo.Orders o; SELECT * FROM dbo.Customers c WHERE |");
  assert.deepEqual(result.suggestions.map(x => x.label), ["Id", "Name"]);
});
test("derived table and CTE output columns are available", () => {
  const derived = complete("SELECT x.| FROM (SELECT o.Id, o.Status AS State FROM dbo.Orders o) AS x");
  assert.deepEqual(derived.suggestions.map(x => x.label), ["Id", "State"]);
  const cte = complete("WITH recent AS (SELECT o.Id, o.Status AS State FROM dbo.Orders o) SELECT r.| FROM recent r");
  assert.deepEqual(cte.suggestions.map(x => x.label), ["Id", "State"]);
});
test("table-valued function columns are available after APPLY", () => {
  const result = complete("SELECT x.| FROM dbo.Orders o CROSS APPLY dbo.OrderItems(o.Id) x");
  assert.deepEqual(result.suggestions.map(x => x.label), ["OrderId", "Quantity"]);
});
test("column details are shown when metadata contains types", () => {
  const result = analyze("SELECT * FROM dbo.t WHERE ", 26, [
    { schema: "dbo", name: "t", type: "Table", columns: ["value"],
      columnDetails: [{ name: "value", dataType: "int", nullable: false }] }
  ]);
  assert.match(result.suggestions[0].detail, /int ·? ?NOT NULL/);
});
test("foreign key suggestions contain the entire composite condition only after ON", () => {
  const fixtures = [
    { schema: "dbo", name: "Orders", type: "Table", columns: ["CustomerId", "TenantId"],
      foreignKeys: [{ name: "FK_customer", referencedSchema: "dbo", referencedTable: "Customers",
        columns: [{ column: "CustomerId", referencedColumn: "Id" }, { column: "TenantId", referencedColumn: "TenantId" }] }] },
    { schema: "dbo", name: "Customers", type: "Table", columns: ["Id", "TenantId"] },
  ];
  const sql = "SELECT * FROM dbo.Orders o JOIN dbo.Customers c ON ";
  const result = analyze(sql, sql.length, fixtures);
  assert.equal(result.suggestions.find(s => s.kind === "join").insertText,
    "o.CustomerId = c.Id AND o.TenantId = c.TenantId");
  assert.ok(!analyze(sql + "o.", sql.length + 2, fixtures).suggestions.some(s => s.kind === "join"));
});
test("variables and temporary table columns are scoped to the batch", () => {
  let sql = "DECLARE @id int; CREATE TABLE #orders (Id int, Status nvarchar(20)); SELECT * FROM #orders o WHERE ";
  let result = analyze(sql, sql.length, metadata);
  assert.ok(result.suggestions.some(s => s.label === "@id"));
  assert.ok(result.suggestions.some(s => s.label === "Status"));
  sql += "\nGO\nSELECT * FROM #orders o WHERE ";
  result = analyze(sql, sql.length, metadata);
  assert.ok(!result.suggestions.some(s => s.label === "@id" || s.label === "Status"));
});
test("table variables and DML targets expose their columns", () => {
  let sql = "DECLARE @items TABLE (Id int, Name nvarchar(20)); SELECT i. FROM @items i";
  let result = analyze(sql, sql.indexOf("i.") + 2, metadata);
  assert.deepEqual(result.suggestions.map(s => s.label), ["Id", "Name"]);
  sql = "INSERT INTO dbo.Orders (";
  result = analyze(sql, sql.length, metadata);
  assert.ok(result.suggestions.some(s => s.label === "CustomerId"));
  sql = "UPDATE dbo.Orders SET ";
  result = analyze(sql, sql.length, metadata);
  assert.ok(result.suggestions.some(s => s.label === "Status"));
});
test("qualified ambiguous table variable columns use a valid identifier", () => {
  const sql = "DECLARE @t TABLE (Id int); SELECT  FROM @t JOIN dbo.Orders o ON 1=1";
  const result = analyze(sql, sql.indexOf(" FROM"), metadata);
  assert.ok(result.suggestions.some(s => s.insertText === "@t.Id"));
});
test("CTEs are offered as sources", () => {
  const sql = "WITH recent AS (SELECT Id FROM dbo.Orders) SELECT * FROM ";
  assert.ok(analyze(sql, sql.length, metadata).suggestions.some(s => s.label === "recent"));
});
