using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Which languages a call can cross when it is resolved by name. A call binds to a definition of
/// its own language family, plus the foreign-function boundaries that really link by symbol name:
/// Swift and Rust call C/C++/Objective-C functions, and Swift and C call Rust functions exported
/// with a C ABI under their own symbol name (<c>#[no_mangle] extern "C" fn</c>). A definition or
/// caller whose language is unknown (a stub, a file of an unmapped extension, a node without a
/// file) is not filtered by language, but only callables (and, within a family, the types a call
/// constructs) are ever call targets (<see cref="IsCallTarget"/>).
/// </summary>
public static partial class CallLanguages
{
    /// <summary>Calling convention of a function exported for foreign callers (e.g. <c>C</c>).</summary>
    public const string AbiProperty = "abi";

    private const string CFamily = "c";
    private const string Rust = "rust";
    private const string Swift = "swift";

    private static readonly Dictionary<string, string> FamilyByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".c"] = CFamily, [".h"] = CFamily, [".cc"] = CFamily, [".cpp"] = CFamily, [".cxx"] = CFamily,
        [".hpp"] = CFamily, [".hxx"] = CFamily, [".hh"] = CFamily, [".m"] = CFamily, [".mm"] = CFamily,
        [".swift"] = Swift,
        [".rs"] = Rust,
        [".py"] = "python", [".pyw"] = "python", [".pyi"] = "python",
        [".ts"] = "javascript", [".tsx"] = "javascript", [".mts"] = "javascript", [".cts"] = "javascript",
        [".js"] = "javascript", [".jsx"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
        [".cs"] = "dotnet", [".fs"] = "dotnet", [".vb"] = "dotnet",
        [".java"] = "jvm", [".kt"] = "jvm", [".kts"] = "jvm", [".scala"] = "jvm",
        [".go"] = "go",
        [".rb"] = "ruby",
        [".php"] = "php",
        [".sh"] = "shell", [".bash"] = "shell", [".zsh"] = "shell",
        [".cfm"] = "cfml", [".cfc"] = "cfml",
        [".sql"] = "sql"
    };

    /// <summary>Definitions a call invokes directly.</summary>
    private static readonly HashSet<NodeLabel> CallableLabels =
    [
        NodeLabel.Function, NodeLabel.Method, NodeLabel.Constructor, NodeLabel.Delegate, NodeLabel.ExternalSymbol
    ];

    /// <summary>Types a call constructs (<c>Foo()</c> in Python, Swift, Kotlin; a Rust tuple struct).</summary>
    private static readonly HashSet<NodeLabel> ConstructibleTypeLabels =
    [
        NodeLabel.Class, NodeLabel.Struct, NodeLabel.Record, NodeLabel.Enum,
        NodeLabel.Component, NodeLabel.Service, NodeLabel.Job
    ];

    /// <summary>The language family of a file, or null for an unmapped extension or no file.</summary>
    public static string? FamilyOf(string? filePath) =>
        string.IsNullOrWhiteSpace(filePath)
            ? null
            : FamilyByExtension.GetValueOrDefault(Path.GetExtension(filePath));

    /// <summary>
    /// The language family of the file a qualified name of <paramref name="project"/> is declared
    /// in (<c>{project}:{file}</c> or <c>{project}:{file}#...</c>); null for any other shape.
    /// </summary>
    public static string? FamilyOfQualifiedName(string project, string qualifiedName)
    {
        var prefix = project + ":";
        if (!qualifiedName.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var rest = qualifiedName[prefix.Length..];
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        return FamilyOf(hash < 0 ? rest : rest[..hash]);
    }

    /// <summary>
    /// The language family of a definition: its file's, or Rust for a SCIP symbol without a file
    /// (rust-analyzer's external symbols).
    /// </summary>
    private static string? FamilyOf(GraphNode node) =>
        FamilyOf(node.FilePath)
        ?? (Text(node.Properties.GetValueOrDefault("source")) == "scip" ? Rust : null);

    /// <summary>
    /// Whether a call from a file of <paramref name="callerFamily"/> may bind to
    /// <paramref name="candidate"/> by name. Only callables are call targets (a package, module,
    /// file or namespace of the same name never is); a type is one only as a constructor call
    /// within its own language family, since no FFI boundary links a call to a foreign type.
    /// </summary>
    public static bool IsCallTarget(string? callerFamily, GraphNode candidate)
    {
        if (CallableLabels.Contains(candidate.Label))
            return CanCall(callerFamily, candidate);
        if (!ConstructibleTypeLabels.Contains(candidate.Label))
            return false;

        var candidateFamily = FamilyOf(candidate);
        return callerFamily is null || candidateFamily is null || callerFamily == candidateFamily;
    }

    /// <summary>Whether a call from a file of <paramref name="callerFamily"/> may bind to <paramref name="candidate"/>.</summary>
    public static bool CanCall(string? callerFamily, GraphNode candidate)
    {
        var candidateFamily = FamilyOf(candidate);
        if (callerFamily is null || candidateFamily is null || callerFamily == candidateFamily)
            return true;

        return candidateFamily switch
        {
            CFamily => callerFamily is Swift or Rust,
            Rust => callerFamily is Swift or CFamily && ExportsCAbi(candidate),
            _ => false
        };
    }

    /// <summary>
    /// Whether a Rust function is exported for foreign callers by name: its <see cref="AbiProperty"/>
    /// (recorded by the tree-sitter extractor and the SCIP importer only for an <c>extern "abi" fn</c>
    /// whose symbol is not mangled, <c>#[no_mangle]</c> / <c>#[export_name]</c>) names a non-Rust ABI.
    /// </summary>
    public static bool ExportsCAbi(GraphNode node) =>
        Text(node.Properties.GetValueOrDefault(AbiProperty)) is { Length: > 0 } abi && abi != "Rust";

    /// <summary>
    /// The ABI of an <c>extern "abi" fn</c> signature (<c>C</c> for a bare <c>extern fn</c>), or null
    /// when the signature declares no extern function.
    /// </summary>
    public static string? ParseExternAbi(string? signature)
    {
        if (signature is null || ExternRegex().Match(signature) is not { Success: true } match)
            return null;
        return match.Groups["abi"].Success && match.Groups["abi"].Value.Length > 0
            ? match.Groups["abi"].Value
            : "C";
    }

    private static string? Text(object? value) => value switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
        JsonElement json => json.ToString(),
        _ => value.ToString()
    };

    // `extern "C" fn`, `extern fn` (C), `unsafe extern "C-unwind" fn`.
    [GeneratedRegex("""\bextern(?:\s+"(?<abi>[^"]*)")?\s+fn\b""")]
    private static partial Regex ExternRegex();
}
