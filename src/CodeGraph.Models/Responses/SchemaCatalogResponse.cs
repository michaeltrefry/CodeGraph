namespace CodeGraph.Models.Responses;

public record SchemaListResponse(
    IReadOnlyList<SchemaListItem> Items,
    int Total,
    int TotalTables,
    int TotalViews,
    int TotalProcedures,
    int Page,
    int PageSize,
    IReadOnlyList<string> Servers,
    IReadOnlyList<string> Databases);

public record SchemaListItem(
    string Name,
    string ServerName,
    string DatabaseName,
    int TableCount,
    int ViewCount,
    int ProcedureCount,
    DateTime? IndexedAt,
    string? Language,
    string? Framework,
    Dictionary<string, object>? Properties);

public record SchemaCatalogResponse(
    string ProjectName,
    string ServerName,
    string DatabaseName,
    IReadOnlyList<SchemaObjectResponse> Tables,
    IReadOnlyList<SchemaObjectResponse> Views,
    IReadOnlyList<SchemaProcedureResponse> Procedures);

public record SchemaObjectResponse(
    long Id,
    string Name,
    string QualifiedName,
    string Label,
    string? Comment,
    IReadOnlyList<string> PrimaryKeyColumns,
    IReadOnlyList<SchemaIndexResponse> Indexes,
    IReadOnlyList<SchemaConstraintResponse> Constraints,
    IReadOnlyList<SchemaForeignKeyResponse> ForeignKeys,
    IReadOnlyList<SchemaColumnResponse> Columns);

public record SchemaProcedureResponse(
    long Id,
    string Name,
    string QualifiedName,
    string RoutineType,
    string? Comment,
    IReadOnlyList<SchemaParameterResponse> Parameters);

public record SchemaColumnResponse(
    long Id,
    string Name,
    string QualifiedName,
    int Ordinal,
    string DataType,
    bool Nullable,
    bool IsPrimaryKey,
    string? Default,
    string? Key,
    string? Extra,
    string? Comment);

public record SchemaIndexResponse(
    string Name,
    bool IsUnique,
    string? IndexType,
    IReadOnlyList<string> Columns);

public record SchemaConstraintResponse(
    string Name,
    string ConstraintType,
    IReadOnlyList<string> Columns,
    string? ReferencedTable,
    IReadOnlyList<string>? ReferencedColumns,
    string? CheckClause);

public record SchemaForeignKeyResponse(
    string Name,
    IReadOnlyList<string> Columns,
    string ReferencedTable,
    IReadOnlyList<string> ReferencedColumns);

public record SchemaParameterResponse(
    string Name,
    int Ordinal,
    string Mode,
    string DataType,
    bool Nullable);
