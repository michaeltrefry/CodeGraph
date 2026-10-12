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

    [Fact]
    public void Import_ResolvesLocalReferencesAcrossDocumentsRegardlessOfDocumentOrder()
    {
        const string callerSymbol = "rust-analyzer cargo demo 0.1.0 demo/caller().";
        const string targetSymbol = "rust-analyzer cargo demo 0.1.0 demo/target().";

        var json = $$"""
            {
              "documents": [
                {
                  "language": "rust",
                  "relativePath": "src/caller.rs",
                  "symbols": [
                    { "symbol": "{{callerSymbol}}", "kind": "Function", "displayName": "caller" }
                  ],
                  "occurrences": [
                    {
                      "symbol": "{{callerSymbol}}",
                      "symbolRoles": 1,
                      "range": [0, 7, 13],
                      "multiLineEnclosingRange": {
                        "startLine": 0, "startCharacter": 0,
                        "endLine": 2, "endCharacter": 1
                      }
                    },
                    { "symbol": "{{targetSymbol}}", "symbolRoles": 8, "range": [1, 4, 10] }
                  ]
                },
                {
                  "language": "rust",
                  "relativePath": "src/target.rs",
                  "symbols": [
                    { "symbol": "{{targetSymbol}}", "kind": "Function", "displayName": "target" }
                  ],
                  "occurrences": [
                    {
                      "symbol": "{{targetSymbol}}",
                      "symbolRoles": 1,
                      "range": [0, 7, 13],
                      "singleLineEnclosingRange": {
                        "startLine": 0, "startCharacter": 0, "endCharacter": 15
                      }
                    }
                  ]
                }
              ]
            }
            """;

        var result = ScipJsonImporter.Import(json, TestContext);

        result.Edges.ShouldContain(edge =>
            edge.SourceQN == $"DemoRust:scip:{callerSymbol}" &&
            edge.TargetQN == $"DemoRust:scip:{targetSymbol}" &&
            edge.Type == EdgeType.CALLS);
        result.Nodes.ShouldNotContain(node => node.Label == NodeLabel.ExternalSymbol);
    }

    [Fact]
    public void Import_PreservesExternalSymbolsAndSemanticEdgesForCrossRepoLinking()
    {
        const string callerSymbol = "rust-analyzer cargo consumer 0.1.0 consumer/run().";
        const string externalSymbol = "rust-analyzer cargo provider 1.2.3 provider/process().";

        var json = $$"""
            {
              "externalSymbols": [
                {
                  "symbol": "{{externalSymbol}}",
                  "kind": "Function",
                  "displayName": "process",
                  "documentation": ["Processes one message."]
                }
              ],
              "documents": [
                {
                  "language": "rust",
                  "relativePath": "src/lib.rs",
                  "symbols": [
                    { "symbol": "{{callerSymbol}}", "kind": "Function", "displayName": "run" }
                  ],
                  "occurrences": [
                    {
                      "symbol": "{{callerSymbol}}",
                      "symbolRoles": 1,
                      "range": [0, 7, 10],
                      "multiLineEnclosingRange": {
                        "startLine": 0, "startCharacter": 0,
                        "endLine": 2, "endCharacter": 1
                      }
                    },
                    { "symbol": "{{externalSymbol}}", "symbolRoles": 8, "range": [1, 4, 11] }
                  ]
                }
              ]
            }
            """;

        var result = ScipJsonImporter.Import(json, TestContext);

        var external = result.Nodes.Single(node => node.Label == NodeLabel.ExternalSymbol);
        external.Name.ShouldBe("process");
        external.Properties["scip_symbol"].ShouldBe(externalSymbol);
        external.Properties["scip_external"].ShouldBe(true);
        result.Edges.ShouldContain(edge =>
            edge.SourceQN == $"DemoRust:scip:{callerSymbol}" &&
            edge.TargetQN == external.QualifiedName &&
            edge.Type == EdgeType.CALLS &&
            edge.Properties!["scip_symbol"].ToString() == externalSymbol);
    }

    [Fact]
    public void Import_AcceptsCanonicalScipPrintSnakeCaseAndPackedEnclosingRanges()
    {
        const string callerSymbol = "rust-analyzer cargo consumer 0.1.0 caller().";
        const string externalSymbol = "rust-analyzer cargo provider 1.0.0 process().";
        var json = $$"""
            {
              "external_symbols": [
                {
                  "symbol": "{{externalSymbol}}",
                  "kind": 17,
                  "display_name": "process",
                  "signature_documentation": {
                    "language": "rust",
                    "text": "pub fn process()"
                  }
                }
              ],
              "documents": [
                {
                  "language": "rust",
                  "relative_path": "src/lib.rs",
                  "occurrences": [
                    {
                      "range": [0, 7, 13],
                      "symbol": "{{callerSymbol}}",
                      "symbol_roles": 1,
                      "enclosing_range": [0, 0, 2, 1]
                    },
                    {
                      "range": [1, 4, 11],
                      "symbol": "{{externalSymbol}}"
                    }
                  ],
                  "symbols": [
                    {
                      "symbol": "{{callerSymbol}}",
                      "kind": 17,
                      "display_name": "caller"
                    }
                  ]
                }
              ]
            }
            """;

        var result = ScipJsonImporter.Import(json, TestContext);

        var external = result.Nodes.Single(node => node.Label == NodeLabel.ExternalSymbol);
        external.Properties["scip_kind"].ShouldBe("Function");
        external.Properties["signature"].ShouldBe("pub fn process()");
        result.Edges.ShouldContain(edge =>
            edge.SourceQN == $"DemoRust:scip:{callerSymbol}" &&
            edge.TargetQN == external.QualifiedName &&
            edge.Type == EdgeType.CALLS);
    }

    [Fact]
    public void Import_ScopesDocumentLocalSymbolsToTheirDocument()
    {
        const string outerA = "rust-analyzer cargo demo 0.1.0 demo/a/outer_a().";
        const string outerB = "rust-analyzer cargo demo 0.1.0 demo/b/outer_b().";
        const string traitSymbol = "rust-analyzer cargo demo 0.1.0 demo/Shape#";

        // Both documents define `local 0` (a nested fn) and call it. src/b.rs also defines
        // `local 1` (a nested struct implementing a crate trait); src/a.rs references a `local 1`
        // it never defines (a variable) and must not reach b.rs's struct.
        var json = $$"""
            {
              "documents": [
                {
                  "language": "rust",
                  "relativePath": "src/a.rs",
                  "symbols": [
                    { "symbol": "{{outerA}}", "kind": "Function", "displayName": "outer_a" },
                    { "symbol": "local 0", "kind": "Function", "displayName": "inner_a" },
                    { "symbol": "local 1", "kind": "Variable", "displayName": "count" }
                  ],
                  "occurrences": [
                    { "symbol": "{{outerA}}", "symbolRoles": 1, "range": [0, 7, 14], "enclosingRange": [0, 0, 9, 1] },
                    { "symbol": "local 0", "symbolRoles": 1, "range": [1, 7, 14], "enclosingRange": [1, 4, 3, 5] },
                    { "symbol": "local 0", "symbolRoles": 8, "range": [5, 4, 11] },
                    { "symbol": "local 1", "symbolRoles": 8, "range": [6, 4, 9] }
                  ]
                },
                {
                  "language": "rust",
                  "relativePath": "src/b.rs",
                  "symbols": [
                    { "symbol": "{{outerB}}", "kind": "Function", "displayName": "outer_b" },
                    { "symbol": "local 0", "kind": "Function", "displayName": "inner_b" },
                    {
                      "symbol": "local 1",
                      "kind": "Struct",
                      "displayName": "Square",
                      "relationships": [ { "symbol": "{{traitSymbol}}", "isImplementation": true } ]
                    }
                  ],
                  "occurrences": [
                    { "symbol": "{{outerB}}", "symbolRoles": 1, "range": [0, 7, 14], "enclosingRange": [0, 0, 9, 1] },
                    { "symbol": "local 0", "symbolRoles": 1, "range": [1, 7, 14], "enclosingRange": [1, 4, 3, 5] },
                    { "symbol": "local 1", "symbolRoles": 1, "range": [4, 11, 17], "enclosingRange": [4, 4, 4, 18] },
                    { "symbol": "local 0", "symbolRoles": 8, "range": [6, 4, 11] }
                  ]
                }
              ]
            }
            """;

        var result = ScipJsonImporter.Import(json, TestContext);

        var innerA = result.Nodes.Where(node => node.Name == "inner_a").ShouldHaveSingleItem();
        var innerB = result.Nodes.Where(node => node.Name == "inner_b").ShouldHaveSingleItem();
        var square = result.Nodes.Where(node => node.Name == "Square").ShouldHaveSingleItem();
        innerA.QualifiedName.ShouldNotBe(innerB.QualifiedName);
        innerA.FilePath.ShouldBe("src/a.rs");
        innerB.FilePath.ShouldBe("src/b.rs");
        // Linkers join on scip_symbol; a document-local symbol must not carry it.
        new[] { innerA, innerB, square }.ShouldAllBe(node => !node.Properties.ContainsKey("scip_symbol"));

        result.Edges.ShouldContain(edge =>
            edge.SourceQN == $"DemoRust:scip:{outerA}" && edge.TargetQN == innerA.QualifiedName &&
            edge.Type == EdgeType.CALLS);
        result.Edges.ShouldContain(edge =>
            edge.SourceQN == $"DemoRust:scip:{outerB}" && edge.TargetQN == innerB.QualifiedName &&
            edge.Type == EdgeType.CALLS);
        result.Edges.ShouldContain(edge =>
            edge.SourceQN == square.QualifiedName && edge.Type == EdgeType.IMPLEMENTS);
        // No a.rs reference reaches a b.rs local, and no unresolved local becomes a placeholder.
        result.Edges.Where(edge => edge.SourceQN == $"DemoRust:scip:{outerA}")
            .Select(edge => edge.TargetQN)
            .ShouldBe([innerA.QualifiedName]);
        result.Nodes.ShouldNotContain(node => node.Label == NodeLabel.ExternalSymbol &&
                                              node.QualifiedName.Contains("local "));
    }
}
