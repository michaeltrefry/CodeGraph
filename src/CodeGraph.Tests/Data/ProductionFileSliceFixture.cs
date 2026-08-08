using CodeGraph.Extractors.ColdFusion;
using CodeGraph.Extractors.Sql;
using CodeGraph.Models;
using CodeGraph.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeGraph.Tests.Data;

internal static class ProductionFileSliceFixture
{
    internal static readonly string[] FilePaths = ["schema.sql", "page.cfm", "OrderService.cfc"];

    internal static async Task<(ProductionSlice Old, ProductionSlice New)> CreateAsync(
        string project,
        string rootPath)
    {
        var context = new ExtractorContext { ProjectName = project, RootPath = rootPath };
        var sql = new SqlExtractor(NullLogger<SqlExtractor>.Instance);
        var coldFusion = new ColdFusionExtractor(NullLogger<ColdFusionExtractor>.Instance);

        var oldResults = new[]
        {
            await sql.ExtractAsync(
                Path.Combine(rootPath, "schema.sql"),
                "CREATE TABLE OldOrders (Id INT PRIMARY KEY);",
                context),
            await coldFusion.ExtractAsync(
                Path.Combine(rootPath, "page.cfm"),
                "<cffunction name=\"oldPageAction\"></cffunction>",
                context),
            await coldFusion.ExtractAsync(
                Path.Combine(rootPath, "OrderService.cfc"),
                "<cfcomponent name=\"OldOrderService\"><cffunction name=\"runOld\"></cffunction></cfcomponent>",
                context)
        };
        var newResults = new[]
        {
            await sql.ExtractAsync(
                Path.Combine(rootPath, "schema.sql"),
                "CREATE TABLE NewOrders (Id INT PRIMARY KEY);",
                context),
            await coldFusion.ExtractAsync(
                Path.Combine(rootPath, "page.cfm"),
                "<cffunction name=\"newPageAction\"></cffunction>",
                context),
            await coldFusion.ExtractAsync(
                Path.Combine(rootPath, "OrderService.cfc"),
                "<cfcomponent name=\"NewOrderService\"><cffunction name=\"runNew\"></cffunction></cfcomponent>",
                context)
        };

        return (Build(oldResults), Build(newResults));

        static ProductionSlice Build(IEnumerable<ExtractionResult> results)
        {
            var materialized = results.ToList();
            return new ProductionSlice(
                materialized.SelectMany(result => result.Nodes).ToList(),
                materialized.SelectMany(result => result.Edges).ToList());
        }
    }
}

internal sealed record ProductionSlice(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<PendingEdge> Edges);
