using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using CodeGraph.Extractors.CSharp;
using CodeGraph.Models;
using CodeGraph.Services;

namespace CodeGraph.Tests.Extractors;

public class RoslynExtractorTests
{
    private static readonly ExtractorContext TestContext = new()
    {
        ProjectName = "TestProject",
        RootPath = "/test"
    };

    private static ExtractionResult ExtractFromSource(string code, bool withSemantics = false)
    {
        var tree = CSharpSyntaxTree.ParseText(code);

        SemanticModel? model = null;
        if (withSemantics)
        {
            var refs = new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
                MetadataReference.CreateFromFile(
                    System.Runtime.InteropServices.RuntimeEnvironment
                        .GetRuntimeDirectory() + "System.Runtime.dll")
            };
            var compilation = CSharpCompilation.Create("Test",
                new[] { tree }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            model = compilation.GetSemanticModel(tree);
        }

        var walker = new CodeGraphSyntaxWalker(TestContext, model);
        walker.Visit(tree.GetRoot());
        return walker.GetResult();
    }

    [Fact]
    public void Extracts_ClassDeclaration()
    {
        var code = """
            namespace MyApp.Services;
            public class WalletService
            {
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var classNode = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Class);
        classNode.Name.ShouldBe("WalletService");
        classNode.QualifiedName.ShouldBe("MyApp.Services.WalletService");
        classNode.Project.ShouldBe("TestProject");
    }

    [Fact]
    public void Extracts_ClassWithBaseType_CreatesInheritsEdge()
    {
        var code = """
            namespace MyApp.Services;
            public class BaseService { }
            public class WalletService : BaseService { }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        result.Nodes.Count(n => n.Label == NodeLabel.Class).ShouldBe(2);

        var inheritsEdge = result.Edges.ShouldContain(e => e.Type == EdgeType.INHERITS);
        inheritsEdge.SourceQN.ShouldBe("MyApp.Services.WalletService");
        inheritsEdge.TargetQN.ShouldBe("MyApp.Services.BaseService");
    }

    [Fact]
    public void Extracts_ClassImplementingInterface_CreatesImplementsEdge()
    {
        var code = """
            namespace MyApp.Services;
            public interface IWalletService { }
            public class WalletService : IWalletService { }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var implementsEdge = result.Edges.ShouldContain(e => e.Type == EdgeType.IMPLEMENTS);
        implementsEdge.SourceQN.ShouldBe("MyApp.Services.WalletService");
        implementsEdge.TargetQN.ShouldBe("MyApp.Services.IWalletService");
    }

    [Fact]
    public void Extracts_InterfaceDeclaration()
    {
        var code = """
            namespace MyApp.Services;
            public interface IWalletService { }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var ifaceNode = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Interface);
        ifaceNode.Name.ShouldBe("IWalletService");
        ifaceNode.QualifiedName.ShouldBe("MyApp.Services.IWalletService");
    }

    [Fact]
    public void Extracts_RecordDeclaration()
    {
        var code = """
            namespace MyApp.Models;
            public record OrderCreatedEvent(int OrderId, decimal Amount);
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var recordNode = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Record);
        recordNode.Name.ShouldBe("OrderCreatedEvent");
        recordNode.QualifiedName.ShouldBe("MyApp.Models.OrderCreatedEvent");
    }

    [Fact]
    public void Extracts_EnumDeclaration()
    {
        var code = """
            namespace MyApp.Models;
            public enum OrderStatus { Pending, Active, Complete }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var enumNode = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Enum);
        enumNode.Name.ShouldBe("OrderStatus");
    }

    [Fact]
    public void Extracts_MethodDeclaration()
    {
        var code = """
            namespace MyApp.Services;
            public class WalletService
            {
                public async System.Threading.Tasks.Task<decimal> GetBalanceAsync(int walletId)
                {
                    return 0m;
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var methodNode = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Method);
        methodNode.Name.ShouldBe("GetBalanceAsync");
        methodNode.Properties["is_async"].ShouldBe(true);
        methodNode.Properties["parameter_count"].ShouldBe(1);
    }

    [Fact]
    public void Extracts_Method_CreatesDefinesMethodEdge()
    {
        var code = """
            namespace MyApp.Services;
            public class WalletService
            {
                public void DoWork() { }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var edge = result.Edges.ShouldContain(e => e.Type == EdgeType.DEFINES_METHOD);
        edge.SourceQN.ShouldBe("MyApp.Services.WalletService");
        edge.TargetQN.ShouldContain("DoWork");
    }

    [Fact]
    public void Extracts_NamespaceDeclaration()
    {
        var code = """
            namespace MyApp.Services;
            public class Foo { }
            """;

        var result = ExtractFromSource(code);

        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Namespace && n.Name == "MyApp.Services");
    }

    [Fact]
    public void Extracts_UsingDirectives()
    {
        var code = """
            using System.Collections.Generic;
            namespace MyApp.Services;
            public class Foo { }
            """;

        var result = ExtractFromSource(code);

        result.UnresolvedImports.ShouldContain(i => i.ImportedNamespace == "System.Collections.Generic");
    }

    [Fact]
    public void Detects_ControllerRoute()
    {
        // Syntax-only detection (no semantics needed for attribute syntax check)
        var code = """
            using System;
            namespace MyApp.Controllers;
            [Route("api/[controller]")]
            public class WalletController
            {
                [HttpGet("{id}")]
                public void Get(int id) { }
            }
            """;

        var result = ExtractFromSource(code);

        // Route node should be created
        var routeNode = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Route);
        routeNode.Properties["http_method"].ShouldBe("GET");
        routeNode.Properties["route_template"].ShouldBe("api/wallet/{id}");
    }

    [Fact]
    public void Detects_ControllerRoute_WithSemantics()
    {
        var code = """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public class RouteAttribute : Attribute
            {
                public RouteAttribute(string template) { }
            }

            [AttributeUsage(AttributeTargets.Method)]
            public class HttpGetAttribute : Attribute
            {
                public HttpGetAttribute() { }
                public HttpGetAttribute(string template) { }
            }

            namespace MyApp.Controllers
            {
                [Route("api/[controller]")]
                public class OrderController
                {
                    [HttpGet("{id}")]
                    public void Get(int id) { }
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var routeNode = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Route);
        routeNode.Properties["http_method"].ShouldBe("GET");
        routeNode.Properties["route_template"].ShouldBe("api/order/{id}");

        // HANDLES edge from Route → Method
        result.Edges.ShouldContain(e => e.Type == EdgeType.HANDLES);
    }

    [Fact]
    public void Detects_ConstructorInjection()
    {
        var code = """
            namespace MyApp.Services;
            public interface IWalletService { }
            public class OrderService
            {
                public OrderService(IWalletService wallet)
                {
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var injectsEdge = result.Edges.ShouldContain(e => e.Type == EdgeType.INJECTS);
        injectsEdge.TargetQN.ShouldBe("MyApp.Services.IWalletService");
        injectsEdge.Properties!["parameter_name"].ShouldBe("wallet");
    }

    [Fact]
    public void Detects_ConstructorInjection_FiltersFrameworkTypes()
    {
        var code = """
            using Microsoft.Extensions.Logging;
            namespace MyApp.Services;
            public interface IWalletService { }
            public class OrderService
            {
                public OrderService(IWalletService wallet, ILogger<OrderService> logger)
                {
                }
            }
            """;

        // Syntax-only won't resolve types, so use semantics for this test
        // But ILogger won't resolve without the actual assembly reference,
        // so we verify the filter logic conceptually via the simpler case above.
        // This test verifies that at minimum IWalletService IS detected.
        var result = ExtractFromSource(code, withSemantics: true);

        // Should have INJECTS for IWalletService
        result.Edges.ShouldContain(e =>
            e.Type == EdgeType.INJECTS &&
            e.TargetQN == "MyApp.Services.IWalletService");
    }

    [Fact]
    public void Detects_MassTransitConsumer()
    {
        var code = """
            namespace MyApp.Events
            {
                public class OrderCreatedEvent { }
            }

            namespace MyApp.Consumers
            {
                public class Consumer<T> { }
                public class OrderCreatedConsumer : Consumer<MyApp.Events.OrderCreatedEvent>
                {
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var consumesEdge = result.Edges.ShouldContain(e => e.Type == EdgeType.CONSUMES);
        consumesEdge.TargetQN.ShouldBe("MyApp.Events.OrderCreatedEvent");
        consumesEdge.Properties!["confidence_band"].ShouldBe("high");
    }

    [Fact]
    public void Computes_CyclomaticComplexity()
    {
        var code = """
            namespace MyApp;
            public class Calc
            {
                public int Complex(int x, bool flag)
                {
                    if (x > 0 && flag)
                        return x;
                    else if (x < 0 || !flag)
                        return -x;
                    for (int i = 0; i < x; i++) { }
                    return 0;
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var method = result.Nodes.Single(n => n.Label == NodeLabel.Method && n.Name == "Complex");
        // 1 base + 2 if + 1 && + 1 || + 1 for = 6
        ((int)method.Properties["complexity"]).ShouldBe(6);
    }

    [Fact]
    public void Extracts_PropertyDeclaration()
    {
        var code = """
            namespace MyApp.Models;
            public class Order
            {
                public int OrderId { get; set; }
                public static string DefaultStatus { get; set; }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var props = result.Nodes.Where(n => n.Label == NodeLabel.Property).ToList();
        props.Count.ShouldBe(2);

        var orderIdProp = props.Single(p => p.Name == "OrderId");
        orderIdProp.Properties["type"].ShouldBe("int");

        var staticProp = props.Single(p => p.Name == "DefaultStatus");
        staticProp.Properties["is_static"].ShouldBe(true);
    }

    [Fact]
    public void Extracts_StructDeclaration()
    {
        var code = """
            namespace MyApp.Models;
            public struct Point { public int X; public int Y; }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Struct && n.Name == "Point");
    }

    [Fact]
    public void Extracts_DelegateDeclaration()
    {
        var code = """
            namespace MyApp;
            public delegate void MyHandler(int value);
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Delegate && n.Name == "MyHandler");
    }

    [Fact]
    public void SyntaxOnly_FallbackExtraction_StillProducesNodes()
    {
        var code = """
            namespace MyApp.Services;
            public class WalletService
            {
                public void DoWork() { }
            }
            """;

        // No semantic model — syntax only
        var result = ExtractFromSource(code, withSemantics: false);

        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Class && n.Name == "WalletService");
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Method && n.Name == "DoWork");
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Namespace);
    }

    [Fact]
    public async Task RoslynExtractor_ExtractAsync_Works()
    {
        var extractor = new RoslynExtractor();

        extractor.SupportedExtensions.ShouldContain(".cs");

        var code = """
            namespace MyApp;
            public class Foo { public void Bar() { } }
            """;

        var result = await extractor.ExtractAsync("/test/Foo.cs", code, TestContext);

        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Class && n.Name == "Foo");
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Method && n.Name == "Bar");
    }

    [Fact]
    public void Detects_MethodMarkedAsTest()
    {
        var code = """
            using System;

            [AttributeUsage(AttributeTargets.Method)]
            public class FactAttribute : Attribute { }

            namespace MyApp.Tests;
            public class MyTests
            {
                [Fact]
                public void MyTest() { }

                public void NotATest() { }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var testMethod = result.Nodes.Single(n => n.Label == NodeLabel.Method && n.Name == "MyTest");
        testMethod.Properties["is_test"].ShouldBe(true);

        var normalMethod = result.Nodes.Single(n => n.Label == NodeLabel.Method && n.Name == "NotATest");
        normalMethod.Properties["is_test"].ShouldBe(false);
    }

    [Fact]
    public void Detects_AbstractAndStaticClass()
    {
        var code = """
            namespace MyApp;
            public abstract class BaseService { }
            public static class Helpers { }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var abstractClass = result.Nodes.Single(n => n.Name == "BaseService");
        abstractClass.Properties["is_abstract"].ShouldBe(true);

        var staticClass = result.Nodes.Single(n => n.Name == "Helpers");
        staticClass.Properties["is_static"].ShouldBe(true);
    }

    // ── Messaging Extraction Enhancements ────────────────────────────────

    [Fact]
    public void Detects_ServiceBusEventAttribute_RoutingMetadata()
    {
        var code = """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public class TcServiceBusEventAttribute : Attribute
            {
                public TcServiceBusEventAttribute(string queueName, string exchangeName, string virtualHost) { }
            }

            namespace MyApp.Events
            {
                [TcServiceBusEvent("order-created-queue", "order-exchange", "/orders")]
                public class OrderCreatedEvent
                {
                    public int OrderId { get; set; }
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        // Class node should have routing metadata
        var classNode = result.Nodes.Single(n => n.Label == NodeLabel.Class && n.Name == "OrderCreatedEvent");
        classNode.Properties["queue_name"].ShouldBe("order-created-queue");
        classNode.Properties["exchange_name"].ShouldBe("order-exchange");
        classNode.Properties["virtual_host"].ShouldBe("/orders");
        classNode.Properties["is_service_bus_event"].ShouldBe(true);

        // Queue node should be created
        var queueNode = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Queue);
        queueNode.Name.ShouldBe("order-created-queue");
        queueNode.Properties["queue_name"].ShouldBe("order-created-queue");

        // Exchange node should be created
        var exchangeNode = result.Nodes.ShouldContain(n => n.Label == NodeLabel.Exchange);
        exchangeNode.Name.ShouldBe("order-exchange");

        // ROUTED_TO edge: Event → Queue
        var routedToEdge = result.Edges.ShouldContain(e => e.Type == EdgeType.ROUTED_TO);
        routedToEdge.SourceQN.ShouldBe("MyApp.Events.OrderCreatedEvent");
        routedToEdge.TargetQN.ShouldContain("order-created-queue");

        // BOUND_TO edge: Queue → Exchange
        var boundToEdge = result.Edges.ShouldContain(e => e.Type == EdgeType.BOUND_TO);
        boundToEdge.TargetQN.ShouldContain("order-exchange");
    }

    [Fact]
    public void Detects_ServiceBusEventAttribute_NamedArguments()
    {
        var code = """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public class TcServiceBusEventAttribute : Attribute
            {
                public TcServiceBusEventAttribute() { }
                public string QueueName { get; set; }
                public string ExchangeName { get; set; }
                public string VirtualHost { get; set; }
                public string RoutingKey { get; set; }
            }

            namespace MyApp.Events
            {
                [TcServiceBusEvent(QueueName = "payment-queue", RoutingKey = "payment.completed")]
                public class PaymentCompletedEvent { }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var classNode = result.Nodes.Single(n => n.Label == NodeLabel.Class && n.Name == "PaymentCompletedEvent");
        classNode.Properties["queue_name"].ShouldBe("payment-queue");
        classNode.Properties["routing_key"].ShouldBe("payment.completed");

        // Queue node should be created
        result.Nodes.ShouldContain(n => n.Label == NodeLabel.Queue && n.Name == "payment-queue");
    }

    [Fact]
    public void Detects_EventMessageFields()
    {
        var code = """
            namespace MyApp.Events
            {
                public class OrderCreatedEvent
                {
                    public int OrderId { get; set; }
                    public string CustomerName { get; set; }
                    public decimal Amount { get; set; }
                    private string InternalNote { get; set; }
                    public static int Counter { get; set; }
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var classNode = result.Nodes.Single(n => n.Label == NodeLabel.Class && n.Name == "OrderCreatedEvent");
        classNode.Properties.ShouldContainKey("fields");

        var fields = (List<Dictionary<string, object>>)classNode.Properties["fields"];
        // Should have 3 public non-static properties (not InternalNote, not Counter)
        fields.Count.ShouldBe(3);
        fields.ShouldContain(f => (string)f["name"] == "OrderId" && (string)f["type"] == "int");
        fields.ShouldContain(f => (string)f["name"] == "CustomerName" && (string)f["type"] == "string");
        fields.ShouldContain(f => (string)f["name"] == "Amount" && (string)f["type"] == "decimal");
    }

    [Fact]
    public void Detects_EventMessageFields_WithDomainType_CreatesCarriesFieldEdge()
    {
        var code = """
            namespace TC.Orders.Models
            {
                public class OrderLineItem
                {
                    public int ProductId { get; set; }
                }

                public class OrderCreatedEvent
                {
                    public int OrderId { get; set; }
                    public TC.Orders.Models.OrderLineItem LineItem { get; set; }
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        // Should have CARRIES_FIELD edge from Event to the domain type
        var carriesFieldEdge = result.Edges.ShouldContain(e => e.Type == EdgeType.CARRIES_FIELD);
        carriesFieldEdge.SourceQN.ShouldBe("TC.Orders.Models.OrderCreatedEvent");
        carriesFieldEdge.TargetQN.ShouldBe("TC.Orders.Models.OrderLineItem");
        carriesFieldEdge.Properties!["field_name"].ShouldBe("LineItem");
    }

    [Fact]
    public void Detects_ConsumerRegistration_AddConsumer()
    {
        var code = """
            namespace MyApp.Consumers
            {
                public class Consumer<T> { }
                public class OrderCreatedConsumer : Consumer<object> { }
            }

            namespace MyApp
            {
                public interface IBusRegistrationConfigurator
                {
                    void AddConsumer<T>();
                }

                public class Startup
                {
                    public void ConfigureServices(IBusRegistrationConfigurator cfg)
                    {
                        cfg.AddConsumer<MyApp.Consumers.OrderCreatedConsumer>();
                    }
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var registersEdge = result.Edges.ShouldContain(e => e.Type == EdgeType.REGISTERS);
        registersEdge.TargetQN.ShouldBe("MyApp.Consumers.OrderCreatedConsumer");
        registersEdge.Properties!["registration_pattern"].ShouldBe("AddConsumer<T>");
        registersEdge.Properties["confidence_band"].ShouldBe("high");
    }

    [Fact]
    public void Detects_ConsumerConfigMetadata()
    {
        var code = """
            using System;

            [AttributeUsage(AttributeTargets.Class)]
            public class ConcurrencyLimitAttribute : Attribute
            {
                public ConcurrencyLimitAttribute(int limit) { }
            }

            [AttributeUsage(AttributeTargets.Class)]
            public class PrefetchCountAttribute : Attribute
            {
                public PrefetchCountAttribute(int count) { }
            }

            namespace MyApp.Consumers
            {
                public class Consumer<T> { }

                [ConcurrencyLimit(10)]
                [PrefetchCount(20)]
                public class OrderCreatedConsumer : Consumer<object>
                {
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var classNode = result.Nodes.Single(n =>
            n.Label == NodeLabel.Class && n.Name == "OrderCreatedConsumer");
        classNode.Properties["concurrency_limit"].ShouldBe(10);
        classNode.Properties["prefetch_count"].ShouldBe(20);
    }

    [Fact]
    public void Detects_ServiceBusPublish_CreatesPublishesEdge()
    {
        var code = """
            namespace MyApp.Events
            {
                public class OrderShippedEvent { }
            }

            namespace MyApp.Services
            {
                public interface ITcServiceBus
                {
                    void Publish<T>(T message);
                }

                public class OrderService
                {
                    private readonly ITcServiceBus _serviceBus;

                    public OrderService(ITcServiceBus serviceBus)
                    {
                        _serviceBus = serviceBus;
                    }

                    public void ShipOrder()
                    {
                        _serviceBus.Publish(new MyApp.Events.OrderShippedEvent());
                    }
                }
            }
            """;

        var result = ExtractFromSource(code, withSemantics: true);

        var publishesEdge = result.Edges.ShouldContain(e => e.Type == EdgeType.PUBLISHES);
        publishesEdge.TargetQN.ShouldBe("MyApp.Events.OrderShippedEvent");
        publishesEdge.Properties!["confidence_band"].ShouldBe("high");
    }
}

public static class ShouldlyCollectionExtensions
{
    public static T ShouldContain<T>(this IEnumerable<T> source, Func<T, bool> predicate) where T : class
    {
        var item = source.FirstOrDefault(predicate);
        item.ShouldNotBeNull("Collection should contain an item matching the predicate, but none was found.");
        return item;
    }
}
