namespace CodeGraph.Services.Extractors;

/// <summary>A file's source language and whether CodeGraph indexes it.</summary>
/// <param name="Indexed">
/// False only for a source file in a language CodeGraph does not index; true for an indexed
/// language and for a non-code file (documentation, configuration, data, images, ...).
/// </param>
/// <param name="Language">The capitalised language name ("Swift", "C#", "Objective-C"), or null for a non-code file.</param>
public sealed record FileLanguage(bool Indexed, string? Language);

/// <summary>
/// Says whether a file's language is indexed. "Indexed" is the set of extensions indexing discovers:
/// the registered extractors' <see cref="ICodeExtractor.SupportedExtensions"/>, matched by ordinal
/// (case-sensitive) comparison through <see cref="ExtractorExtensions"/>, the predicate default-branch
/// discovery, overlay filtering and extraction dispatch share, so <c>Foo.SWIFT</c> is not indexed. Repository analyzers
/// share the frontend extensions registered by TypeScriptExtractor: JS, JSX, TS and TSX.
/// <see cref="LanguageNames"/> only names languages. The answer is per language: a file
/// of an indexed language that discovery excludes (dependency directories, skip patterns), that is
/// over the size limit, or that an extractor skips (<c>.d.ts</c>, <c>.spec.ts</c>) is still indexed.
/// </summary>
public sealed class IndexedLanguageCatalog
{
    /// <summary>
    /// Display names of source languages by extension: every indexed extension, plus common source
    /// languages CodeGraph does not index, so their answers can name the language. An extension
    /// that is neither indexed nor listed here is a non-code file.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> LanguageNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Indexed by the registered extractors and analyzers.
            [".cs"] = "C#",
            [".ts"] = "TypeScript",
            [".tsx"] = "TypeScript", [".js"] = "JavaScript", [".jsx"] = "JavaScript",
            [".rs"] = "Rust",
            [".swift"] = "Swift",
            [".py"] = "Python", [".pyw"] = "Python",
            [".go"] = "Go",
            [".java"] = "Java",
            [".rb"] = "Ruby",
            [".php"] = "PHP",
            [".sh"] = "Bash", [".bash"] = "Bash", [".zsh"] = "Bash",
            // By extension alone a .h header is C, so it is indexed even in an Objective-C codebase.
            [".c"] = "C", [".h"] = "C",
            [".cpp"] = "C++", [".cc"] = "C++", [".cxx"] = "C++", [".hpp"] = "C++", [".hxx"] = "C++", [".hh"] = "C++",
            [".sql"] = "SQL",
            [".tf"] = "Terraform", [".tfvars"] = "Terraform",
            [".cfm"] = "ColdFusion", [".cfc"] = "ColdFusion",
            [".yml"] = "YAML", [".yaml"] = "YAML",

            // Source languages that are not indexed.
            [".m"] = "Objective-C", [".mm"] = "Objective-C++",
            [".kt"] = "Kotlin", [".kts"] = "Kotlin",
            [".scala"] = "Scala", [".sc"] = "Scala",
            [".dart"] = "Dart",
            [".mjs"] = "JavaScript", [".cjs"] = "JavaScript",
            [".mts"] = "TypeScript", [".cts"] = "TypeScript",
            [".pyi"] = "Python",
            [".fs"] = "F#", [".fsx"] = "F#", [".fsi"] = "F#",
            [".vb"] = "Visual Basic",
            [".groovy"] = "Groovy",
            [".lua"] = "Lua",
            [".pl"] = "Perl", [".pm"] = "Perl",
            [".r"] = "R",
            [".ex"] = "Elixir", [".exs"] = "Elixir",
            [".erl"] = "Erlang", [".hrl"] = "Erlang",
            [".hs"] = "Haskell",
            [".ml"] = "OCaml", [".mli"] = "OCaml",
            [".clj"] = "Clojure", [".cljs"] = "Clojure",
            [".jl"] = "Julia",
            [".zig"] = "Zig",
            [".nim"] = "Nim",
            [".cr"] = "Crystal",
            [".ps1"] = "PowerShell", [".psm1"] = "PowerShell",
            [".vue"] = "Vue", [".svelte"] = "Svelte",
            [".cshtml"] = "Razor", [".razor"] = "Razor",
            [".sol"] = "Solidity",
            [".f90"] = "Fortran", [".f"] = "Fortran",
            [".asm"] = "Assembly", [".s"] = "Assembly",
            [".metal"] = "Metal",
            [".cu"] = "CUDA",
            [".hcl"] = "HCL"
        };

    private readonly ExtractorExtensions _indexedExtensions;

    public IndexedLanguageCatalog(IEnumerable<ICodeExtractor> extractors)
    {
        // The same predicate as discovery, overlay filtering and extraction dispatch.
        _indexedExtensions = new ExtractorExtensions(extractors);
    }

    /// <summary>Every extension indexing discovers (case-sensitive).</summary>
    public IReadOnlySet<string> IndexedExtensions => _indexedExtensions.Extensions;

    /// <summary>The language of the file at <paramref name="path"/> and whether it is indexed.</summary>
    public FileLanguage Describe(string path)
    {
        var extension = Path.GetExtension(path);
        var language = LanguageNames.GetValueOrDefault(extension);
        if (_indexedExtensions.IsSupportedExtension(extension))
            return new FileLanguage(true, language);
        // An unindexed extension is a source file of an unindexed language when the language is
        // known; anything else (docs, config, data, images, no extension) is not code.
        return new FileLanguage(language is null, language);
    }
}
