using CodeGraph.Models;
using CodeGraph.Services.Pipeline;
using TreeSitter;

namespace CodeGraph.Extractors.TreeSitter;

/// <summary>
/// Reads Swift call sites, import declarations and argument-label signatures.
/// <para>
/// A call's receiver is a type only when the syntax or the file proves it: <c>self</c>/<c>Self</c>
/// (the enclosing type), an UpperCamelCase name (a type, by Swift naming convention), or a
/// variable whose type is written in the file — a parameter, a local or global declaration, or a
/// property of a type declared in the file, with a type annotation or an initializer-call value.
/// Chains (<c>a.b.c()</c>) follow property types through types declared in the file. Any other
/// receiver is reported as unresolved, so the pipeline never guesses an owner.
/// </para>
/// <para>
/// A call of a type (<c>Circle(radius: 1)</c>, <c>Circle.init(radius: 1)</c>, <c>.init(...)</c>
/// with a contextual type) is a call of that type's <c>init</c>. A bare call inside a type of a
/// method that type declares in this file is a call on <c>self</c>. Every call carries its
/// argument labels so the pipeline can pick the overload they select.
/// </para>
/// </summary>
internal sealed class SwiftCallSites
{
    // Guards receiver inference against cyclic declarations in malformed source.
    private const int MaxInferenceDepth = 8;

    private static readonly HashSet<string> TypeDeclarationNodes = new(StringComparer.Ordinal)
    {
        "class_declaration", "protocol_declaration"
    };

    private static readonly HashSet<string> CallableNodes = new(StringComparer.Ordinal)
    {
        "function_declaration", "init_declaration", "subscript_declaration"
    };

    private static readonly HashSet<string> ScopedImportKinds = new(StringComparer.Ordinal)
    {
        "typealias", "struct", "class", "enum", "protocol", "let", "var", "func"
    };

    // Type path → every declaration and same-file extension block of the type.
    private readonly Dictionary<string, List<Node>> _typeBlocks = new(StringComparer.Ordinal);

    // Type declaration or extension (by source span) → its dotted type path.
    private readonly Dictionary<(int Start, int End), string> _blockPaths = new();

    private SwiftCallSites(Node root) => IndexTypes(root, prefix: null);

    /// <summary>The call-site reader for one file.</summary>
    public static Func<CallSiteContext, CallSite?> For(Node root) => new SwiftCallSites(root).Read;

    // ── Calls ───────────────────────────────────────────────────────

    private CallSite? Read(CallSiteContext context)
    {
        var call = context.CallNode;
        if (call.Type == "constructor_expression")
        {
            var constructed = TypePath(call.GetChildForField("constructed_type"));
            return constructed is null
                ? null
                : new CallSite("init", ResolveType(constructed, context), CallReceiverKind.Resolved,
                    Labels(call.NamedChildren.FirstOrDefault(child => child.Type == "constructor_suffix")));
        }

        var suffix = call.NamedChildren.FirstOrDefault(child => child.Type == "call_suffix");
        var function = call.NamedChildren.FirstOrDefault();
        if (suffix is null || function is null || function.Type == "call_suffix")
            return null;

        return ReadCallee(function, call, Labels(suffix), context);
    }

    private CallSite? ReadCallee(Node function, Node call, string labels, CallSiteContext context)
    {
        switch (function.Type)
        {
            case "simple_identifier":
            {
                var name = function.Text;
                if (IsTypeName(name))
                    return new CallSite("init", ResolveType(name, context), CallReceiverKind.Resolved, labels);

                // An unqualified call of a method the enclosing type declares is a call on self.
                if (EnclosingTypePath(call) is { } enclosing && DeclaresFunction(enclosing, name)
                    && (context.EnclosingTypeQN ?? context.EnclosingOwnerName) is { } owner)
                {
                    return new CallSite(name, owner, CallReceiverKind.Resolved, labels);
                }

                // Calling a closure held by a parameter, local or property (`completion(1)`) is
                // not a call of a function of that name.
                if (IsBoundVariable(name, function))
                    return new CallSite(name, null, CallReceiverKind.Unresolved, labels);

                return new CallSite(name, null, CallReceiverKind.Bare, labels);
            }

            case "navigation_expression":
            {
                var member = function.GetChildForField("suffix")?.GetChildForField("suffix");
                if (member is null || member.Type != "simple_identifier")
                    return null;

                var receiver = InferType(function.GetChildForField("target"), context, depth: 0);
                return receiver is null
                    ? new CallSite(member.Text, null, CallReceiverKind.Unresolved, labels)
                    : new CallSite(member.Text, receiver.Receiver, CallReceiverKind.Resolved, labels);
            }

            case "prefix_expression":
            {
                var target = function.GetChildForField("target");
                if (target is null)
                    return null;

                // `!isValid()` / `-value()`: the operator applies to the call's result.
                if (function.Children.FirstOrDefault()?.Type != ".")
                    return ReadCallee(target, call, labels, context);

                // `.init(...)` / `.make(...)`: an implicit member of the contextual type.
                if (target.Type != "simple_identifier")
                    return null;
                var contextual = ContextualType(call, context);
                return contextual is null
                    ? new CallSite(target.Text, null, CallReceiverKind.Unresolved, labels)
                    : new CallSite(target.Text, contextual.Receiver, CallReceiverKind.Resolved, labels);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// The call's argument-label signature: one segment per argument (<c>_</c> when unlabeled),
    /// then one per trailing closure — <c>*</c> for the first, which binds to a parameter of any
    /// label, and its label for each later one.
    /// </summary>
    private static string Labels(Node? suffix)
    {
        if (suffix is null)
            return "";

        var labels = new List<string>();
        var arguments = suffix.NamedChildren.FirstOrDefault(child => child.Type == "value_arguments");
        foreach (var argument in arguments?.NamedChildren.Where(child => child.Type == "value_argument") ?? [])
            labels.Add(argument.GetChildForField("name")?.Text.Trim() is { Length: > 0 } label ? label : "_");

        string? pendingLabel = null;
        var trailingClosures = 0;
        var index = 0;
        foreach (var child in suffix.Children)
        {
            var field = suffix.GetFieldNameForChild(index++);
            if (field == "name")
            {
                pendingLabel = child.Text;
            }
            else if (child.Type == "lambda_literal")
            {
                labels.Add(trailingClosures++ == 0 ? ArgumentLabels.AnyLabel : pendingLabel ?? "_");
                pendingLabel = null;
            }
        }

        return string.Concat(labels.Select(label => label + ":"));
    }

    // ── Receiver inference ──────────────────────────────────────────

    /// <summary>
    /// A receiver's type: its dotted <paramref name="Path"/> as written, the
    /// <paramref name="Receiver"/> reported to the pipeline (a type declared in this file by
    /// qualified name, else the simple name the pipeline resolves) and whether the expression
    /// names the type itself rather than a value of it.
    /// </summary>
    private sealed record TypeRef(string Path, string Receiver, bool IsType);

    private TypeRef? InferType(Node? expression, CallSiteContext context, int depth)
    {
        if (expression is null || depth > MaxInferenceDepth)
            return null;

        switch (expression.Type)
        {
            case "self_expression":
                return EnclosingType(expression, context, isType: false);

            case "simple_identifier":
            {
                var name = expression.Text;
                if (name == "Self")
                    return EnclosingType(expression, context, isType: true);
                if (IsTypeName(name))
                    return new TypeRef(name, ResolveType(name, context), IsType: true);
                return LookupVariable(name, expression, context, depth) is { } path
                    ? new TypeRef(path, ResolveType(path, context), IsType: false)
                    : null;
            }

            case "navigation_expression":
            {
                var owner = InferType(expression.GetChildForField("target"), context, depth + 1);
                var member = expression.GetChildForField("suffix")?.GetChildForField("suffix");
                if (owner is null || member is null || member.Type != "simple_identifier")
                    return null;

                if (owner.IsType && IsTypeName(member.Text))
                {
                    var nested = $"{owner.Path}.{member.Text}";
                    return new TypeRef(nested, ResolveType(nested, context), IsType: true);
                }

                return MemberType(owner.Path, member.Text, context, depth + 1) is { } memberPath
                    ? new TypeRef(memberPath, ResolveType(memberPath, context), IsType: false)
                    : null;
            }

            // `value!` / `value?` unwrap to the value's type.
            case "postfix_expression":
                return InferType(expression.GetChildForField("target"), context, depth + 1);

            case "try_expression" or "await_expression":
                return InferType(expression.GetChildForField("expr"), context, depth + 1);

            // An initializer call yields a value of its type.
            case "call_expression" or "constructor_expression":
                return InitializedTypePath(expression) is { } initialized
                    ? new TypeRef(initialized, ResolveType(initialized, context), IsType: false)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// The written path of the type an initializer call constructs — <c>T(...)</c>,
    /// <c>T.init(...)</c>, <c>T&lt;U&gt;(...)</c> — read from syntax alone so inference cannot
    /// recurse through it. Null for any other call.
    /// </summary>
    private static string? InitializedTypePath(Node call)
    {
        if (call.Type == "constructor_expression")
            return TypePath(call.GetChildForField("constructed_type"));

        var function = call.NamedChildren.FirstOrDefault();
        switch (function?.Type)
        {
            case "simple_identifier" when IsTypeName(function.Text):
                return function.Text;
            case "navigation_expression"
                when function.GetChildForField("suffix")?.GetChildForField("suffix")?.Text == "init":
                var target = function.GetChildForField("target");
                return target?.Type == "simple_identifier" && IsTypeName(target.Text) ? target.Text : null;
            default:
                return null;
        }
    }

    private TypeRef? EnclosingType(Node node, CallSiteContext context, bool isType) =>
        EnclosingTypePath(node) is { } path
            ? new TypeRef(path, context.EnclosingTypeQN ?? context.EnclosingOwnerName ?? path, isType)
            : null;

    /// <summary>The type an implicit member expression (<c>.init(...)</c>) is resolved against.</summary>
    private TypeRef? ContextualType(Node call, CallSiteContext context)
    {
        var parent = call.Parent;
        Node? annotation = null;
        if (parent?.Type == "property_declaration")
        {
            annotation = parent.NamedChildren.FirstOrDefault(child => child.Type == "type_annotation")
                ?.GetChildForField("name");
        }
        else if (parent?.Type == "control_transfer_statement")
        {
            var callable = Ancestors(parent).FirstOrDefault(ancestor =>
                CallableNodes.Contains(ancestor.Type) || ancestor.Type == "lambda_literal");
            annotation = callable?.Type == "function_declaration"
                ? callable.GetChildForField("return_type")
                : null;
        }

        if (annotation?.Text == "Self")
            return EnclosingType(call, context, isType: true);

        return TypePath(annotation) is { } path
            ? new TypeRef(path, ResolveType(path, context), IsType: true)
            : null;
    }

    /// <summary>
    /// The written type of the variable <paramref name="name"/> visible at <paramref name="from"/>,
    /// or null when it is unknown (including when a closure parameter or loop variable shadows it).
    /// </summary>
    private string? LookupVariable(string name, Node from, CallSiteContext context, int depth)
    {
        var child = from;
        for (var scope = from.Parent; scope is not null; child = scope, scope = scope.Parent)
        {
            switch (scope.Type)
            {
                case "statements" or "source_file":
                {
                    Node? binding = null;
                    foreach (var statement in scope.NamedChildren)
                    {
                        if (scope.Type == "statements" && statement.StartIndex >= child.StartIndex)
                            break;
                        if (statement.Type == "property_declaration" && BoundName(statement) == name
                            || statement.Type == "guard_statement" && BindsName(statement, name))
                        {
                            binding = statement;
                        }
                    }

                    if (binding is not null)
                    {
                        return binding.Type == "property_declaration"
                            ? DeclaredType(binding, context, depth + 1)
                            : ConditionalBindingType(binding, name, context, depth + 1);
                    }

                    break;
                }

                case "if_statement" or "while_statement" when BindsName(scope, name):
                    return ConditionalBindingType(scope, name, context, depth + 1);

                case "for_statement" when scope.GetChildForField("item")
                    ?.GetChildForField("bound_identifier")?.Text == name:
                    return null;

                case "lambda_literal" when ClosureParameters(scope).Contains(name):
                    return null;

                case "function_declaration" or "init_declaration" or "subscript_declaration":
                    foreach (var parameter in scope.NamedChildren.Where(node => node.Type == "parameter"))
                    {
                        var (_, internalName, type) = ParameterParts(parameter);
                        if (internalName == name)
                            return TypePath(type);
                    }

                    break;

                // Inside a type body, an unqualified name may be a property of the type.
                case "class_body" or "protocol_body" or "enum_class_body":
                    if (scope.Parent is { } declaration && PathOf(declaration) is { } typePath
                        && MemberType(typePath, name, context, depth + 1) is { } memberType)
                    {
                        return memberType;
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="name"/> names a variable visible at <paramref name="from"/>: a
    /// parameter, closure parameter, loop variable, <c>if let</c>/<c>guard let</c> binding, local
    /// or global declaration, or a property of an enclosing type declared in the file.
    /// </summary>
    private bool IsBoundVariable(string name, Node from)
    {
        var child = from;
        for (var scope = from.Parent; scope is not null; child = scope, scope = scope.Parent)
        {
            switch (scope.Type)
            {
                case "statements" or "source_file":
                    foreach (var statement in scope.NamedChildren)
                    {
                        if (scope.Type == "statements" && statement.StartIndex >= child.StartIndex)
                            break;
                        if (statement.Type == "property_declaration" && BoundName(statement) == name
                            || statement.Type == "guard_statement" && BindsName(statement, name))
                        {
                            return true;
                        }
                    }

                    break;

                case "if_statement" or "while_statement" when BindsName(scope, name):
                    return true;

                case "for_statement" when scope.GetChildForField("item")
                    ?.GetChildForField("bound_identifier")?.Text == name:
                    return true;

                case "lambda_literal" when ClosureParameters(scope).Contains(name):
                    return true;

                case "function_declaration" or "init_declaration" or "subscript_declaration"
                    when scope.NamedChildren.Any(node =>
                        node.Type == "parameter" && ParameterParts(node).Internal == name):
                    return true;

                case "class_body" or "protocol_body" or "enum_class_body"
                    when scope.Parent is { } declaration && PathOf(declaration) is { } typePath
                        && _typeBlocks.TryGetValue(typePath, out var blocks)
                        && blocks.Any(block => block.GetChildForField("body")?.NamedChildren.Any(member =>
                            member.Type is "property_declaration" or "protocol_property_declaration"
                            && BoundName(member) == name) == true):
                    return true;
            }
        }

        return false;
    }

    /// <summary>The written type of property <paramref name="member"/> of the type at <paramref name="typePath"/>.</summary>
    private string? MemberType(string typePath, string member, CallSiteContext context, int depth)
    {
        if (depth > MaxInferenceDepth || !_typeBlocks.TryGetValue(typePath, out var blocks))
            return null;

        foreach (var block in blocks)
        {
            var body = block.GetChildForField("body");
            foreach (var declaration in body?.NamedChildren ?? [])
            {
                if (declaration.Type is "property_declaration" or "protocol_property_declaration"
                    && BoundName(declaration) == member
                    && DeclaredType(declaration, context, depth + 1) is { } type)
                {
                    return type;
                }
            }
        }

        return null;
    }

    /// <summary>A declaration's type annotation, else the type of its initial value.</summary>
    private string? DeclaredType(Node declaration, CallSiteContext context, int depth)
    {
        var annotation = declaration.NamedChildren.FirstOrDefault(child => child.Type == "type_annotation");
        if (annotation is not null)
            return TypePath(annotation.GetChildForField("name") ?? annotation.NamedChildren.LastOrDefault());

        return InferType(declaration.GetChildForField("value"), context, depth) is { IsType: false } value
            ? value.Path
            : null;
    }

    /// <summary>The type an <c>if let</c> / <c>guard let</c> binding of <paramref name="name"/> unwraps.</summary>
    private string? ConditionalBindingType(Node statement, string name, CallSiteContext context, int depth)
    {
        var children = statement.Children.ToList();
        var index = -1;
        for (var i = 0; i < children.Count && index < 0; i++)
        {
            if (statement.GetFieldNameForChild(i) == "bound_identifier" && children[i].Text == name)
                index = i;
        }

        if (index < 0)
            return null;

        var value = children.Skip(index + 1).FirstOrDefault(child => child.IsNamed && child.Type != "value_binding_pattern");
        // `if let value {` (Swift 5.7 shorthand) unwraps the outer variable of the same name.
        if (value is null || value.Type is "statements" or "else")
            return LookupVariable(name, statement, context, depth + 1);

        return InferType(value, context, depth) is { IsType: false } type ? type.Path : null;
    }

    private static bool BindsName(Node statement, string name)
    {
        var index = 0;
        foreach (var child in statement.Children)
        {
            if (statement.GetFieldNameForChild(index++) == "bound_identifier" && child.Text == name)
                return true;
        }

        return false;
    }

    private static HashSet<string> ClosureParameters(Node closure)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var type = closure.GetChildForField("type");
        if (type is null)
            return names;

        foreach (var node in Descendants(type))
        {
            if (node.Type == "lambda_parameter" && node.GetChildForField("name") is { } parameterName)
                names.Add(parameterName.Text);
            else if (node.Type == "simple_identifier" && node.Parent?.Type == "lambda_function_type_parameters")
                names.Add(node.Text);
        }

        return names;
    }

    private static string? BoundName(Node declaration) =>
        declaration.GetChildForField("name") is { } pattern
            ? pattern.GetChildForField("bound_identifier")?.Text ?? pattern.Text
            : null;

    private static string ResolveType(string path, CallSiteContext context) =>
        context.ResolveLocalType(path) ?? path[(path.LastIndexOf('.') + 1)..];

    /// <summary>Swift types are UpperCamelCase; values and functions are lowerCamelCase.</summary>
    private static bool IsTypeName(string name) => name.Length > 0 && char.IsUpper(name[0]);

    // ── Types declared in the file ──────────────────────────────────

    private void IndexTypes(Node node, string? prefix)
    {
        foreach (var child in node.NamedChildren)
        {
            if (!TypeDeclarationNodes.Contains(child.Type))
            {
                IndexTypes(child, prefix);
                continue;
            }

            var isExtension = child.GetChildForField("declaration_kind")?.Text == "extension";
            var name = isExtension
                ? LanguageSpecs.GetSwiftTypePath(child.GetChildForField("name"))
                : child.GetChildForField("name")?.Text;
            if (string.IsNullOrWhiteSpace(name))
            {
                IndexTypes(child, prefix);
                continue;
            }

            var path = isExtension || prefix is null ? name : $"{prefix}.{name}";
            if (!_typeBlocks.TryGetValue(path, out var blocks))
                _typeBlocks[path] = blocks = [];
            blocks.Add(child);
            _blockPaths[(child.StartIndex, child.EndIndex)] = path;
            IndexTypes(child, path);
        }
    }

    private string? PathOf(Node declaration) =>
        _blockPaths.GetValueOrDefault((declaration.StartIndex, declaration.EndIndex));

    /// <summary>Path of the type (or extended type) whose body contains <paramref name="node"/>.</summary>
    private string? EnclosingTypePath(Node node) =>
        Ancestors(node).Where(ancestor => TypeDeclarationNodes.Contains(ancestor.Type))
            .Select(PathOf)
            .FirstOrDefault(path => path is not null);

    private bool DeclaresFunction(string typePath, string name) =>
        _typeBlocks.TryGetValue(typePath, out var blocks)
        && blocks.Any(block => block.GetChildForField("body")?.NamedChildren.Any(member =>
            member.Type is "function_declaration" or "protocol_function_declaration"
            && member.GetChildForField("name")?.Text == name) == true);

    private static IEnumerable<Node> Ancestors(Node node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
            yield return current;
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.NamedChildren)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    // ── Shared syntax helpers ───────────────────────────────────────

    /// <summary>
    /// Dotted path of a nominal type, without generic arguments; <c>T?</c> and <c>T!</c> unwrap to
    /// <c>T</c>. Null for structural types (arrays, dictionaries, tuples, functions).
    /// </summary>
    internal static string? TypePath(Node? type)
    {
        switch (type?.Type)
        {
            case "type_identifier":
                return type.Text;
            case "optional_type":
                return TypePath(type.GetChildForField("wrapped") ?? type.NamedChildren.FirstOrDefault());
            case "user_type":
                var parts = type.NamedChildren
                    .Where(child => child.Type == "type_identifier")
                    .Select(child => child.Text)
                    .ToList();
                return parts.Count == 0 ? null : string.Join('.', parts);
            default:
                return null;
        }
    }

    /// <summary>A parameter's external label (if written), internal name and type node.</summary>
    private static (string? External, string? Internal, Node? Type) ParameterParts(Node parameter)
    {
        string? internalName = null;
        Node? type = null;
        var index = 0;
        foreach (var child in parameter.Children)
        {
            if (parameter.GetFieldNameForChild(index++) != "name")
                continue;
            if (child.Type == "simple_identifier" && internalName is null)
                internalName = child.Text;
            else
                type ??= child;
        }

        return (parameter.GetChildForField("external_name")?.Text, internalName, type);
    }

    /// <summary>
    /// A function's or initializer's argument-label signature (format: <see cref="ArgumentLabels"/>):
    /// each parameter's external label, else its name; <c>=</c> marks a default value and
    /// <c>...</c> a variadic parameter.
    /// </summary>
    internal static string? DeclarationLabels(Node declaration)
    {
        if (declaration.Type is not ("function_declaration" or "init_declaration" or "protocol_function_declaration"))
            return null;

        var labels = new List<string>();
        var index = 0;
        foreach (var child in declaration.Children)
        {
            var field = declaration.GetFieldNameForChild(index++);
            if (child.Type == "parameter")
            {
                var (external, internalName, _) = ParameterParts(child);
                var label = external ?? internalName ?? "_";
                labels.Add(child.Text.TrimEnd().EndsWith("...", StringComparison.Ordinal) ? label + "..." : label);
            }
            else if (field == "default_value" && labels.Count > 0)
            {
                labels[^1] += "=";
            }
        }

        return string.Concat(labels.Select(label => label + ":"));
    }

    /// <summary>
    /// <c>import M</c>, <c>@testable import M</c> and <c>import M.Sub</c> import module <c>M</c>
    /// (<c>M.Sub</c>); <c>import struct M.Symbol</c> (or class/enum/protocol/typealias/func/let/var)
    /// imports one symbol of module <c>M</c>.
    /// </summary>
    internal static ImportDeclaration? ReadImport(Node declaration)
    {
        var path = declaration.NamedChildren.FirstOrDefault(child => child.Type == "identifier");
        var parts = path?.NamedChildren
            .Where(child => child.Type == "simple_identifier")
            .Select(child => child.Text)
            .ToList() ?? [];
        if (parts.Count == 0)
            return null;

        var isScoped = declaration.Children.Any(child => !child.IsNamed && ScopedImportKinds.Contains(child.Type));
        return isScoped && parts.Count > 1
            ? new ImportDeclaration(string.Join('.', parts.Take(parts.Count - 1)), parts[^1])
            : new ImportDeclaration(string.Join('.', parts));
    }
}
