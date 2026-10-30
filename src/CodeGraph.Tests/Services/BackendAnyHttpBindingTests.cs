using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

public sealed class BackendAnyHttpBindingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAndPostCallsReachWildcardHandler_AndRegistrationEditsReconcilePersistedFacts(bool rust)
    {
        const string repository = "app";
        var path = rust ? "src/lib.rs" : "main.go";
        var source = rust ? """
            use axum::{Router, routing::any};
            fn handle() {}
            fn specific() {}
            fn build() { let r = Router::new().route("/wild", any(handle)); }
            """ : """
            package main
            import "github.com/gin-gonic/gin"
            func handle() {}
            func specific() {}
            func main() { r := gin.New(); r.Any("/wild", handle) }
            """;
        var store = new InMemoryGraphStore();
        var extractor = new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance);
        var context = new ExtractorContext { RepositoryName = repository, RootPath = "/test" };
        var pipeline = new IndexingPipeline(store, [], Options.Create(new IndexingOptions()), new LocalFileSystem(), NullLogger<IndexingPipeline>.Instance);
        var first = true;
        async Task ReplaceBackend(string updated)
        {
            var definitions = await extractor.ExtractAsync("/test/" + path, updated, context);
            var endpoints = await extractor.ExtractBackendEndpointsAsync("/test/" + path, updated, context, definitions.Nodes);
            endpoints.Succeeded.ShouldBeTrue(endpoints.FailureReason);
            var buffer = new GraphBuffer();
            foreach (var node in definitions.Nodes.Concat(endpoints.Nodes)) buffer.AddNode(node);
            foreach (var edge in definitions.Edges.Concat(endpoints.Edges)) buffer.AddEdge(edge);
            if (first)
                foreach (var method in new[] { "GET", "POST" })
                {
                    buffer.AddNode(new() { Repository = repository, Label = NodeLabel.Function, Name = method, QualifiedName = "client:" + method, FilePath = "ui/client.ts" });
                    buffer.AddNode(new() { Repository = repository, Label = NodeLabel.Route, Name = "raw", QualifiedName = $"route:*:{method}:/wild", Properties = new() { ["stub"] = true } });
                    buffer.AddEdge(new("client:" + method, $"route:*:{method}:/wild", EdgeType.HTTP_CALLS,
                        new() { ["http_method"] = method, ["url_pattern"] = "/wild", ["extraction_owner"] = "typescript" }));
                }
            var binding = await pipeline.RebindHttpCallsAsync(repository, buffer, !first,
                first ? new HashSet<string>() : new HashSet<string> { path }, CancellationToken.None);
            await store.ReplaceRepositoryFilesAsync(repository, first ? [path, "ui/client.ts"] : [path],
                buffer.AllNodes.ToList(), buffer.AllPendingEdges.ToList(), new Dictionary<string, string>(), retractions: binding.Retractions);
            first = false;
        }
        string ReachedHandler(string method)
        {
            var caller = store.Nodes.Single(node => node.QualifiedName == "client:" + method);
            var call = store.Edges.Single(edge => edge.SourceId == caller.Id && edge.Type == EdgeType.HTTP_CALLS && edge.Properties.ContainsKey("semantic_owner"));
            var route = store.Nodes.Single(node => node.Id == call.TargetId);
            route.QualifiedName.ShouldBe($"route:app:{method}:/wild");
            var handles = store.Edges.Single(edge => edge.SourceId == route.Id && edge.Type == EdgeType.HANDLES);
            return store.Nodes.Single(node => node.Id == handles.TargetId).Name;
        }

        await ReplaceBackend(source);
        ReachedHandler("GET").ShouldBe("handle");
        ReachedHandler("POST").ShouldBe("handle");
        var callerIds = store.Nodes.Where(node => node.FilePath == "ui/client.ts").Select(node => node.Id).Order().ToArray();
        if (rust)
        {
            await ReplaceBackend(source.Replace("any(handle)", "any(handle).post(specific)"));
            ReachedHandler("GET").ShouldBe("handle");
            ReachedHandler("POST").ShouldBe("specific");
            await ReplaceBackend(source);
            ReachedHandler("POST").ShouldBe("handle");
        }
        await ReplaceBackend(source.Replace(rust ? ".route(\"/wild\", any(handle))" : "r.Any(\"/wild\", handle)", ""));
        store.Edges.ShouldNotContain(edge => edge.Type == EdgeType.HTTP_CALLS && edge.Properties.ContainsKey("semantic_owner"));
        store.Edges.Count(edge => edge.Type == EdgeType.HTTP_CALLS && edge.Properties.ContainsKey("extraction_owner")).ShouldBe(2);
        store.Nodes.Where(node => node.FilePath == "ui/client.ts").Select(node => node.Id).Order().ShouldBe(callerIds);
    }
}
