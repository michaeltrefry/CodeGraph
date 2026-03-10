using Microsoft.SqlServer.TransactSql.ScriptDom;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Extractors.Sql;

/// <summary>
/// Walks a ScriptDom AST and emits GraphNodes and PendingEdges for SQL objects.
/// Extracts tables, views, stored procedures, functions, and their relationships.
/// </summary>
public class SqlGraphVisitor : TSqlFragmentVisitor
{
    private readonly ExtractorContext _context;
    private readonly string _filePath;

    private readonly List<GraphNode> _nodes = [];
    private readonly List<PendingEdge> _edges = [];

    // Track the current scope for relationship building (e.g., inside a proc or view)
    private string? _currentObjectQN;

    public SqlGraphVisitor(ExtractorContext context, string filePath)
    {
        _context = context;
        _filePath = filePath;
    }

    public ExtractionResult GetResult() => new()
    {
        Nodes = _nodes,
        Edges = _edges
    };

    // ── CREATE TABLE ──────────────────────────────────────────────

    public override void Visit(CreateTableStatement node)
    {
        var tableName = GetObjectName(node.SchemaObjectName);
        var qn = BuildQualifiedName(tableName);

        var columns = new List<Dictionary<string, object>>();
        var primaryKeyColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Gather PK columns from table-level constraints
        foreach (var constraint in node.Definition.TableConstraints)
        {
            if (constraint is UniqueConstraintDefinition { IsPrimaryKey: true } pk)
            {
                foreach (var col in pk.Columns)
                    primaryKeyColumns.Add(col.Column.MultiPartIdentifier.Identifiers.Last().Value);
            }
        }

        foreach (var col in node.Definition.ColumnDefinitions)
        {
            var colName = col.ColumnIdentifier.Value;
            var colInfo = new Dictionary<string, object>
            {
                ["name"] = colName,
                ["type"] = FragmentToString(col.DataType),
                ["nullable"] = !col.Constraints.Any(c => c is NullableConstraintDefinition { Nullable: false })
            };

            // Check for inline PK constraint
            if (col.Constraints.Any(c => c is UniqueConstraintDefinition { IsPrimaryKey: true }))
                primaryKeyColumns.Add(colName);

            if (primaryKeyColumns.Contains(colName))
                colInfo["is_primary_key"] = true;

            // Check for inline FK
            foreach (var fk in col.Constraints.OfType<ForeignKeyConstraintDefinition>())
            {
                var refTable = GetObjectName(fk.ReferenceTableName);
                var refQN = BuildQualifiedName(refTable);
                _edges.Add(new PendingEdge(qn, refQN, EdgeType.QUERIES,
                    new Dictionary<string, object>
                    {
                        ["relationship"] = "foreign_key",
                        ["column"] = colName,
                        ["referenced_column"] = fk.ReferencedTableColumns.FirstOrDefault()?.Value ?? ""
                    }));
            }

            columns.Add(colInfo);
        }

        // Table-level foreign key constraints
        foreach (var constraint in node.Definition.TableConstraints.OfType<ForeignKeyConstraintDefinition>())
        {
            var refTable = GetObjectName(constraint.ReferenceTableName);
            var refQN = BuildQualifiedName(refTable);
            var fkCols = constraint.Columns.Select(c => c.Value).ToList();
            var refCols = constraint.ReferencedTableColumns.Select(c => c.Value).ToList();

            _edges.Add(new PendingEdge(qn, refQN, EdgeType.QUERIES,
                new Dictionary<string, object>
                {
                    ["relationship"] = "foreign_key",
                    ["columns"] = string.Join(", ", fkCols),
                    ["referenced_columns"] = string.Join(", ", refCols)
                }));
        }

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            Label = NodeLabel.Table,
            Name = tableName,
            QualifiedName = qn,
            FilePath = _filePath,
            StartLine = node.StartLine,
            EndLine = node.StartLine + CountLines(node),
            Properties = new Dictionary<string, object>
            {
                ["columns"] = columns,
                ["column_count"] = columns.Count
            }
        });

        // Don't call base — we've handled children (columns, constraints) explicitly
    }

    // ── ALTER TABLE (FK additions) ────────────────────────────────

    public override void Visit(AlterTableAddTableElementStatement node)
    {
        var tableName = GetObjectName(node.SchemaObjectName);
        var tableQN = BuildQualifiedName(tableName);

        foreach (var constraint in node.Definition.TableConstraints.OfType<ForeignKeyConstraintDefinition>())
        {
            var refTable = GetObjectName(constraint.ReferenceTableName);
            var refQN = BuildQualifiedName(refTable);

            _edges.Add(new PendingEdge(tableQN, refQN, EdgeType.QUERIES,
                new Dictionary<string, object>
                {
                    ["relationship"] = "foreign_key",
                    ["via"] = "alter_table"
                }));
        }

        base.Visit(node);
    }

    // ── CREATE VIEW ───────────────────────────────────────────────

    public override void Visit(CreateViewStatement node)
    {
        var viewName = GetObjectName(node.SchemaObjectName);
        var qn = BuildQualifiedName(viewName);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            Label = NodeLabel.View,
            Name = viewName,
            QualifiedName = qn,
            FilePath = _filePath,
            StartLine = node.StartLine,
            EndLine = node.StartLine + CountLines(node),
        });

        // Walk the SELECT body to find table references
        _currentObjectQN = qn;
        node.SelectStatement.Accept(this);
        _currentObjectQN = null;
    }

    // ── ALTER VIEW ────────────────────────────────────────────────

    public override void Visit(AlterViewStatement node)
    {
        var viewName = GetObjectName(node.SchemaObjectName);
        var qn = BuildQualifiedName(viewName);

        // Treat ALTER VIEW as an update — same node QN, pipeline will upsert
        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            Label = NodeLabel.View,
            Name = viewName,
            QualifiedName = qn,
            FilePath = _filePath,
            StartLine = node.StartLine,
            EndLine = node.StartLine + CountLines(node),
        });

        _currentObjectQN = qn;
        node.SelectStatement.Accept(this);
        _currentObjectQN = null;
    }

    // ── CREATE PROCEDURE ──────────────────────────────────────────

    public override void Visit(CreateProcedureStatement node)
    {
        VisitProcedure(node.ProcedureReference.Name, node.Parameters, node.StatementList, node);
    }

    public override void Visit(AlterProcedureStatement node)
    {
        VisitProcedure(node.ProcedureReference.Name, node.Parameters, node.StatementList, node);
    }

    private void VisitProcedure(SchemaObjectName name, IList<ProcedureParameter> parameters,
        StatementList? body, TSqlFragment node)
    {
        var procName = GetObjectName(name);
        var qn = BuildQualifiedName(procName);

        var paramList = parameters.Select(p => new Dictionary<string, object>
        {
            ["name"] = p.VariableName.Value,
            ["type"] = FragmentToString(p.DataType),
            ["is_output"] = p.Modifier == ParameterModifier.Output
        }).ToList();

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            Label = NodeLabel.StoredProcedure,
            Name = procName,
            QualifiedName = qn,
            FilePath = _filePath,
            StartLine = node.StartLine,
            EndLine = node.StartLine + CountLines(node),
            Properties = new Dictionary<string, object>
            {
                ["parameters"] = paramList,
                ["parameter_count"] = paramList.Count
            }
        });

        // Walk body to discover table references and proc calls
        if (body != null)
        {
            _currentObjectQN = qn;
                body.Accept(this);
            _currentObjectQN = null;
            }
    }

    // ── CREATE FUNCTION ───────────────────────────────────────────

    public override void Visit(CreateFunctionStatement node)
    {
        VisitFunction(node.Name, node.Parameters, node);
    }

    public override void Visit(AlterFunctionStatement node)
    {
        VisitFunction(node.Name, node.Parameters, node);
    }

    private void VisitFunction(SchemaObjectName name, IList<ProcedureParameter> parameters,
        TSqlStatement node)
    {
        var funcName = GetObjectName(name);
        var qn = BuildQualifiedName(funcName);

        var paramList = parameters.Select(p => new Dictionary<string, object>
        {
            ["name"] = p.VariableName.Value,
            ["type"] = FragmentToString(p.DataType),
        }).ToList();

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            Label = NodeLabel.Function,
            Name = funcName,
            QualifiedName = qn,
            FilePath = _filePath,
            StartLine = node.StartLine,
            EndLine = node.StartLine + CountLines(node),
            Properties = new Dictionary<string, object>
            {
                ["parameters"] = paramList,
                ["parameter_count"] = paramList.Count,
                ["kind"] = "sql_function"
            }
        });

        // Walk body for table refs
        _currentObjectQN = qn;
        base.Visit(node);
        _currentObjectQN = null;
    }

    // ── CREATE INDEX ──────────────────────────────────────────────

    public override void Visit(CreateIndexStatement node)
    {
        // We don't create a node for indexes, but record a property
        // The table node should already exist or will be upserted
        var tableName = GetObjectName(node.OnName);
        var tableQN = BuildQualifiedName(tableName);
        var indexName = node.Name?.Value ?? "unnamed";
        var columns = node.Columns.Select(c =>
            c.Column.MultiPartIdentifier.Identifiers.Last().Value).ToList();

        // Store as an edge property — the table references itself via index
        // This is informational, attached when the table node is queried
        _edges.Add(new PendingEdge(tableQN, tableQN, EdgeType.DEFINES,
            new Dictionary<string, object>
            {
                ["relationship"] = "index",
                ["index_name"] = indexName,
                ["columns"] = string.Join(", ", columns),
                ["is_unique"] = node.Unique
            }));

        base.Visit(node);
    }

    // ── Table references inside procs/views/functions ─────────────

    public override void Visit(NamedTableReference node)
    {
        if (_currentObjectQN == null)
        {
            base.Visit(node);
            return;
        }

        var tableName = GetObjectName(node.SchemaObject);

        // Skip temp tables, table variables, and common CTEs
        if (tableName.StartsWith('#') || tableName.StartsWith('@'))
        {
            base.Visit(node);
            return;
        }

        var tableQN = BuildQualifiedName(tableName);
        _edges.Add(new PendingEdge(_currentObjectQN, tableQN, EdgeType.QUERIES,
            new Dictionary<string, object>
            {
                ["confidence_band"] = "high"
            }));

        base.Visit(node);
    }

    // ── EXEC calls inside procs ───────────────────────────────────

    public override void Visit(ExecuteStatement node)
    {
        if (_currentObjectQN != null && node.ExecuteSpecification.ExecutableEntity
                is ExecutableProcedureReference procRef)
        {
            var targetName = GetObjectName(procRef.ProcedureReference.ProcedureReference.Name);
            var targetQN = BuildQualifiedName(targetName);

            _edges.Add(new PendingEdge(_currentObjectQN, targetQN, EdgeType.CALLS,
                new Dictionary<string, object>
                {
                    ["confidence_band"] = "high"
                }));
        }

        base.Visit(node);
    }

    // ── Helpers ───────────────────────────────────────────────────

    private static string GetObjectName(SchemaObjectName schemaObject)
    {
        var identifiers = schemaObject.Identifiers;
        return identifiers.Count switch
        {
            // Just name
            1 => identifiers[0].Value,
            // schema.name
            2 => $"{identifiers[0].Value}.{identifiers[1].Value}",
            // database.schema.name
            3 => $"{identifiers[1].Value}.{identifiers[2].Value}",
            // server.database.schema.name
            4 => $"{identifiers[2].Value}.{identifiers[3].Value}",
            _ => identifiers.Last().Value
        };
    }

    private string BuildQualifiedName(string objectName)
    {
        // Prefix with project name for cross-repo uniqueness
        return $"{_context.ProjectName}.{objectName}";
    }

    private static string FragmentToString(TSqlFragment? fragment)
    {
        if (fragment == null) return "unknown";

        // Reconstruct text from tokens
        var tokens = new List<string>();
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            tokens.Add(fragment.ScriptTokenStream[i].Text);
        }
        return string.Join("", tokens).Trim();
    }

    private static int CountLines(TSqlFragment fragment)
    {
        if (fragment.LastTokenIndex < 0 || fragment.FirstTokenIndex < 0)
            return 0;

        var lastToken = fragment.ScriptTokenStream[fragment.LastTokenIndex];
        return lastToken.Line - fragment.StartLine;
    }
}
