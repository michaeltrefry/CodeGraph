using CodeGraph.Models;
using TreeSitter;

namespace CodeGraph.Extractors.TreeSitter;

/// <summary>
/// Registry of all tree-sitter language specs, keyed by file extension.
/// Add new languages by defining a spec and mapping its extensions.
/// </summary>
public static class LanguageSpecs
{
    private static readonly Dictionary<string, LanguageSpec> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    static LanguageSpecs()
    {
        Register(C, ".c", ".h");
        Register(Cpp, ".cpp", ".cc", ".cxx", ".hpp", ".hxx", ".hh");
        Register(Python, ".py", ".pyw");
        Register(Go, ".go");
        Register(Java, ".java");
        Register(Ruby, ".rb");
        Register(Rust, ".rs");
        Register(Php, ".php");
        Register(Bash, ".sh", ".bash", ".zsh");
    }

    public static LanguageSpec? ForExtension(string extension) =>
        ByExtension.GetValueOrDefault(extension);

    public static IReadOnlyCollection<string> SupportedExtensions => ByExtension.Keys;

    private static void Register(LanguageSpec spec, params string[] extensions)
    {
        foreach (var ext in extensions)
            ByExtension[ext] = spec;
    }

    // ── C ───────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the function/type name from a C node.
    /// C nests function names at declarator.declarator (function_declarator → identifier).
    /// Typedef names are at the declarator field directly.
    /// Struct/enum names are at the name field.
    /// </summary>
    private static string? ExtractCName(Node node)
    {
        // function_definition: name is at declarator.declarator
        if (node.Type == "function_definition")
        {
            var funcDecl = node.GetChildForField("declarator"); // function_declarator
            if (funcDecl is null) return null;
            var ident = funcDecl.GetChildForField("declarator"); // identifier
            return ident?.Text;
        }

        // type_definition (typedef struct { ... } name_t): name is at declarator
        if (node.Type == "type_definition")
            return node.GetChildForField("declarator")?.Text;

        // struct_specifier / enum_specifier: name field
        return node.GetChildForField("name")?.Text;
    }

    public static readonly LanguageSpec C = new()
    {
        LanguageName = "C",
        Framework = null,
        GetLanguage = () => new Language("C"),
        FunctionNodeTypes = ["function_definition"],
        ClassNodeTypes = ["struct_specifier", "enum_specifier", "type_definition"],
        CallNodeTypes = ["call_expression"],
        ImportNodeTypes = ["preproc_include"],
        VariableNodeTypes = ["declaration"],
        NameExtractor = ExtractCName,
        ReturnTypeField = "type",
        ParametersField = null, // parameters are nested in declarator
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Struct
    };

    // ── C++ ─────────────────────────────────────────────────────────

    /// <summary>
    /// C++ name extraction — same as C for functions, plus class_specifier/namespace.
    /// </summary>
    private static string? ExtractCppName(Node node)
    {
        if (node.Type is "function_definition")
        {
            var funcDecl = node.GetChildForField("declarator");
            if (funcDecl is null) return null;
            var ident = funcDecl.GetChildForField("declarator");
            return ident?.Text;
        }

        if (node.Type == "type_definition")
            return node.GetChildForField("declarator")?.Text;

        // class_specifier, struct_specifier, enum_specifier, namespace_definition
        return node.GetChildForField("name")?.Text;
    }

    public static readonly LanguageSpec Cpp = new()
    {
        LanguageName = "C++",
        Framework = null,
        GetLanguage = () => new Language("Cpp"),
        FunctionNodeTypes = ["function_definition"],
        ClassNodeTypes = ["class_specifier", "struct_specifier", "enum_specifier",
                          "type_definition", "namespace_definition"],
        CallNodeTypes = ["call_expression"],
        ImportNodeTypes = ["preproc_include"],
        VariableNodeTypes = ["declaration"],
        NameExtractor = ExtractCppName,
        ReturnTypeField = "type",
        ParametersField = null,
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    // ── Python ──────────────────────────────────────────────────────

    public static readonly LanguageSpec Python = new()
    {
        LanguageName = "Python",
        Framework = null,
        GetLanguage = () => new Language("Python"),
        FunctionNodeTypes = ["function_definition"],
        ClassNodeTypes = ["class_definition"],
        CallNodeTypes = ["call"],
        ImportNodeTypes = ["import_statement", "import_from_statement"],
        VariableNodeTypes = ["assignment"],
        NameField = "name",
        ReturnTypeField = "return_type",
        ParametersField = "parameters",
        BodyField = "body",
        SuperclassField = "superclasses",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    // ── Go ──────────────────────────────────────────────────────────

    public static readonly LanguageSpec Go = new()
    {
        LanguageName = "Go",
        Framework = null,
        GetLanguage = () => new Language("Go"),
        FunctionNodeTypes = ["function_declaration", "method_declaration"],
        ClassNodeTypes = ["type_declaration"],
        CallNodeTypes = ["call_expression"],
        ImportNodeTypes = ["import_declaration"],
        VariableNodeTypes = ["short_var_declaration", "var_declaration"],
        NameField = "name",
        ReturnTypeField = "result",
        ParametersField = "parameters",
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    // ── Java ────────────────────────────────────────────────────────

    public static readonly LanguageSpec Java = new()
    {
        LanguageName = "Java",
        Framework = null,
        GetLanguage = () => new Language("Java"),
        FunctionNodeTypes = ["method_declaration", "constructor_declaration"],
        ClassNodeTypes = ["class_declaration", "interface_declaration", "enum_declaration"],
        CallNodeTypes = ["method_invocation"],
        ImportNodeTypes = ["import_declaration"],
        VariableNodeTypes = ["local_variable_declaration", "field_declaration"],
        NameField = "name",
        ReturnTypeField = "type",
        ParametersField = "parameters",
        BodyField = "body",
        SuperclassField = "superclass",
        FunctionLabel = NodeLabel.Method,
        ClassLabel = NodeLabel.Class
    };

    // ── Ruby ────────────────────────────────────────────────────────

    public static readonly LanguageSpec Ruby = new()
    {
        LanguageName = "Ruby",
        Framework = null,
        GetLanguage = () => new Language("Ruby"),
        FunctionNodeTypes = ["method", "singleton_method"],
        ClassNodeTypes = ["class", "module"],
        CallNodeTypes = ["call", "method_call"],
        ImportNodeTypes = [],
        VariableNodeTypes = ["assignment"],
        NameField = "name",
        ParametersField = "parameters",
        BodyField = "body",
        SuperclassField = "superclass",
        FunctionLabel = NodeLabel.Method,
        ClassLabel = NodeLabel.Class
    };

    // ── Rust ────────────────────────────────────────────────────────

    public static readonly LanguageSpec Rust = new()
    {
        LanguageName = "Rust",
        Framework = null,
        GetLanguage = () => new Language("Rust"),
        FunctionNodeTypes = ["function_item"],
        ClassNodeTypes = ["struct_item", "enum_item", "trait_item", "impl_item"],
        CallNodeTypes = ["call_expression"],
        ImportNodeTypes = ["use_declaration"],
        VariableNodeTypes = ["let_declaration", "const_item", "static_item"],
        NameField = "name",
        ReturnTypeField = "return_type",
        ParametersField = "parameters",
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    // ── PHP ─────────────────────────────────────────────────────────

    public static readonly LanguageSpec Php = new()
    {
        LanguageName = "PHP",
        Framework = null,
        GetLanguage = () => new Language("Php"),
        FunctionNodeTypes = ["function_definition", "method_declaration"],
        ClassNodeTypes = ["class_declaration", "interface_declaration", "trait_declaration"],
        CallNodeTypes = ["function_call_expression", "member_call_expression"],
        ImportNodeTypes = ["namespace_use_declaration"],
        VariableNodeTypes = ["property_declaration"],
        NameField = "name",
        ReturnTypeField = "return_type",
        ParametersField = "parameters",
        BodyField = "body",
        FunctionLabel = NodeLabel.Function,
        ClassLabel = NodeLabel.Class
    };

    // ── Bash ────────────────────────────────────────────────────────

    public static readonly LanguageSpec Bash = new()
    {
        LanguageName = "Bash",
        Framework = null,
        GetLanguage = () => new Language("Bash"),
        FunctionNodeTypes = ["function_definition"],
        ClassNodeTypes = [],
        CallNodeTypes = ["command"],
        ImportNodeTypes = [],
        VariableNodeTypes = ["variable_assignment"],
        NameField = "name",
        BodyField = "body",
        FunctionLabel = NodeLabel.Function
    };

}
