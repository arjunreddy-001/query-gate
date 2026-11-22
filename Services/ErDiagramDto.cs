namespace DbAccessManager.Services;

public sealed record ErColumnDto(
    string Name,
    string DataType,
    bool IsPrimaryKey,
    bool IsForeignKey,
    bool Nullable);

public sealed record ErTableNode(
    string Schema,
    string Name,
    string FullName,
    List<ErColumnDto> Columns);

public sealed record ErRelationshipEdge(
    string FromTable,
    string FromColumn,
    string ToTable,
    string ToColumn,
    string ConstraintName);

public sealed record ErDiagramData(
    List<ErTableNode> Nodes,
    List<ErRelationshipEdge> Edges,
    string? FocusTable,
    int Hops,
    bool IsFullSchema);
