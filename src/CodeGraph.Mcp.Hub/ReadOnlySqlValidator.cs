using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace CodeGraph.Mcp.Hub;

public sealed record ReadOnlySqlValidationResult(
    bool IsValid,
    string? ErrorMessage,
    IReadOnlyCollection<string> ReferencedTables,
    IReadOnlyCollection<string> ReferencedColumns,
    bool HasWildcardProjection)
{
    public static ReadOnlySqlValidationResult Invalid(string message) =>
        new(false, message, [], [], false);

    public static ReadOnlySqlValidationResult Valid(
        IReadOnlyCollection<string> referencedTables,
        IReadOnlyCollection<string> referencedColumns,
        bool hasWildcardProjection) =>
        new(true, null, referencedTables, referencedColumns, hasWildcardProjection);
}

/// <summary>
/// AST-based read-only SQL guard for the MySQL hub provider. Parses the statement with the
/// MySQL dialect and rejects anything that is not a single read-only statement, calls a
/// filesystem/timing/lock function, or uses SELECT ... INTO. A regex blocklist was previously
/// used here and was bypassable (e.g. LOAD_FILE, INTO DUMPFILE) — see Shortcut sc-1050.
///
/// On success it also surfaces the referenced table set, the referenced column set, and
/// whether a wildcard projection was used so sensitive-column policy can be evaluated
/// before the query executes — see Shortcut sc-1051.
/// </summary>
public static class ReadOnlySqlValidator
{
    // MySQL functions that read the filesystem, stall the server, or manipulate locks —
    // none of which belong in a read-only query path. Matched case-insensitively.
    private static readonly HashSet<string> DeniedFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_file",
        "sleep",
        "benchmark",
        "get_lock",
        "release_lock",
        "release_all_locks",
        "is_free_lock",
        "is_used_lock",
        "master_pos_wait",
        "source_pos_wait",
    };

    public static ReadOnlySqlValidationResult Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return ReadOnlySqlValidationResult.Invalid("sql is required.");

        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, new MySqlDialect());
        }
        catch (ParserException ex)
        {
            return ReadOnlySqlValidationResult.Invalid(
                $"The statement could not be parsed as read-only SQL: {ex.Message}");
        }

        if (statements.Count != 1)
            return ReadOnlySqlValidationResult.Invalid("Exactly one SQL statement is allowed.");

        if (!IsReadOnlyStatement(statements[0]))
            return ReadOnlySqlValidationResult.Invalid(
                "Only SELECT, SHOW, DESCRIBE/DESC, and EXPLAIN statements are allowed.");

        var inspector = new ReadOnlyInspector();
        statements.Visit(inspector);
        inspector.InspectStructure(statements);

        if (inspector.DeniedFunction is not null)
            return ReadOnlySqlValidationResult.Invalid(
                $"The function '{inspector.DeniedFunction}' is not allowed in read-only SQL.");

        if (inspector.HasSelectInto)
            return ReadOnlySqlValidationResult.Invalid("SELECT ... INTO is not allowed.");

        return ReadOnlySqlValidationResult.Valid(
            inspector.ReferencedTables,
            inspector.ReferencedColumns,
            inspector.HasWildcardProjection);
    }

    private static bool IsReadOnlyStatement(Statement statement) => statement switch
    {
        Statement.Select => true,
        Statement.ExplainTable => true,
        Statement.Explain explain => IsReadOnlyStatement(explain.Statement),
        Statement.ShowColumns
            or Statement.ShowTables
            or Statement.ShowVariable
            or Statement.ShowVariables
            or Statement.ShowCreate
            or Statement.ShowDatabases
            or Statement.ShowSchemas
            or Statement.ShowFunctions
            or Statement.ShowCollation
            or Statement.ShowStatus
            or Statement.ShowViews => true,
        _ => false,
    };

    private sealed class ReadOnlyInspector : Visitor
    {
        public string? DeniedFunction { get; private set; }

        public bool HasSelectInto { get; private set; }

        public bool HasWildcardProjection { get; private set; }

        public HashSet<string> ReferencedTables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ReferencedColumns { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override ControlFlow PreVisitExpression(Expression expression)
        {
            switch (expression)
            {
                case Expression.Function function:
                    var name = function.Name.Values.LastOrDefault()?.Value;
                    if (name is not null && DeniedFunctions.Contains(name))
                    {
                        DeniedFunction = name;
                        return ControlFlow.Break;
                    }

                    break;

                case Expression.Identifier identifier:
                    ReferencedColumns.Add(identifier.Ident.Value);
                    break;

                case Expression.CompoundIdentifier compound:
                    var last = compound.Idents.LastOrDefault()?.Value;
                    if (last is not null)
                        ReferencedColumns.Add(last);
                    break;
            }

            return ControlFlow.Continue;
        }

        public override ControlFlow PreVisitRelation(ObjectName name)
        {
            // The last identifier is the table name; earlier parts are schema/catalog qualifiers.
            var table = name.Values.LastOrDefault()?.Value;
            if (table is not null)
                ReferencedTables.Add(table);
            return ControlFlow.Continue;
        }

        // The visitor surfaces expressions and relations but not SELECT nodes, so walk the
        // query tree directly to catch wildcard projections, SELECT ... INTO, and wildcard
        // projections inside derived-table subqueries.
        public void InspectStructure(IEnumerable<Statement> statements)
        {
            foreach (var statement in statements)
                InspectStatement(statement);
        }

        private void InspectStatement(Statement statement)
        {
            switch (statement)
            {
                case Statement.Select select:
                    InspectQuery(select.Query);
                    break;
                case Statement.Explain explain:
                    InspectStatement(explain.Statement);
                    break;
            }
        }

        private void InspectQuery(Query query) => InspectSetExpression(query.Body);

        private void InspectSetExpression(SetExpression body)
        {
            switch (body)
            {
                case SetExpression.SelectExpression selectExpression:
                    InspectSelect(selectExpression.Select);
                    break;
                case SetExpression.SetOperation setOperation:
                    InspectSetExpression(setOperation.Left);
                    InspectSetExpression(setOperation.Right);
                    break;
                case SetExpression.QueryExpression queryExpression:
                    InspectQuery(queryExpression.Query);
                    break;
            }
        }

        private void InspectSelect(Select select)
        {
            if (select.Into is not null)
                HasSelectInto = true;

            foreach (var item in select.Projection ?? [])
            {
                if (item is SelectItem.Wildcard or SelectItem.QualifiedWildcard)
                    HasWildcardProjection = true;
            }

            foreach (var tableWithJoins in select.From ?? [])
                InspectTableWithJoins(tableWithJoins);
        }

        private void InspectTableWithJoins(TableWithJoins tableWithJoins)
        {
            InspectTableFactor(tableWithJoins.Relation);
            foreach (var join in tableWithJoins.Joins ?? [])
                InspectTableFactor(join.Relation);
        }

        private void InspectTableFactor(TableFactor relation)
        {
            switch (relation)
            {
                case TableFactor.Derived derived:
                    InspectQuery(derived.SubQuery);
                    break;
                case TableFactor.NestedJoin nestedJoin:
                    InspectTableWithJoins(nestedJoin.TableWithJoins);
                    break;
            }
        }
    }
}
