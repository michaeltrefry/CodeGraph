using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TC.CodeGraphApi.Models;
using TC.CodeGraphApi.Services;

namespace TC.CodeGraphApi.Extractors.CSharp;

public class CodeGraphSyntaxWalker : CSharpSyntaxWalker
{
    private readonly ExtractorContext _context;
    private readonly SemanticModel? _model;
    private readonly List<GraphNode> _nodes = new();
    private readonly List<PendingEdge> _edges = new();
    private readonly List<UnresolvedCall> _calls = new();
    private readonly List<UnresolvedImport> _imports = new();

    private readonly Stack<string> _scopeStack = new();

    private static readonly HashSet<string> RouteAttributeNames = new()
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "Route",
        "HttpGetAttribute", "HttpPostAttribute", "HttpPutAttribute",
        "HttpDeleteAttribute", "HttpPatchAttribute", "RouteAttribute"
    };

    private static readonly HashSet<string> TestAttributeNames = new()
    {
        "Fact", "Theory", "Test", "TestMethod",
        "FactAttribute", "TheoryAttribute", "TestAttribute", "TestMethodAttribute"
    };

    private static readonly HashSet<string> HttpMethodNames = new()
    {
        "GetAsync", "PostAsync", "PutAsync", "DeleteAsync", "PatchAsync",
        "PostAsJsonAsync", "PutAsJsonAsync", "GetFromJsonAsync",
        "GetStringAsync", "GetStreamAsync", "GetByteArrayAsync",
        "SendAsync"
    };

    private static readonly Dictionary<string, string> HttpMethodMapping = new()
    {
        ["GetAsync"] = "GET", ["GetFromJsonAsync"] = "GET",
        ["GetStringAsync"] = "GET", ["GetStreamAsync"] = "GET",
        ["GetByteArrayAsync"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST",
        ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT",
        ["DeleteAsync"] = "DELETE",
        ["PatchAsync"] = "PATCH",
        ["SendAsync"] = "UNKNOWN"
    };

    private static readonly HashSet<string> DIRegistrationMethods = new()
    {
        "AddScoped", "AddTransient", "AddSingleton",
        "AddKeyedScoped", "AddKeyedTransient", "AddKeyedSingleton"
    };

    /// <summary>
    /// Optional override for the file path when SyntaxTree.FilePath is empty
    /// (common with older non-SDK .csproj projects loaded via MSBuildWorkspace).
    /// </summary>
    private readonly string? _filePathOverride;

    public CodeGraphSyntaxWalker(ExtractorContext context, SemanticModel? semanticModel,
        string? filePathOverride = null)
    {
        _context = context;
        _model = semanticModel;
        _filePathOverride = filePathOverride;
    }

    public ExtractionResult GetResult() => new()
    {
        Nodes = _nodes,
        Edges = _edges,
        UnresolvedCalls = _calls,
        UnresolvedImports = _imports
    };

    // ── Namespace declarations ──────────────────────────────────────────

    public override void VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        var ns = node.Name.ToString();
        AddNamespaceNode(ns, node);
        _scopeStack.Push(ns);
        base.VisitNamespaceDeclaration(node);
        _scopeStack.Pop();
    }

    public override void VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var ns = node.Name.ToString();
        AddNamespaceNode(ns, node);
        _scopeStack.Push(ns);
        base.VisitFileScopedNamespaceDeclaration(node);
        _scopeStack.Pop();
    }

    private void AddNamespaceNode(string ns, SyntaxNode node)
    {
        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Namespace,
            Name = ns,
            QualifiedName = ns,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node)
        });
    }

    // ── Type declarations ───────────────────────────────────────────────

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Class,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node),
            Properties = new()
            {
                ["is_abstract"] = node.Modifiers.Any(SyntaxKind.AbstractKeyword),
                ["is_static"] = node.Modifiers.Any(SyntaxKind.StaticKeyword),
                ["is_generic"] = symbol?.IsGenericType ?? false,
                ["base_types"] = GetBaseTypes(symbol)
            }
        });

        // INHERITS edges
        if (symbol?.BaseType is { SpecialType: not SpecialType.System_Object })
        {
            _edges.Add(new PendingEdge(qn,
                symbol.BaseType.ToDisplayString(), EdgeType.INHERITS));
        }

        // IMPLEMENTS edges
        if (symbol is not null)
        {
            foreach (var iface in symbol.AllInterfaces)
                _edges.Add(new PendingEdge(qn,
                    iface.ToDisplayString(), EdgeType.IMPLEMENTS));
        }

        // Check for MassTransit consumer pattern, messaging attributes, event fields
        if (symbol is not null)
        {
            DetectConsumer(symbol, qn);
            DetectConsumerConfigMetadata(symbol, qn);
            DetectServiceBusEventAttributes(symbol, qn, node);
            DetectEventMessageFields(symbol, qn);
        }

        _scopeStack.Push(qn);
        base.VisitClassDeclaration(node);
        _scopeStack.Pop();
    }

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Interface,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node),
            Properties = new()
            {
                ["is_generic"] = symbol?.IsGenericType ?? false
            }
        });

        if (symbol is not null)
        {
            foreach (var iface in symbol.AllInterfaces)
                _edges.Add(new PendingEdge(qn,
                    iface.ToDisplayString(), EdgeType.INHERITS));
        }

        _scopeStack.Push(qn);
        base.VisitInterfaceDeclaration(node);
        _scopeStack.Pop();
    }

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Record,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node),
            Properties = new()
            {
                ["is_generic"] = symbol?.IsGenericType ?? false,
                ["base_types"] = GetBaseTypes(symbol)
            }
        });

        if (symbol?.BaseType is { SpecialType: not SpecialType.System_Object })
            _edges.Add(new PendingEdge(qn, symbol.BaseType.ToDisplayString(), EdgeType.INHERITS));

        if (symbol is not null)
        {
            foreach (var iface in symbol.AllInterfaces)
                _edges.Add(new PendingEdge(qn, iface.ToDisplayString(), EdgeType.IMPLEMENTS));
        }

        _scopeStack.Push(qn);
        base.VisitRecordDeclaration(node);
        _scopeStack.Pop();
    }

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Struct,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node)
        });

        if (symbol is not null)
        {
            foreach (var iface in symbol.AllInterfaces)
                _edges.Add(new PendingEdge(qn, iface.ToDisplayString(), EdgeType.IMPLEMENTS));
        }

        _scopeStack.Push(qn);
        base.VisitStructDeclaration(node);
        _scopeStack.Pop();
    }

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Enum,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node)
        });

        _scopeStack.Push(qn);
        base.VisitEnumDeclaration(node);
        _scopeStack.Pop();
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Delegate,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node)
        });
    }

    // ── Members ─────────────────────────────────────────────────────────

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Method,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node),
            Properties = new()
            {
                ["signature"] = symbol?.ToDisplayString(
                    SymbolDisplayFormat.MinimallyQualifiedFormat) ?? node.ToString(),
                ["return_type"] = symbol?.ReturnType.ToDisplayString() ?? "unknown",
                ["is_async"] = symbol?.IsAsync ?? node.Modifiers.Any(SyntaxKind.AsyncKeyword),
                ["is_static"] = symbol?.IsStatic ?? node.Modifiers.Any(SyntaxKind.StaticKeyword),
                ["complexity"] = ComputeCyclomaticComplexity(node),
                ["parameter_count"] = symbol?.Parameters.Length ?? node.ParameterList.Parameters.Count,
                ["is_entry_point"] = HasRouteAttribute(node, symbol),
                ["is_test"] = HasTestAttribute(node, symbol)
            }
        });

        // DEFINES_METHOD edge from enclosing type
        if (_scopeStack.Count > 0)
            _edges.Add(new PendingEdge(_scopeStack.Peek(), qn, EdgeType.DEFINES_METHOD));

        // Check for HTTP route attributes → Route node
        DetectRouteEndpoint(node, symbol, qn);

        _scopeStack.Push(qn);
        base.VisitMethodDeclaration(node);
        _scopeStack.Pop();
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);
        var qn = symbol?.ToDisplayString() ?? BuildQualifiedName(node.Identifier.Text);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Property,
            Name = symbol?.Name ?? node.Identifier.Text,
            QualifiedName = qn,
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node),
            Properties = new()
            {
                ["type"] = symbol?.Type.ToDisplayString() ?? node.Type.ToString(),
                ["is_static"] = symbol?.IsStatic ?? node.Modifiers.Any(SyntaxKind.StaticKeyword)
            }
        });

        base.VisitPropertyDeclaration(node);
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var symbol = _model?.GetDeclaredSymbol(node);

        // Detect DI injection via constructor parameters
        if (symbol is not null)
        {
            var ctorQN = symbol.ToDisplayString();
            foreach (var param in symbol.Parameters)
            {
                if (param.Type.TypeKind == TypeKind.Interface &&
                    !IsFrameworkType(param.Type))
                {
                    _edges.Add(new PendingEdge(
                        _scopeStack.Count > 0 ? _scopeStack.Peek() : ctorQN,
                        param.Type.ToDisplayString(),
                        EdgeType.INJECTS,
                        new() { ["parameter_name"] = param.Name }));
                }
            }
        }

        base.VisitConstructorDeclaration(node);
    }

    // ── Invocations ─────────────────────────────────────────────────────

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var symbolInfo = _model?.GetSymbolInfo(node);
        if (symbolInfo?.Symbol is IMethodSymbol targetMethod)
        {
            var callerQN = _scopeStack.Count > 0 ? _scopeStack.Peek() : null;
            if (callerQN is not null)
            {
                _edges.Add(new PendingEdge(callerQN,
                    targetMethod.ToDisplayString(), EdgeType.CALLS,
                    new()
                    {
                        ["confidence"] = 1.0,
                        ["confidence_band"] = "high"
                    }));

                DetectServiceBusPublish(node, targetMethod, callerQN);
                DetectHttpClientCall(node, targetMethod, callerQN);
                DetectGatewayCall(node, targetMethod, callerQN);
                DetectDIRegistration(node, targetMethod, callerQN);
                DetectConsumerRegistration(node, targetMethod, callerQN);
            }
        }
        else if (_scopeStack.Count > 0)
        {
            // Unresolved — record for later matching
            var methodName = GetInvokedMethodName(node);
            if (methodName is not null)
            {
                _calls.Add(new UnresolvedCall(
                    _scopeStack.Peek(),
                    methodName,
                    GetReceiverType(node),
                    0.5));

                // Syntax-based fallback for cross-service patterns when Roslyn
                // can't resolve symbols (e.g., types from external NuGet packages).
                DetectCrossServiceCallsFallback(node, methodName, _scopeStack.Peek());
            }
        }

        base.VisitInvocationExpression(node);
    }

    // ── Using directives ────────────────────────────────────────────────

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var ns = node.Name?.ToString();
        if (ns is not null)
        {
            var fileQN = $"{_context.ProjectName}:{GetRelativePath(node)}";
            _imports.Add(new UnresolvedImport(fileQN, ns));
        }
        base.VisitUsingDirective(node);
    }

    // ── Pattern Detection ───────────────────────────────────────────────

    private void DetectRouteEndpoint(MethodDeclarationSyntax node,
        IMethodSymbol? symbol, string methodQN)
    {
        if (!HasRouteAttribute(node, symbol))
            return;

        var httpMethod = GetHttpMethod(node, symbol);
        var routeTemplate = GetRouteTemplate(node, symbol);

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Route,
            Name = $"{httpMethod} {routeTemplate}",
            QualifiedName = $"route:{_context.ProjectName}:{httpMethod}:{routeTemplate}",
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            EndLine = GetEndLine(node),
            Properties = new()
            {
                ["http_method"] = httpMethod,
                ["route_template"] = routeTemplate
            }
        });

        _edges.Add(new PendingEdge(
            $"route:{_context.ProjectName}:{httpMethod}:{routeTemplate}",
            methodQN,
            EdgeType.HANDLES));
    }

    private void DetectServiceBusPublish(InvocationExpressionSyntax node,
        IMethodSymbol method, string callerQN)
    {
        // Detect: serviceBus.Publish(someEvent) or _bus.Publish(new SomeEvent { ... })
        // ITcServiceBus methods: Publish, PublishToVirtualHost, SendCommandToCustomQueue
        // MassTransit methods: Publish, Send
        if (method.Name is not ("Publish" or "Send" or "PublishToVirtualHost" or "SendCommandToCustomQueue"))
            return;

        var receiverType = method.ContainingType?.ToDisplayString() ?? "";
        if (!receiverType.Contains("ServiceBus") &&
            !receiverType.Contains("ITcServiceBus") &&
            !receiverType.Contains("IBus") &&
            !receiverType.Contains("IPublishEndpoint") &&
            !receiverType.Contains("ISendEndpointProvider"))
            return;

        // Extract event type from generic argument or first parameter type
        string? eventTypeQN = null;

        if (method.TypeArguments.Length > 0)
        {
            eventTypeQN = method.TypeArguments[0].ToDisplayString();
        }
        else if (method.Parameters.Length > 0 && node.ArgumentList.Arguments.Count > 0)
        {
            var argType = _model?.GetTypeInfo(node.ArgumentList.Arguments[0].Expression);
            eventTypeQN = argType?.Type?.ToDisplayString();
        }

        if (eventTypeQN is not null)
        {
            _edges.Add(new PendingEdge(callerQN, eventTypeQN, EdgeType.PUBLISHES,
                new() { ["confidence_band"] = "high" }));
        }
    }

    private void DetectConsumer(INamedTypeSymbol classSymbol, string classQN)
    {
        // Check if class implements Consumer<T> or IConsumer<T>
        foreach (var baseType in classSymbol.AllInterfaces.Concat(
            GetBaseTypeChain(classSymbol)))
        {
            if (baseType is not INamedTypeSymbol namedBase)
                continue;

            var baseName = namedBase.Name;
            if (baseName is not ("Consumer" or "IConsumer" or "TcConsumer" or "ITcConsumer"))
                continue;

            if (namedBase.TypeArguments.Length > 0)
            {
                var eventType = namedBase.TypeArguments[0].ToDisplayString();
                _edges.Add(new PendingEdge(classQN, eventType, EdgeType.CONSUMES,
                    new() { ["confidence_band"] = "high" }));
            }
        }
    }

    /// <summary>
    /// Extract routing metadata from [TcServiceBusEvent], [TcServiceBusCommand], and similar
    /// attributes on event classes. Creates Queue and Exchange nodes and ROUTED_TO/BOUND_TO edges.
    /// </summary>
    private void DetectServiceBusEventAttributes(INamedTypeSymbol classSymbol, string classQN,
        ClassDeclarationSyntax node)
    {
        foreach (var attr in classSymbol.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name ?? "";
            if (attrName is not ("TcServiceBusEvent" or "TcServiceBusEventAttribute"
                or "TcServiceBusCommand" or "TcServiceBusCommandAttribute"))
                continue;

            string? queueName = null;
            string? exchangeName = null;
            string? virtualHost = null;
            string? routingKey = null;

            // Extract from constructor arguments
            var ctorArgs = attr.ConstructorArguments;
            if (ctorArgs.Length >= 1)
                queueName = ctorArgs[0].Value?.ToString();
            if (ctorArgs.Length >= 2)
                exchangeName = ctorArgs[1].Value?.ToString();
            if (ctorArgs.Length >= 3)
                virtualHost = ctorArgs[2].Value?.ToString();

            // Extract from named arguments (overrides positional)
            foreach (var named in attr.NamedArguments)
            {
                switch (named.Key)
                {
                    case "QueueName" or "Queue":
                        queueName = named.Value.Value?.ToString();
                        break;
                    case "ExchangeName" or "Exchange":
                        exchangeName = named.Value.Value?.ToString();
                        break;
                    case "VirtualHost":
                        virtualHost = named.Value.Value?.ToString();
                        break;
                    case "RoutingKey":
                        routingKey = named.Value.Value?.ToString();
                        break;
                }
            }

            // Enrich the existing class node's properties with routing metadata
            var classNode = _nodes.LastOrDefault(n => n.QualifiedName == classQN);
            if (classNode is not null)
            {
                if (queueName is not null) classNode.Properties["queue_name"] = queueName;
                if (exchangeName is not null) classNode.Properties["exchange_name"] = exchangeName;
                if (virtualHost is not null) classNode.Properties["virtual_host"] = virtualHost;
                if (routingKey is not null) classNode.Properties["routing_key"] = routingKey;
                classNode.Properties["is_service_bus_event"] = true;
            }

            // Create Queue node if queue name is known
            if (queueName is not null)
            {
                var queueQN = $"queue:{_context.ProjectName}:{queueName}";
                _nodes.Add(new GraphNode
                {
                    Project = _context.ProjectName,
                    DotnetProject = _context.DotnetProject,
                    Label = NodeLabel.Queue,
                    Name = queueName,
                    QualifiedName = queueQN,
                    FilePath = GetRelativePath(node),
                    StartLine = GetStartLine(node),
                    Properties = new()
                    {
                        ["queue_name"] = queueName,
                        ["virtual_host"] = virtualHost ?? "/",
                    }
                });

                // Event --ROUTED_TO--> Queue
                _edges.Add(new PendingEdge(classQN, queueQN, EdgeType.ROUTED_TO,
                    new() { ["confidence_band"] = "high" }));

                // Create Exchange node and Queue --BOUND_TO--> Exchange if exchange is specified
                if (exchangeName is not null)
                {
                    var exchangeQN = $"exchange:{_context.ProjectName}:{exchangeName}";
                    _nodes.Add(new GraphNode
                    {
                        Project = _context.ProjectName,
                        DotnetProject = _context.DotnetProject,
                        Label = NodeLabel.Exchange,
                        Name = exchangeName,
                        QualifiedName = exchangeQN,
                        FilePath = GetRelativePath(node),
                        StartLine = GetStartLine(node),
                        Properties = new()
                        {
                            ["exchange_name"] = exchangeName,
                            ["virtual_host"] = virtualHost ?? "/",
                        }
                    });

                    _edges.Add(new PendingEdge(queueQN, exchangeQN, EdgeType.BOUND_TO,
                        new() { ["confidence_band"] = "high" }));
                }
            }

            break; // Only process the first matching attribute
        }
    }

    /// <summary>
    /// Extract public properties of event classes to index the message contract fields.
    /// An "event class" is one that is the target of a PUBLISHES or CONSUMES edge,
    /// or has a [TcServiceBusEvent]/[TcServiceBusCommand] attribute.
    /// We detect event-ness by naming convention (*Event, *Command, *Message) or attribute presence.
    /// </summary>
    private void DetectEventMessageFields(INamedTypeSymbol classSymbol, string classQN)
    {
        // Check if this looks like an event/message class
        var name = classSymbol.Name;
        var hasEventAttribute = classSymbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name is "TcServiceBusEvent" or "TcServiceBusEventAttribute"
                or "TcServiceBusCommand" or "TcServiceBusCommandAttribute");

        var looksLikeEvent = hasEventAttribute ||
            name.EndsWith("Event") || name.EndsWith("Command") || name.EndsWith("Message");

        if (!looksLikeEvent)
            return;

        var fields = new List<Dictionary<string, object>>();

        foreach (var member in classSymbol.GetMembers())
        {
            if (member is not IPropertySymbol prop)
                continue;
            if (prop.DeclaredAccessibility != Accessibility.Public)
                continue;
            if (prop.IsStatic || prop.IsIndexer)
                continue;

            var fieldInfo = new Dictionary<string, object>
            {
                ["name"] = prop.Name,
                ["type"] = prop.Type.ToDisplayString(),
                ["nullable"] = prop.NullableAnnotation == NullableAnnotation.Annotated
                    || prop.Type.IsReferenceType
            };

            fields.Add(fieldInfo);

            // If the field type is a known domain type (TC.* namespace), create CARRIES_FIELD edge
            var fieldTypeNs = prop.Type.ContainingNamespace?.ToDisplayString() ?? "";
            var underlyingType = UnwrapCollectionType(prop.Type);
            var underlyingNs = underlyingType?.ContainingNamespace?.ToDisplayString() ?? "";

            if ((fieldTypeNs.StartsWith("TC.") || underlyingNs.StartsWith("TC.")) &&
                underlyingType is not null)
            {
                _edges.Add(new PendingEdge(classQN, underlyingType.ToDisplayString(),
                    EdgeType.CARRIES_FIELD,
                    new()
                    {
                        ["field_name"] = prop.Name,
                        ["confidence_band"] = "high"
                    }));
            }
        }

        if (fields.Count > 0)
        {
            // Enrich the class node with field metadata
            var classNode = _nodes.LastOrDefault(n => n.QualifiedName == classQN);
            if (classNode is not null)
                classNode.Properties["fields"] = fields;
        }
    }

    /// <summary>
    /// Unwrap collection types (List&lt;T&gt;, IEnumerable&lt;T&gt;, T[], etc.) to get the element type.
    /// Returns null if the type is not a collection of a named type.
    /// </summary>
    private static ITypeSymbol? UnwrapCollectionType(ITypeSymbol type)
    {
        // Handle arrays
        if (type is IArrayTypeSymbol arrayType)
            return arrayType.ElementType;

        // Handle generic collections (List<T>, IEnumerable<T>, ICollection<T>, etc.)
        if (type is INamedTypeSymbol { IsGenericType: true } namedType)
        {
            var name = namedType.OriginalDefinition.ToDisplayString();
            if (name.StartsWith("System.Collections.") ||
                name is "System.Collections.Generic.List<T>"
                    or "System.Collections.Generic.IList<T>"
                    or "System.Collections.Generic.IEnumerable<T>"
                    or "System.Collections.Generic.ICollection<T>"
                    or "System.Collections.Generic.IReadOnlyList<T>"
                    or "System.Collections.Generic.IReadOnlyCollection<T>"
                    or "System.Collections.Generic.HashSet<T>")
            {
                return namedType.TypeArguments[0];
            }
        }

        // Not a collection, return the type itself if it's a domain type
        return type;
    }

    /// <summary>
    /// Extract consumer configuration metadata from MassTransit/in-house attributes.
    /// Captures concurrency limits, retry policies, prefetch counts.
    /// </summary>
    private void DetectConsumerConfigMetadata(INamedTypeSymbol classSymbol, string classQN)
    {
        // Only process consumer classes
        var isConsumer = classSymbol.AllInterfaces.Concat(GetBaseTypeChain(classSymbol))
            .OfType<INamedTypeSymbol>()
            .Any(t => t.Name is "Consumer" or "IConsumer" or "TcConsumer" or "ITcConsumer");

        if (!isConsumer)
            return;

        var consumerProps = new Dictionary<string, object>();

        foreach (var attr in classSymbol.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name ?? "";

            switch (attrName)
            {
                case "ConcurrencyLimit" or "ConcurrencyLimitAttribute":
                    if (attr.ConstructorArguments.Length > 0)
                        consumerProps["concurrency_limit"] = attr.ConstructorArguments[0].Value!;
                    break;

                case "RetryPolicy" or "RetryPolicyAttribute":
                    if (attr.ConstructorArguments.Length > 0)
                        consumerProps["retry_policy"] = attr.ConstructorArguments[0].Value?.ToString() ?? "default";
                    foreach (var named in attr.NamedArguments)
                    {
                        if (named.Key is "RetryCount" or "Retries")
                            consumerProps["retry_count"] = named.Value.Value!;
                        if (named.Key is "Interval" or "RetryInterval")
                            consumerProps["retry_interval"] = named.Value.Value?.ToString() ?? "";
                    }
                    break;

                case "PrefetchCount" or "PrefetchCountAttribute":
                    if (attr.ConstructorArguments.Length > 0)
                        consumerProps["prefetch_count"] = attr.ConstructorArguments[0].Value!;
                    break;
            }
        }

        if (consumerProps.Count > 0)
        {
            var classNode = _nodes.LastOrDefault(n => n.QualifiedName == classQN);
            if (classNode is not null)
            {
                foreach (var kv in consumerProps)
                    classNode.Properties[kv.Key] = kv.Value;
            }
        }
    }

    private void DetectHttpClientCall(InvocationExpressionSyntax node,
        IMethodSymbol method, string callerQN)
    {
        if (!HttpMethodNames.Contains(method.Name))
            return;

        var receiverType = method.ContainingType?.ToDisplayString() ?? "";
        if (!receiverType.Contains("HttpClient"))
            return;

        var httpMethod = HttpMethodMapping.GetValueOrDefault(method.Name, "UNKNOWN");

        // Try to extract URL from the first string argument
        string? urlPattern = null;
        if (node.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = node.ArgumentList.Arguments[0].Expression;
            if (firstArg is LiteralExpressionSyntax literal &&
                literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                urlPattern = literal.Token.ValueText;
            }
            else if (firstArg is InterpolatedStringExpressionSyntax interpolated)
            {
                // Convert interpolated parts to pattern: /api/wallet/{id}
                urlPattern = string.Concat(interpolated.Contents.Select(c => c switch
                {
                    InterpolatedStringTextSyntax text => text.TextToken.ValueText,
                    InterpolationSyntax => "{param}",
                    _ => ""
                }));
            }
        }

        if (urlPattern is not null)
        {
            _edges.Add(new PendingEdge(callerQN,
                $"route:*:{httpMethod}:{urlPattern}",
                EdgeType.HTTP_CALLS,
                new()
                {
                    ["http_method"] = httpMethod,
                    ["url_pattern"] = urlPattern,
                    ["confidence_band"] = "medium"
                }));
        }
    }

    private void DetectGatewayCall(InvocationExpressionSyntax node,
        IMethodSymbol method, string callerQN)
    {
        var receiverType = method.ContainingType?.ToDisplayString() ?? "";
        if (!receiverType.Contains("Gateway"))
            return;

        if (method.Name is "SendToService" or "SendToServiceAsync")
        {
            // Explicit service name + path: SendToService(HttpMethod, serviceName, path, dto)
            var args = node.ArgumentList.Arguments;
            if (args.Count >= 3)
            {
                var serviceNameArg = args[1].Expression;
                var pathArg = args[2].Expression;

                var serviceName = ExtractStringLiteral(serviceNameArg);
                var path = ExtractStringLiteral(pathArg);

                // Also try to determine HTTP method from first arg
                var httpMethod = "UNKNOWN";
                if (args[0].Expression is MemberAccessExpressionSyntax memberAccess)
                    httpMethod = memberAccess.Name.Identifier.Text.ToUpperInvariant();

                if (serviceName is not null && path is not null)
                {
                    _edges.Add(new PendingEdge(callerQN,
                        $"route:{serviceName}:{httpMethod}:{path}",
                        EdgeType.HTTP_CALLS,
                        new()
                        {
                            ["http_method"] = httpMethod,
                            ["url_pattern"] = path,
                            ["service_name"] = serviceName,
                            ["gateway_call"] = true,
                            ["confidence_band"] = "medium"
                        }));
                    return;
                }
            }
        }

        if (method.Name is not ("Send" or "SendAsync"))
            return;

        // Resolve the request DTO type from the first argument
        if (node.ArgumentList.Arguments.Count == 0)
            return;

        var argTypeInfo = _model?.GetTypeInfo(node.ArgumentList.Arguments[0].Expression);
        var dtoType = argTypeInfo?.Type as INamedTypeSymbol;
        if (dtoType is null)
            return;

        var dtoTypeQN = dtoType.ToDisplayString();

        // Read [TcServiceDto("ServiceName", "RouteName", "GET")] from the DTO type
        string? serviceName2 = null;
        string? routeName = null;
        string? httpMethod2 = null;

        foreach (var attr in dtoType.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name ?? "";
            if (attrName is not ("TcServiceDto" or "TcServiceDtoAttribute"))
                continue;

            var ctorArgs = attr.ConstructorArguments;
            if (ctorArgs.Length >= 3)
            {
                serviceName2 = ctorArgs[0].Value?.ToString();
                routeName = ctorArgs[1].Value?.ToString();
                httpMethod2 = ctorArgs[2].Value?.ToString()?.ToUpperInvariant();
            }
            else if (ctorArgs.Length >= 2)
            {
                serviceName2 = ctorArgs[0].Value?.ToString();
                routeName = ctorArgs[1].Value?.ToString();
            }
            break;
        }

        var props = new Dictionary<string, object>
        {
            ["request_dto"] = dtoTypeQN,
            ["gateway_call"] = true,
            ["confidence_band"] = serviceName2 is not null ? "high" : "medium"
        };
        if (serviceName2 is not null) props["service_name"] = serviceName2;
        if (routeName is not null) props["route_name"] = routeName;
        if (httpMethod2 is not null) props["http_method"] = httpMethod2;

        // Use the DTO type QN as target — CrossRepoLinker resolves to the owning project
        _edges.Add(new PendingEdge(callerQN, dtoTypeQN, EdgeType.HTTP_CALLS, props));
    }

    private static string? ExtractStringLiteral(ExpressionSyntax expr)
    {
        return expr switch
        {
            LiteralExpressionSyntax literal
                when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
            InterpolatedStringExpressionSyntax interpolated =>
                string.Concat(interpolated.Contents.Select(c => c switch
                {
                    InterpolatedStringTextSyntax text => text.TextToken.ValueText,
                    InterpolationSyntax => "{param}",
                    _ => ""
                })),
            _ => null
        };
    }

    /// <summary>
    /// Syntax-based fallback for detecting cross-service patterns when Roslyn can't resolve
    /// method symbols (common when types come from external NuGet packages).
    /// Uses field/variable naming conventions to infer the receiver type.
    /// </summary>
    private void DetectCrossServiceCallsFallback(
        InvocationExpressionSyntax node, string methodName, string callerQN)
    {
        if (node.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        // Get the receiver identifier name (e.g., _serviceBus, _tcGateway, _httpClient)
        var receiverName = memberAccess.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax nested => nested.Name.Identifier.Text,
            _ => null
        };

        if (receiverName is null) return;

        var receiverLower = receiverName.ToLowerInvariant();

        // ServiceBus pattern: _serviceBus.Publish, _bus.Publish, etc.
        if (receiverLower.Contains("servicebus") || receiverLower.Contains("bus"))
        {
            if (methodName is "Publish" or "PublishToVirtualHost" or "SendCommandToCustomQueue")
            {
                var eventTypeQN = TryExtractGenericTypeArgFromSyntax(node);
                if (eventTypeQN is not null)
                {
                    _edges.Add(new PendingEdge(callerQN, eventTypeQN, EdgeType.PUBLISHES,
                        new()
                        {
                            ["confidence_band"] = "medium",
                            ["resolution"] = "syntax_fallback"
                        }));
                }
            }
        }

        // Gateway pattern: _gateway.SendAsync, _tcGateway.Send, etc.
        if (receiverLower.Contains("gateway"))
        {
            if (methodName is "Send" or "SendAsync")
            {
                var dtoTypeQN = TryExtractFirstArgTypeFromSyntax(node);
                if (dtoTypeQN is not null)
                {
                    _edges.Add(new PendingEdge(callerQN, dtoTypeQN, EdgeType.HTTP_CALLS,
                        new()
                        {
                            ["request_dto"] = dtoTypeQN,
                            ["gateway_call"] = true,
                            ["confidence_band"] = "low",
                            ["resolution"] = "syntax_fallback"
                        }));
                }
            }
            else if (methodName is "SendToService" or "SendToServiceAsync")
            {
                var args = node.ArgumentList.Arguments;
                if (args.Count >= 3)
                {
                    var serviceName = ExtractStringLiteral(args[1].Expression);
                    var path = ExtractStringLiteral(args[2].Expression);

                    if (serviceName is not null && path is not null)
                    {
                        _edges.Add(new PendingEdge(callerQN,
                            $"route:{serviceName}:UNKNOWN:{path}",
                            EdgeType.HTTP_CALLS,
                            new()
                            {
                                ["service_name"] = serviceName,
                                ["url_pattern"] = path,
                                ["gateway_call"] = true,
                                ["confidence_band"] = "low",
                                ["resolution"] = "syntax_fallback"
                            }));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Try to extract a generic type argument from syntax when semantic resolution fails.
    /// Handles: Publish&lt;SomeEvent&gt;(...) and Publish(new SomeEvent { ... })
    /// </summary>
    private string? TryExtractGenericTypeArgFromSyntax(InvocationExpressionSyntax node)
    {
        // Check for generic type argument: Publish<SomeEvent>(...)
        if (node.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic })
        {
            if (generic.TypeArgumentList.Arguments.Count > 0)
            {
                var typeArg = generic.TypeArgumentList.Arguments[0];
                // Try semantic resolution first
                var typeInfo = _model?.GetTypeInfo(typeArg);
                if (typeInfo?.Type is not null && typeInfo.Value.Type.TypeKind != TypeKind.Error)
                    return typeInfo.Value.Type.ToDisplayString();
                // Fall back to syntax text
                return typeArg.ToString();
            }
        }

        // Check for new SomeEvent() as first argument
        if (node.ArgumentList.Arguments.Count > 0)
        {
            var firstArg = node.ArgumentList.Arguments[0].Expression;
            if (firstArg is ObjectCreationExpressionSyntax creation)
            {
                var typeInfo = _model?.GetTypeInfo(creation);
                if (typeInfo?.Type is not null && typeInfo.Value.Type.TypeKind != TypeKind.Error)
                    return typeInfo.Value.Type.ToDisplayString();
                return creation.Type.ToString();
            }
            // Also handle implicit new: new() { ... } won't help, but typed new SomeEvent() { ... } will
        }

        return null;
    }

    /// <summary>
    /// Try to extract the type of the first argument from syntax.
    /// Handles: SendAsync(new CheckBlacklist { ... })
    /// </summary>
    private string? TryExtractFirstArgTypeFromSyntax(InvocationExpressionSyntax node)
    {
        if (node.ArgumentList.Arguments.Count == 0) return null;

        var firstArg = node.ArgumentList.Arguments[0].Expression;

        if (firstArg is ObjectCreationExpressionSyntax creation)
        {
            var typeInfo = _model?.GetTypeInfo(creation);
            if (typeInfo?.Type is not null && typeInfo.Value.Type.TypeKind != TypeKind.Error)
                return typeInfo.Value.Type.ToDisplayString();
            return creation.Type.ToString();
        }

        // Try semantic model on the expression
        var argTypeInfo = _model?.GetTypeInfo(firstArg);
        if (argTypeInfo?.Type is not null && argTypeInfo.Value.Type.TypeKind != TypeKind.Error)
            return argTypeInfo.Value.Type.ToDisplayString();

        return null;
    }

    private void DetectDIRegistration(InvocationExpressionSyntax node,
        IMethodSymbol method, string callerQN)
    {
        if (!DIRegistrationMethods.Contains(method.Name))
            return;

        // Check for generic type arguments: AddScoped<IFoo, Foo>()
        if (method.TypeArguments.Length < 2)
            return;

        var interfaceType = method.TypeArguments[0].ToDisplayString();
        var implementationType = method.TypeArguments[1].ToDisplayString();

        var lifetime = method.Name switch
        {
            "AddScoped" or "AddKeyedScoped" => "Scoped",
            "AddTransient" or "AddKeyedTransient" => "Transient",
            "AddSingleton" or "AddKeyedSingleton" => "Singleton",
            _ => "Unknown"
        };

        _nodes.Add(new GraphNode
        {
            Project = _context.ProjectName,
            DotnetProject = _context.DotnetProject,
            Label = NodeLabel.Service,
            Name = interfaceType.Split('.').Last(),
            QualifiedName = $"service:{_context.ProjectName}:{interfaceType}",
            FilePath = GetRelativePath(node),
            StartLine = GetStartLine(node),
            Properties = new()
            {
                ["lifetime"] = lifetime,
                ["interface"] = interfaceType,
                ["implementation"] = implementationType
            }
        });
    }

    /// <summary>
    /// Detect consumer registration patterns in Startup/configuration classes:
    /// AddConsumer&lt;T&gt;(), AddConsumers(assembly), RegisterConsumer&lt;T&gt;(),
    /// ServiceBus.RegisterConsumer&lt;T&gt;(), cfg.ReceiveEndpoint("queue", e => e.Consumer&lt;T&gt;())
    /// Creates REGISTERS edges from the enclosing class/project to the consumer type.
    /// </summary>
    private void DetectConsumerRegistration(InvocationExpressionSyntax node,
        IMethodSymbol method, string callerQN)
    {
        var methodName = method.Name;

        // Pattern 1: AddConsumer<T>() — MassTransit
        if (methodName is "AddConsumer" && method.TypeArguments.Length > 0)
        {
            var consumerType = method.TypeArguments[0].ToDisplayString();
            _edges.Add(new PendingEdge(callerQN, consumerType, EdgeType.REGISTERS,
                new()
                {
                    ["registration_pattern"] = "AddConsumer<T>",
                    ["confidence_band"] = "high"
                }));
            return;
        }

        // Pattern 2: RegisterConsumer<T>() — in-house ServiceBus
        if (methodName is "RegisterConsumer" && method.TypeArguments.Length > 0)
        {
            var consumerType = method.TypeArguments[0].ToDisplayString();
            _edges.Add(new PendingEdge(callerQN, consumerType, EdgeType.REGISTERS,
                new()
                {
                    ["registration_pattern"] = "RegisterConsumer<T>",
                    ["confidence_band"] = "high"
                }));
            return;
        }

        // Pattern 3: AddConsumers(typeof(X).Assembly) — MassTransit assembly scanning
        if (methodName is "AddConsumers" && node.ArgumentList.Arguments.Count > 0)
        {
            var arg = node.ArgumentList.Arguments[0].Expression;
            // typeof(SomeType).Assembly
            if (arg is MemberAccessExpressionSyntax { Name.Identifier.Text: "Assembly" } memberAccess
                && memberAccess.Expression is InvocationExpressionSyntax typeofExpr
                && typeofExpr.Expression is IdentifierNameSyntax { Identifier.Text: "typeof" }
                && typeofExpr.ArgumentList.Arguments.Count > 0)
            {
                var typeArg = _model?.GetTypeInfo(typeofExpr.ArgumentList.Arguments[0].Expression);
                var assemblyMarkerType = typeArg?.Type?.ToDisplayString();
                if (assemblyMarkerType is not null)
                {
                    _edges.Add(new PendingEdge(callerQN, assemblyMarkerType, EdgeType.REGISTERS,
                        new()
                        {
                            ["registration_pattern"] = "AddConsumers(assembly)",
                            ["assembly_scan"] = true,
                            ["confidence_band"] = "medium"
                        }));
                }
            }
            return;
        }

        // Pattern 4: cfg.ReceiveEndpoint("queue-name", e => e.Consumer<T>())
        if (methodName is "ReceiveEndpoint" && node.ArgumentList.Arguments.Count >= 2)
        {
            var queueArg = node.ArgumentList.Arguments[0].Expression;
            var queueName = ExtractStringLiteral(queueArg);

            // Look for Consumer<T>() in the lambda body
            var lambdaArg = node.ArgumentList.Arguments[1].Expression;
            if (lambdaArg is SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax)
            {
                foreach (var invocation in lambdaArg.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var innerSymbol = _model?.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
                    if (innerSymbol?.Name is "Consumer" && innerSymbol.TypeArguments.Length > 0)
                    {
                        var consumerType = innerSymbol.TypeArguments[0].ToDisplayString();
                        var props = new Dictionary<string, object>
                        {
                            ["registration_pattern"] = "ReceiveEndpoint",
                            ["confidence_band"] = "high"
                        };
                        if (queueName is not null)
                            props["queue_name"] = queueName;

                        _edges.Add(new PendingEdge(callerQN, consumerType, EdgeType.REGISTERS, props));
                    }
                }
            }
        }
    }

    // ── Utility Methods ─────────────────────────────────────────────────

    private string BuildQualifiedName(string identifier)
    {
        if (_scopeStack.Count == 0)
            return $"{_context.ProjectName}.{identifier}";
        return $"{_scopeStack.Peek()}.{identifier}";
    }

    private string GetRelativePath(SyntaxNode node)
    {
        var filePath = node.SyntaxTree.FilePath;
        if (string.IsNullOrEmpty(filePath))
            filePath = _filePathOverride ?? "";
        if (string.IsNullOrEmpty(filePath))
            return "";

        if (filePath.StartsWith(_context.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = filePath[_context.RootPath.Length..];
            return relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');
        }

        return filePath.Replace('\\', '/');
    }

    private static int GetStartLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int GetEndLine(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    private static string GetBaseTypes(INamedTypeSymbol? symbol)
    {
        if (symbol is null)
            return "";

        var types = new List<string>();
        if (symbol.BaseType is { SpecialType: not SpecialType.System_Object })
            types.Add(symbol.BaseType.ToDisplayString());

        types.AddRange(symbol.AllInterfaces.Select(i => i.ToDisplayString()));
        return string.Join(", ", types);
    }

    private static IEnumerable<ITypeSymbol> GetBaseTypeChain(INamedTypeSymbol symbol)
    {
        var current = symbol.BaseType;
        while (current is not null && current.SpecialType != SpecialType.System_Object)
        {
            yield return current;
            current = current.BaseType;
        }
    }

    private static int ComputeCyclomaticComplexity(MethodDeclarationSyntax node)
    {
        var complexity = 1;
        foreach (var descendant in node.DescendantNodes())
        {
            complexity += descendant switch
            {
                IfStatementSyntax => 1,
                WhileStatementSyntax => 1,
                ForStatementSyntax => 1,
                ForEachStatementSyntax => 1,
                CaseSwitchLabelSyntax => 1,
                CatchClauseSyntax => 1,
                ConditionalExpressionSyntax => 1,
                _ => 0
            };
        }
        foreach (var token in node.DescendantTokens())
        {
            complexity += token.Kind() switch
            {
                SyntaxKind.AmpersandAmpersandToken => 1,
                SyntaxKind.BarBarToken => 1,
                SyntaxKind.QuestionQuestionToken => 1,
                _ => 0
            };
        }
        return complexity;
    }

    private static bool HasRouteAttribute(MethodDeclarationSyntax node, IMethodSymbol? symbol)
    {
        if (symbol is not null)
        {
            return symbol.GetAttributes().Any(a =>
                a.AttributeClass?.Name is "HttpGetAttribute" or
                    "HttpPostAttribute" or "HttpPutAttribute" or
                    "HttpDeleteAttribute" or "HttpPatchAttribute" or
                    "RouteAttribute");
        }

        return node.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => RouteAttributeNames.Contains(a.Name.ToString()));
    }

    private static bool HasTestAttribute(MethodDeclarationSyntax node, IMethodSymbol? symbol)
    {
        if (symbol is not null)
            return symbol.GetAttributes()
                .Any(a => a.AttributeClass?.Name is not null &&
                          TestAttributeNames.Contains(a.AttributeClass.Name));

        return node.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a => TestAttributeNames.Contains(a.Name.ToString()));
    }

    private static string GetHttpMethod(MethodDeclarationSyntax node, IMethodSymbol? symbol)
    {
        var attrMap = new Dictionary<string, string>
        {
            ["HttpGet"] = "GET", ["HttpGetAttribute"] = "GET",
            ["HttpPost"] = "POST", ["HttpPostAttribute"] = "POST",
            ["HttpPut"] = "PUT", ["HttpPutAttribute"] = "PUT",
            ["HttpDelete"] = "DELETE", ["HttpDeleteAttribute"] = "DELETE",
            ["HttpPatch"] = "PATCH", ["HttpPatchAttribute"] = "PATCH"
        };

        if (symbol is not null)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                var name = attr.AttributeClass?.Name;
                if (name is not null && attrMap.TryGetValue(name, out var method))
                    return method;
            }
        }

        foreach (var attr in node.AttributeLists.SelectMany(al => al.Attributes))
        {
            var name = attr.Name.ToString();
            if (attrMap.TryGetValue(name, out var method))
                return method;
        }

        return "UNKNOWN";
    }

    private static string GetRouteTemplate(MethodDeclarationSyntax node, IMethodSymbol? symbol)
    {
        // Get class-level route prefix
        var classRoute = GetClassRoutePrefix(node);

        // Get method-level route from attribute argument
        var methodRoute = "";

        if (symbol is not null)
        {
            foreach (var attr in symbol.GetAttributes())
            {
                var name = attr.AttributeClass?.Name;
                if (name is not null && RouteAttributeNames.Contains(name) &&
                    attr.ConstructorArguments.Length > 0)
                {
                    methodRoute = attr.ConstructorArguments[0].Value?.ToString() ?? "";
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(methodRoute))
        {
            foreach (var attr in node.AttributeLists.SelectMany(al => al.Attributes))
            {
                if (RouteAttributeNames.Contains(attr.Name.ToString()) &&
                    attr.ArgumentList?.Arguments.Count > 0)
                {
                    var arg = attr.ArgumentList.Arguments[0].Expression;
                    if (arg is LiteralExpressionSyntax literal)
                        methodRoute = literal.Token.ValueText;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(classRoute))
            return methodRoute;
        if (string.IsNullOrEmpty(methodRoute))
            return classRoute;

        return $"{classRoute.TrimEnd('/')}/{methodRoute.TrimStart('/')}";
    }

    private static string GetClassRoutePrefix(SyntaxNode node)
    {
        var classDecl = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDecl is null)
            return "";

        foreach (var attr in classDecl.AttributeLists.SelectMany(al => al.Attributes))
        {
            var name = attr.Name.ToString();
            if (name is "Route" or "RouteAttribute" &&
                attr.ArgumentList?.Arguments.Count > 0)
            {
                var arg = attr.ArgumentList.Arguments[0].Expression;
                if (arg is LiteralExpressionSyntax literal)
                {
                    var template = literal.Token.ValueText;
                    // Replace [controller] with actual controller name
                    if (template.Contains("[controller]"))
                    {
                        var controllerName = classDecl.Identifier.Text
                            .Replace("Controller", "", StringComparison.OrdinalIgnoreCase)
                            .ToLowerInvariant();
                        template = template.Replace("[controller]", controllerName);
                    }
                    return template;
                }
            }
        }

        return "";
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax node)
    {
        return node.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }

    private string? GetReceiverType(InvocationExpressionSyntax node)
    {
        if (node.Expression is MemberAccessExpressionSyntax member && _model is not null)
        {
            var typeInfo = _model.GetTypeInfo(member.Expression);
            return typeInfo.Type?.ToDisplayString();
        }
        return null;
    }

    private static bool IsFrameworkType(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString() ?? "";
        return ns.StartsWith("Microsoft.Extensions.Logging") ||
               ns.StartsWith("Microsoft.Extensions.Configuration") ||
               ns.StartsWith("Microsoft.Extensions.Options") ||
               ns.StartsWith("Microsoft.Extensions.Caching") ||
               ns.StartsWith("System.");
    }
}
