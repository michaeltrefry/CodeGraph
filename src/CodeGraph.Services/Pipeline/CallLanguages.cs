using System.Text.Json;
using System.Text.RegularExpressions;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

/// <summary>
/// Which languages a call can cross when it is resolved by name. A call binds to a definition of
/// its own language family, plus the foreign-function boundaries that really link by symbol name:
/// Swift and Rust call C/C++/Objective-C functions, and Swift and C call Rust functions exported
/// with a C ABI (<c>extern "C" fn</c>). A definition or caller whose language is unknown (a stub,
/// a file of an unmapped extension, a node without a file) is not filtered.
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

    /// <summary>The language family of a file, or null for an unmapped extension or no file.</summary>
    public static string? FamilyOf(string? filePath) =>
        string.IsNullOrWhiteSpace(filePath)
            ? null
            : FamilyByExtension.GetValueOrDefault(Path.GetExtension(filePath));

    /// <summary>Whether a call from a file of <paramref name="callerFamily"/> may bind to <paramref name="candidate"/>.</summary>
    public static bool CanCall(string? callerFamily, GraphNode candidate)
    {
        var candidateFamily = FamilyOf(candidate.FilePath);
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
    /// Whether a Rust function is exported with a foreign (non-Rust) ABI: the tree-sitter
    /// extractor's <see cref="AbiProperty"/>, or an <c>extern "abi"</c> in a SCIP signature.
    /// </summary>
    public static bool ExportsCAbi(GraphNode node)
    {
        if (Text(node.Properties.GetValueOrDefault(AbiProperty)) is { Length: > 0 } abi)
            return abi != "Rust";

        return Text(node.Properties.GetValueOrDefault("signature")) is { } signature
               && ExternRegex().Match(signature) is { Success: true } match
               && match.Groups["abi"].Value != "Rust";
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
