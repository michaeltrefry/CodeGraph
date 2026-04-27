using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using CodeGraph.Extractors.ColdFusion;
using CodeGraph.Models;

namespace CodeGraph.Tests.Extractors;

public class ColdFusionExtractorTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    private static async Task<ExtractionResult> ExtractCfmAsync(string content, string fileName = "page.cfm")
    {
        var extractor = new ColdFusionExtractor(NullLogger<ColdFusionExtractor>.Instance);
        return await extractor.ExtractAsync($"/test/{fileName}", content, TestContext);
    }

    [Fact]
    public async Task SupportedExtensions_IncludesCfmAndCfc()
    {
        var extractor = new ColdFusionExtractor(NullLogger<ColdFusionExtractor>.Instance);
        extractor.SupportedExtensions.ShouldContain(".cfm");
        extractor.SupportedExtensions.ShouldContain(".cfc");
    }

    // -- Component extraction --------------------------------------

    [Fact]
    public async Task Extracts_CfcComponent_WithNameAttribute()
    {
        var cfc = """
            <cfcomponent name="OrderService" extends="BaseService">
                <cffunction name="getOrder" access="public" returntype="struct">
                </cffunction>
            </cfcomponent>
            """;

        var result = await ExtractCfmAsync(cfc, "OrderService.cfc");

        var component = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Component);
        component.Name.ShouldBe("OrderService");
        component.Properties["language"].ShouldBe("coldfusion");

        // Should have INHERITS edge to BaseService
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INHERITS &&
            e.TargetQN == "TestProject.BaseService");
    }

    [Fact]
    public async Task Extracts_CfcComponent_WithoutNameAttribute_UsesFilename()
    {
        var cfc = """
            <cfcomponent>
                <cffunction name="init" access="public" returntype="void">
                </cffunction>
            </cfcomponent>
            """;

        var result = await ExtractCfmAsync(cfc, "UserManager.cfc");

        var component = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Component);
        component.Name.ShouldBe("UserManager");
    }

    [Fact]
    public async Task Extracts_CfScriptComponent()
    {
        var cfc = """
            component extends="BaseComponent" {

                public string function getName() {
                    return "test";
                }
            }
            """;

        var result = await ExtractCfmAsync(cfc, "MyComponent.cfc");

        var component = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Component);
        component.Name.ShouldBe("MyComponent");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INHERITS &&
            e.TargetQN == "TestProject.BaseComponent");
    }

    // -- Function extraction ---------------------------------------

    [Fact]
    public async Task Extracts_CfFunctionTag()
    {
        var cfc = """
            <cfcomponent name="OrderService">
                <cffunction name="getOrder" access="public" returntype="query">
                </cffunction>
                <cffunction name="saveOrder" access="private" returntype="void">
                </cffunction>
            </cfcomponent>
            """;

        var result = await ExtractCfmAsync(cfc, "OrderService.cfc");

        var functions = result.Nodes.Where(n => n.Label == NodeLabel.Function).ToList();
        functions.Count.ShouldBe(2);

        var getOrder = functions.Single(f => f.Name == "getOrder");
        getOrder.Properties["access"].ShouldBe("public");
        getOrder.Properties["return_type"].ShouldBe("query");

        var saveOrder = functions.Single(f => f.Name == "saveOrder");
        saveOrder.Properties["access"].ShouldBe("private");
    }

    [Fact]
    public async Task Extracts_CfScriptFunction()
    {
        var cfc = """
            component {
                public string function getName() {
                    return variables.name;
                }

                private void function setName(required string name) {
                    variables.name = arguments.name;
                }
            }
            """;

        var result = await ExtractCfmAsync(cfc, "Person.cfc");

        var functions = result.Nodes.Where(n => n.Label == NodeLabel.Function).ToList();
        functions.Count.ShouldBe(2);
        functions.ShouldContain(f => f.Name == "getName");
        functions.ShouldContain(f => f.Name == "setName");
    }

    [Fact]
    public async Task Creates_DefinesMethodEdge_ForComponentFunctions()
    {
        var cfc = """
            <cfcomponent name="OrderService">
                <cffunction name="getOrder" access="public" returntype="query">
                </cffunction>
            </cfcomponent>
            """;

        var result = await ExtractCfmAsync(cfc, "OrderService.cfc");

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.DEFINES_METHOD &&
            e.SourceQN == "TestProject.OrderService" &&
            e.TargetQN.Contains("getOrder"));
    }

    // -- cfinvoke --------------------------------------------------

    [Fact]
    public async Task Extracts_CfInvoke()
    {
        var cfm = """
            <cfinvoke component="com.myapp.OrderService" method="getOrder" returnvariable="order">
            """;

        var result = await ExtractCfmAsync(cfm);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.CALLS &&
            e.TargetQN == "TestProject.com.myapp.OrderService.getOrder");

        result.UnresolvedCalls.ShouldContain(c =>
            c.CalleeName == "com.myapp.OrderService.getOrder");
    }

    // -- createObject ----------------------------------------------

    [Fact]
    public async Task Extracts_CreateObject()
    {
        var cfm = """
            <cfscript>
                orderService = createObject("component", "com.myapp.OrderService");
            </cfscript>
            """;

        var result = await ExtractCfmAsync(cfm);

        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.USES_TYPE &&
            e.TargetQN == "TestProject.com.myapp.OrderService");
    }

    // -- cfhttp ----------------------------------------------------

    [Fact]
    public async Task Extracts_CfHttp()
    {
        var cfm = """
            <cfhttp url="https://api.example.com/orders" method="POST">
                <cfhttpparam type="body" value="#orderJson#">
            </cfhttp>
            """;

        var result = await ExtractCfmAsync(cfm);

        var route = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Route);
        route.Properties["http_method"].ShouldBe("POST");
        route.Properties["url"].ShouldBe("https://api.example.com/orders");
        route.Properties["direction"].ShouldBe("outbound");

        result.Edges.ShouldContain(e => e.Type == EdgeType.HTTP_CALLS);
    }

    [Fact]
    public async Task Extracts_CfHttp_DefaultsToGet()
    {
        var cfm = """
            <cfhttp url="https://api.example.com/status">
            """;

        var result = await ExtractCfmAsync(cfm);

        var route = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Route);
        route.Properties["http_method"].ShouldBe("GET");
    }

    // -- cfquery ---------------------------------------------------

    [Fact]
    public async Task Extracts_CfQuery_TableReferences()
    {
        var cfm = """
            <cfquery name="qOrders" datasource="myDB">
                SELECT o.OrderId, c.Name
                FROM Orders o
                INNER JOIN Customers c ON o.CustomerId = c.CustomerId
                WHERE o.Status = 'Active'
            </cfquery>
            """;

        var result = await ExtractCfmAsync(cfm);

        var queryEdges = result.Edges.Where(e => e.Type == EdgeType.QUERIES).ToList();
        queryEdges.ShouldContain(e => e.TargetQN == "TestProject.Orders");
        queryEdges.ShouldContain(e => e.TargetQN == "TestProject.Customers");
    }

    [Fact]
    public async Task CfQuery_SkipsSqlKeywords()
    {
        var cfm = """
            <cfquery name="q">
                SELECT * FROM Orders WHERE Status IN (SELECT Status FROM StatusLookup)
            </cfquery>
            """;

        var result = await ExtractCfmAsync(cfm);

        var queryEdges = result.Edges.Where(e => e.Type == EdgeType.QUERIES).ToList();
        queryEdges.ShouldContain(e => e.TargetQN == "TestProject.Orders");
        queryEdges.ShouldContain(e => e.TargetQN == "TestProject.StatusLookup");
        // Should NOT have edges to SQL keywords like SELECT, WHERE, IN
        queryEdges.ShouldNotContain(e => e.TargetQN.Contains("SELECT"));
    }

    // -- cfinclude -------------------------------------------------

    [Fact]
    public async Task Extracts_CfInclude()
    {
        var cfm = """
            <cfinclude template="/includes/header.cfm">
            <h1>Hello</h1>
            <cfinclude template="/includes/footer.cfm">
            """;

        var result = await ExtractCfmAsync(cfm);

        var importEdges = result.Edges.Where(e => e.Type == EdgeType.IMPORTS).ToList();
        importEdges.Count.ShouldBe(2);
        importEdges.ShouldContain(e => e.Properties!["template_path"].Equals("/includes/header.cfm"));
        importEdges.ShouldContain(e => e.Properties!["template_path"].Equals("/includes/footer.cfm"));
    }

    // -- Complex CFC -----------------------------------------------

    [Fact]
    public async Task Extracts_ComplexCfc_WithMultiplePatterns()
    {
        var cfc = """
            <cfcomponent name="OrderProcessor" extends="BaseProcessor">
                <cffunction name="processOrder" access="public" returntype="struct">
                    <cfargument name="orderId" type="numeric" required="true">

                    <cfquery name="qOrder" datasource="appDB">
                        SELECT * FROM Orders WHERE OrderId = <cfqueryparam value="#arguments.orderId#">
                    </cfquery>

                    <cfinvoke component="com.myapp.NotificationService" method="sendConfirmation"
                        returnvariable="notifyResult">

                    <cfhttp url="https://shipping.example.com/api/ship" method="POST">
                        <cfhttpparam type="body" value="#serializeJSON(qOrder)#">
                    </cfhttp>

                    <cfreturn { success: true }>
                </cffunction>
            </cfcomponent>
            """;

        var result = await ExtractCfmAsync(cfc, "OrderProcessor.cfc");

        // Component
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Component && n.Name == "OrderProcessor");

        // Function
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Function && n.Name == "processOrder");

        // INHERITS
        result.Edges.ShouldContain(e => e.Type == EdgeType.INHERITS);

        // QUERIES (from cfquery)
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.QUERIES && e.TargetQN == "TestProject.Orders");

        // CALLS (from cfinvoke)
        result.Edges.ShouldContain(e => e.Type == EdgeType.CALLS);

        // HTTP_CALLS (from cfhttp)
        result.Edges.ShouldContain(e => e.Type == EdgeType.HTTP_CALLS);
    }

    // -- CFM template (not CFC) ------------------------------------

    [Fact]
    public async Task Extracts_CfmTemplate_WithoutComponent()
    {
        var cfm = """
            <cfquery name="qProducts" datasource="appDB">
                SELECT * FROM Products WHERE Active = 1
            </cfquery>

            <cfoutput query="qProducts">
                <tr><td>#Name#</td></tr>
            </cfoutput>
            """;

        var result = await ExtractCfmAsync(cfm, "products.cfm");

        // No component node (it's a .cfm, not .cfc)
        result.Nodes.ShouldNotContain(n => n.Label == NodeLabel.Component);

        // But should still have QUERIES edges
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.QUERIES && e.TargetQN == "TestProject.Products");
    }
}
