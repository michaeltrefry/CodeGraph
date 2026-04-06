using System.Text.RegularExpressions;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Extractors.ColdFusion;

/// <summary>
/// Regex-based best-effort parser for ColdFusion .cfm and .cfc files.
/// Extracts components, functions, invocations, HTTP calls, queries, and includes.
/// Confidence is medium/low throughout since regex can't fully parse CF.
/// </summary>
public partial class ColdFusionParser
{
    private readonly ExtractorContext _context;
    private readonly string _filePath;
    private readonly bool _isCfc;

    private readonly List<GraphNode> _nodes = [];
    private readonly List<PendingEdge> _edges = [];
    private readonly List<UnresolvedCall> _unresolvedCalls = [];

    public ColdFusionParser(ExtractorContext context, string filePath)
    {
        _context = context;
        _filePath = filePath;
        _isCfc = filePath.EndsWith(".cfc", StringComparison.OrdinalIgnoreCase);
    }

    public ExtractionResult Parse(string content)
    {
        // For CFCs, extract the component declaration
        string? componentQN = null;
        if (_isCfc)
        {
            componentQN = ExtractComponent(content);
        }

        ExtractFunctions(content, componentQN);
        ExtractInvocations(content, componentQN);
        ExtractHttpCalls(content, componentQN);
        ExtractQueries(content, componentQN);
        ExtractIncludes(content, componentQN);
        ExtractCreateObject(content, componentQN);

        return new ExtractionResult
        {
            Nodes = _nodes,
            Edges = _edges,
            UnresolvedCalls = _unresolvedCalls
        };
    }

    // ── cfcomponent ───────────────────────────────────────────────

    private string? ExtractComponent(string content)
    {
        // Match the <cfcomponent ...> tag, then extract attributes individually
        var tagMatch = CfComponentTagRegex().Match(content);

        string componentName;
        string? extendsType = null;

        if (tagMatch.Success)
        {
            var tagContent = tagMatch.Value;
            var nameMatch = TagAttributeRegex("name").Match(tagContent);
            var extendsMatch = TagAttributeRegex("extends").Match(tagContent);

            componentName = nameMatch.Success ? nameMatch.Groups["value"].Value : DeriveNameFromPath();
            extendsType = extendsMatch.Success ? extendsMatch.Groups["value"].Value : null;
        }
        else
        {
            // Try CFScript style: component extends="Foo" { or component {
            var scriptMatch = CfScriptComponentRegex().Match(content);
            if (scriptMatch.Success)
            {
                componentName = DeriveNameFromPath();
                extendsType = scriptMatch.Groups["extends"].Success
                    ? scriptMatch.Groups["extends"].Value
                    : null;
            }
            else
            {
                // CFC without explicit component tag — use filename
                componentName = DeriveNameFromPath();
            }
        }

        var qn = BuildQualifiedName(componentName);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            Label = NodeLabel.Component,
            Name = componentName,
            QualifiedName = qn,
            FilePath = _filePath,
            StartLine = 1,
            Properties = new Dictionary<string, object>
            {
                ["confidence_band"] = "medium",
                ["language"] = "coldfusion"
            }
        });

        if (extendsType != null)
        {
            var extendsQN = BuildQualifiedName(extendsType);
            _edges.Add(new PendingEdge(qn, extendsQN, EdgeType.INHERITS,
                new Dictionary<string, object> { ["confidence_band"] = "medium" }));
        }

        return qn;
    }

    // ── cffunction ────────────────────────────────────────────────

    private void ExtractFunctions(string content, string? parentQN)
    {
        // Tag syntax: <cffunction name="doStuff" access="public" returntype="string">
        foreach (Match match in CfFunctionTagRegex().Matches(content))
        {
            var funcName = match.Groups["name"].Value;
            var access = match.Groups["access"].Success ? match.Groups["access"].Value : "public";
            var returnType = match.Groups["returntype"].Success ? match.Groups["returntype"].Value : "any";
            var line = CountLinesTo(content, match.Index);

            var qn = parentQN != null ? $"{parentQN}.{funcName}" : BuildQualifiedName(funcName);

            _nodes.Add(new GraphNode
            {
                Project = _context.ProjectName,
                Label = NodeLabel.Function,
                Name = funcName,
                QualifiedName = qn,
                FilePath = _filePath,
                StartLine = line,
                Properties = new Dictionary<string, object>
                {
                    ["access"] = access,
                    ["return_type"] = returnType,
                    ["confidence_band"] = "medium",
                    ["language"] = "coldfusion"
                }
            });

            if (parentQN != null)
            {
                _edges.Add(new PendingEdge(parentQN, qn, EdgeType.DEFINES_METHOD));
            }
        }

        // CFScript syntax: public string function doStuff() {
        foreach (Match match in CfScriptFunctionRegex().Matches(content))
        {
            var funcName = match.Groups["name"].Value;
            var access = match.Groups["access"].Success ? match.Groups["access"].Value : "public";
            var returnType = match.Groups["returntype"].Success ? match.Groups["returntype"].Value : "any";
            var line = CountLinesTo(content, match.Index);

            var qn = parentQN != null ? $"{parentQN}.{funcName}" : BuildQualifiedName(funcName);

            // Avoid duplicates if both tag and script forms somehow appear
            if (_nodes.Any(n => n.QualifiedName == qn)) continue;

            _nodes.Add(new GraphNode
            {
                Project = _context.ProjectName,
                Label = NodeLabel.Function,
                Name = funcName,
                QualifiedName = qn,
                FilePath = _filePath,
                StartLine = line,
                Properties = new Dictionary<string, object>
                {
                    ["access"] = access,
                    ["return_type"] = returnType,
                    ["confidence_band"] = "medium",
                    ["language"] = "coldfusion",
                    ["syntax"] = "cfscript"
                }
            });

            if (parentQN != null)
            {
                _edges.Add(new PendingEdge(parentQN, qn, EdgeType.DEFINES_METHOD));
            }
        }
    }

    // ── cfinvoke ──────────────────────────────────────────────────

    private void ExtractInvocations(string content, string? parentQN)
    {
        // <cfinvoke component="my.component.Path" method="doStuff">
        foreach (Match match in CfInvokeRegex().Matches(content))
        {
            var component = match.Groups["component"].Value;
            var method = match.Groups["method"].Value;

            var targetQN = BuildQualifiedName($"{component}.{method}");
            var sourceQN = parentQN ?? BuildFileQualifiedName();

            _edges.Add(new PendingEdge(sourceQN, targetQN, EdgeType.CALLS,
                new Dictionary<string, object> { ["confidence_band"] = "medium" }));

            _unresolvedCalls.Add(new UnresolvedCall(
                sourceQN, $"{component}.{method}", component, 0.6));
        }
    }

    // ── createObject ──────────────────────────────────────────────

    private void ExtractCreateObject(string content, string? parentQN)
    {
        // createObject("component", "my.component.Path")
        foreach (Match match in CreateObjectRegex().Matches(content))
        {
            var componentPath = match.Groups["path"].Value;
            var targetQN = BuildQualifiedName(componentPath);
            var sourceQN = parentQN ?? BuildFileQualifiedName();

            _edges.Add(new PendingEdge(sourceQN, targetQN, EdgeType.USES_TYPE,
                new Dictionary<string, object> { ["confidence_band"] = "medium" }));
        }
    }

    // ── cfhttp ────────────────────────────────────────────────────

    private void ExtractHttpCalls(string content, string? parentQN)
    {
        // <cfhttp url="https://api.example.com/endpoint" method="GET">
        foreach (Match match in CfHttpRegex().Matches(content))
        {
            var url = match.Groups["url"].Value;
            var httpMethod = match.Groups["method"].Success
                ? match.Groups["method"].Value.ToUpperInvariant()
                : "GET";

            var sourceQN = parentQN ?? BuildFileQualifiedName();

            // Create a Route-style node for the outbound call
            var routeQN = $"{sourceQN}:HTTP:{httpMethod}:{url}";
            _nodes.Add(new GraphNode
            {
                Project = _context.ProjectName,
                Label = NodeLabel.Route,
                Name = $"{httpMethod} {url}",
                QualifiedName = routeQN,
                FilePath = _filePath,
                StartLine = CountLinesTo(content, match.Index),
                Properties = new Dictionary<string, object>
                {
                    ["http_method"] = httpMethod,
                    ["url"] = url,
                    ["direction"] = "outbound",
                    ["confidence_band"] = "medium",
                    ["language"] = "coldfusion"
                }
            });

            _edges.Add(new PendingEdge(sourceQN, routeQN, EdgeType.HTTP_CALLS,
                new Dictionary<string, object>
                {
                    ["http_method"] = httpMethod,
                    ["url"] = url,
                    ["confidence_band"] = "medium"
                }));
        }
    }

    // ── cfquery ───────────────────────────────────────────────────

    private void ExtractQueries(string content, string? parentQN)
    {
        // <cfquery name="qResult" datasource="myDB">SELECT * FROM Orders</cfquery>
        foreach (Match match in CfQueryRegex().Matches(content))
        {
            var queryName = match.Groups["name"].Success ? match.Groups["name"].Value : null;
            var sqlBody = match.Groups["sql"].Value;
            var sourceQN = parentQN ?? BuildFileQualifiedName();

            // Extract table names from the SQL body (best-effort)
            foreach (Match tableMatch in SqlTableReferenceRegex().Matches(sqlBody))
            {
                var tableName = tableMatch.Groups["table"].Value;

                // Skip obvious non-tables
                if (IsKnownSqlKeyword(tableName)) continue;

                var tableQN = BuildQualifiedName(tableName);
                _edges.Add(new PendingEdge(sourceQN, tableQN, EdgeType.QUERIES,
                    new Dictionary<string, object>
                    {
                        ["confidence_band"] = "low",
                        ["query_name"] = queryName ?? "",
                        ["source"] = "cfquery"
                    }));
            }
        }
    }

    // ── cfinclude ─────────────────────────────────────────────────

    private void ExtractIncludes(string content, string? parentQN)
    {
        // <cfinclude template="/path/to/template.cfm">
        foreach (Match match in CfIncludeRegex().Matches(content))
        {
            var template = match.Groups["template"].Value;
            var sourceQN = parentQN ?? BuildFileQualifiedName();
            var targetQN = BuildQualifiedName(template.Replace('/', '.').TrimStart('.'));

            _edges.Add(new PendingEdge(sourceQN, targetQN, EdgeType.IMPORTS,
                new Dictionary<string, object>
                {
                    ["confidence_band"] = "medium",
                    ["template_path"] = template
                }));
        }
    }

    // ── Regex patterns ────────────────────────────────────────────

    // Match the entire <cfcomponent ...> opening tag
    [GeneratedRegex(
        @"<cfcomponent\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CfComponentTagRegex();

    // Extract a named attribute value from a tag string
    private static Regex TagAttributeRegex(string attrName) =>
        new($@"\b{attrName}\s*=\s*[""'](?<value>[^""']+)[""']", RegexOptions.IgnoreCase);

    // component extends="..." { (CFScript)
    [GeneratedRegex(
        @"^\s*component\b(?:\s+extends\s*=\s*[""'](?<extends>[^""']+)[""'])?\s*\{",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CfScriptComponentRegex();

    // <cffunction name="..." access="..." returntype="...">
    [GeneratedRegex(
        @"<cffunction\b[^>]*?\bname\s*=\s*[""'](?<name>[^""']+)[""'](?:[^>]*?\baccess\s*=\s*[""'](?<access>[^""']+)[""'])?(?:[^>]*?\breturntype\s*=\s*[""'](?<returntype>[^""']+)[""'])?[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex CfFunctionTagRegex();

    // CFScript: public string function doStuff(
    [GeneratedRegex(
        @"(?:(?<access>public|private|remote|package)\s+)?(?:(?<returntype>\w+)\s+)?function\s+(?<name>\w+)\s*\(",
        RegexOptions.IgnoreCase)]
    private static partial Regex CfScriptFunctionRegex();

    // <cfinvoke component="..." method="...">
    [GeneratedRegex(
        @"<cfinvoke\b[^>]*?\bcomponent\s*=\s*[""'](?<component>[^""']+)[""'][^>]*?\bmethod\s*=\s*[""'](?<method>[^""']+)[""'][^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex CfInvokeRegex();

    // createObject("component", "path.to.Component")
    [GeneratedRegex(
        @"createObject\s*\(\s*[""']component[""']\s*,\s*[""'](?<path>[^""']+)[""']\s*\)",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreateObjectRegex();

    // <cfhttp url="..." method="...">
    [GeneratedRegex(
        @"<cfhttp\b[^>]*?\burl\s*=\s*[""'](?<url>[^""']+)[""'](?:[^>]*?\bmethod\s*=\s*[""'](?<method>[^""']+)[""'])?[^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex CfHttpRegex();

    // <cfquery name="...">...SQL...</cfquery>
    [GeneratedRegex(
        @"<cfquery\b(?:[^>]*?\bname\s*=\s*[""'](?<name>[^""']+)[""'])?[^>]*>(?<sql>[\s\S]*?)</cfquery>",
        RegexOptions.IgnoreCase)]
    private static partial Regex CfQueryRegex();

    // SQL table references: FROM table, JOIN table, INTO table, UPDATE table
    [GeneratedRegex(
        @"(?:FROM|JOIN|INTO|UPDATE)\s+(?:\[?dbo\]?\.)?\[?(?<table>\w+)\]?",
        RegexOptions.IgnoreCase)]
    private static partial Regex SqlTableReferenceRegex();

    // <cfinclude template="...">
    [GeneratedRegex(
        @"<cfinclude\b[^>]*?\btemplate\s*=\s*[""'](?<template>[^""']+)[""'][^>]*>",
        RegexOptions.IgnoreCase)]
    private static partial Regex CfIncludeRegex();

    // ── Helpers ───────────────────────────────────────────────────

    private string DeriveNameFromPath()
    {
        return Path.GetFileNameWithoutExtension(_filePath);
    }

    private string BuildQualifiedName(string name)
    {
        return $"{_context.ProjectName}.{name}";
    }

    private string BuildFileQualifiedName()
    {
        var relativePath = _filePath;
        if (_context.RootPath != null && _filePath.StartsWith(_context.RootPath))
        {
            relativePath = _filePath[_context.RootPath.Length..].TrimStart('/', '\\');
        }
        return $"{_context.ProjectName}.{relativePath.Replace('/', '.').Replace('\\', '.')}";
    }

    private static int CountLinesTo(string content, int charIndex)
    {
        var line = 1;
        for (var i = 0; i < charIndex && i < content.Length; i++)
        {
            if (content[i] == '\n') line++;
        }
        return line;
    }

    private static readonly HashSet<string> SqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WHERE", "SET", "VALUES", "NULL", "NOT", "AND", "OR",
        "IN", "EXISTS", "CASE", "WHEN", "THEN", "ELSE", "END", "AS",
        "ON", "LEFT", "RIGHT", "INNER", "OUTER", "CROSS", "FULL"
    };

    private static bool IsKnownSqlKeyword(string name) => SqlKeywords.Contains(name);
}
