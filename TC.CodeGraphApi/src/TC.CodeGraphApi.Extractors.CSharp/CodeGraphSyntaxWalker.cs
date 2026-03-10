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

    public CodeGraphSyntaxWalker(ExtractorContext context, SemanticModel? semanticModel)
    {
        _context = context;
        _model = semanticModel;
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

        // Check for MassTransit consumer pattern
        if (symbol is not null)
            DetectConsumer(symbol, qn);

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
                DetectDIRegistration(node, targetMethod, callerQN);
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
        if (method.Name is not ("Publish" or "Send"))
            return;

        var receiverType = method.ContainingType?.ToDisplayString() ?? "";
        if (!receiverType.Contains("ServiceBus") &&
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
            if (baseName is not ("Consumer" or "IConsumer"))
                continue;

            if (namedBase.TypeArguments.Length > 0)
            {
                var eventType = namedBase.TypeArguments[0].ToDisplayString();
                _edges.Add(new PendingEdge(classQN, eventType, EdgeType.CONSUMES,
                    new() { ["confidence_band"] = "high" }));
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
            return "";

        if (filePath.StartsWith(_context.RootPath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = filePath[_context.RootPath.Length..];
            return relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return filePath;
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
