using CodeGraph.Extractors.Rust;
using CodeGraph.Models;
using CodeGraph.Services;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

public class RustScipImporterTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "DemoRust",
        RootPath = "/repo"
    };

    [Fact]
    public void Import_MapsRustDefinitionsReferencesAndImplementations()
    {
        const string traitSymbol = "rust-analyzer cargo demo 0.1.0 demo/Greeter#";
        const string structSymbol = "rust-analyzer cargo demo 0.1.0 demo/ConsoleGreeter#";
        const string methodSymbol = "rust-analyzer cargo demo 0.1.0 demo/ConsoleGreeter#greet().";
        const string helperSymbol = "rust-analyzer cargo demo 0.1.0 demo/helper().";

        var json = $$"""
            {
              "metadata": {
                "toolInfo": { "name": "rust-analyzer" },
                "projectRoot": "file:///repo",
                "textDocumentEncoding": "UTF8"
              },
              "documents": [
                {
                  "language": "rust",
                  "relativePath": "src/lib.rs",
                  "symbols": [
                    { "symbol": "{{traitSymbol}}", "kind": "Trait", "displayName": "Greeter" },
                    {
                      "symbol": "{{structSymbol}}",
                      "kind": "Struct",
                      "displayName": "ConsoleGreeter",
                      "relationships": [
                        { "symbol": "{{traitSymbol}}", "isImplementation": true }
                      ]
                    },
                    { "symbol": "{{methodSymbol}}", "kind": "Method", "displayName": "greet" },
                    { "symbol": "{{helperSymbol}}", "kind": "Function", "displayName": "helper" }
                  ],
                  "occurrences": [
                    {
                      "symbol": "{{traitSymbol}}",
                      "symbolRoles": 1,
                      "range": [0, 10, 17],
                      "singleLineEnclosingRange": { "startLine": 0, "startCharacter": 0, "endCharacter": 20 }
                    },
                    {
                      "symbol": "{{structSymbol}}",
                      "symbolRoles": 1,
                      "range": [4, 11, 25],
                      "singleLineEnclosingRange": { "startLine": 4, "startCharacter": 0, "endCharacter": 26 }
                    },
                    {
                      "symbol": "{{methodSymbol}}",
                      "symbolRoles": 1,
                      "range": [8, 7, 12],
                      "multiLineEnclosingRange": { "startLine": 8, "startCharacter": 4, "endLine": 10, "endCharacter": 5 }
                    },
                    {
                      "symbol": "{{helperSymbol}}",
                      "symbolRoles": 8,
                      "range": [9, 8, 14]
                    },
                    {
                      "symbol": "{{helperSymbol}}",
                      "symbolRoles": 1,
                      "range": [13, 3, 9],
                      "multiLineEnclosingRange": { "startLine": 13, "startCharacter": 0, "endLine": 15, "endCharacter": 1 }
                    },
                    {
                      "symbol": "{{structSymbol}}",
                      "symbolRoles": 8,
                      "range": [14, 12, 26]
                    }
                  ]
                }
              ]
            }
            """;

        var result = ScipJsonImporter.Import(json, TestContext);

        result.Metadata.ShouldBe(new ProjectMetadata("Rust", "Cargo"));
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Interface && n.Name == "Greeter");
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Struct && n.Name == "ConsoleGreeter");
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Method && n.Name == "greet");
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Function && n.Name == "helper");

        var methodQN = $"DemoRust:scip:{methodSymbol}";
        var helperQN = $"DemoRust:scip:{helperSymbol}";
        var structQN = $"DemoRust:scip:{structSymbol}";
        var traitQN = $"DemoRust:scip:{traitSymbol}";

        result.Edges.ShouldContain(e =>
            e.SourceQN == "DemoRust:src/lib.rs" &&
            e.TargetQN == methodQN &&
            e.Type == EdgeType.DEFINES_METHOD);
        result.Edges.ShouldContain(e =>
            e.SourceQN == methodQN &&
            e.TargetQN == helperQN &&
            e.Type == EdgeType.CALLS);
        result.Edges.ShouldContain(e =>
            e.SourceQN == helperQN &&
            e.TargetQN == structQN &&
            e.Type == EdgeType.USES_TYPE);
        result.Edges.ShouldContain(e =>
            e.SourceQN == structQN &&
            e.TargetQN == traitQN &&
            e.Type == EdgeType.IMPLEMENTS);
    }

    [Fact]
    public void Import_IgnoresNonRustDocuments()
    {
        const string json = """
            {
              "documents": [
                {
                  "language": "python",
                  "relativePath": "src/app.py",
                  "symbols": [
                    { "symbol": "python app/main().", "kind": "Function", "displayName": "main" }
                  ],
                  "occurrences": [
                    { "symbol": "python app/main().", "symbolRoles": 1, "range": [0, 4, 8] }
                  ]
                }
              ]
            }
            """;

        var result = ScipJsonImporter.Import(json, TestContext);

        result.Nodes.ShouldBeEmpty();
        result.Edges.ShouldBeEmpty();
        result.Metadata.ShouldBe(new ProjectMetadata("Rust", "Cargo"));
    }
}
