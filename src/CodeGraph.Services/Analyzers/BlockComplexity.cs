using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.Services.Analyzers;

/// <summary>A block opening (<c>+1</c>) or closing (<c>-1</c>) on a line, in order.</summary>
/// <param name="FunctionStart">
/// On an opening: it opens a function body whose declaration started on this line index (C/C++,
/// where the declaration is only known at its body's brace; Ruby <c>def</c>).
/// </param>
/// <param name="InlineFunction">A complete one-line function with no block (a Ruby endless <c>def</c>).</param>
internal readonly record struct BlockEvent(int Delta, int? FunctionStart = null, bool InlineFunction = false);

/// <summary>What a language scanner found on one source line.</summary>
/// <param name="HasCode">False for a line that is blank once comments and literals are removed.</param>
/// <param name="DeclaresFunction">The line declares a function at the depth the line starts at.</param>
/// <param name="Branches">Control-flow tokens on the line (counted only inside function bodies).</param>
/// <param name="EndsStatement">The line ends a body-less declaration (a trailing <c>;</c>).</param>
internal sealed record BlockLine(bool HasCode, IReadOnlyList<BlockEvent> Events, bool DeclaresFunction, int Branches, bool EndsStatement);

/// <summary>Scans source lines in order, carrying lexer state across lines.</summary>
internal interface IBlockScanner
{
    BlockLine Scan(string line, int index);
}

/// <summary>
/// Complexity from blocks: nesting depth from block openings and closings, a function from its
/// declaration to the line its body closes, branches from control-flow tokens within functions.
/// Shared by the brace languages (Swift, Rust, C/C++, Go, PHP) and the keyword-delimited ones
/// (Ruby, Bash).
/// </summary>
internal static class BlockComplexity
{
    public static VitalsAnalyzer.ComplexityData Compute(string source, IBlockScanner scanner)
    {
        var lines = source.Split('\n');
        int maxDepth = 0;
        int deepLines = 0;
        int functionCount = 0;
        int longestFunction = 0;
        int branchCount = 0;
        int depth = 0;
        int? functionDepth = null;
        int functionStart = 0;

        for (var index = 0; index < lines.Length; index++)
        {
            var line = scanner.Scan(lines[index], index);
            if (!line.HasCode) continue;

            // A declaration at the depth of a still-open body-less one (a protocol requirement)
            // ends that one; a declaration inside a function body is part of it.
            if (functionDepth is { } open && depth <= open && line.DeclaresFunction)
            {
                longestFunction = Math.Max(longestFunction, index - functionStart);
                functionDepth = null;
            }

            if (functionDepth is null && line.DeclaresFunction)
            {
                functionCount++;
                functionDepth = depth;
                functionStart = index;
            }

            var inFunction = functionDepth is not null;
            var lineDepth = depth;
            foreach (var block in line.Events)
            {
                if (block.InlineFunction)
                {
                    if (functionDepth is null)
                    {
                        functionCount++;
                        longestFunction = Math.Max(longestFunction, 1);
                    }

                    continue;
                }

                if (block.Delta > 0)
                {
                    if (block.FunctionStart is { } start && functionDepth is null)
                    {
                        functionCount++;
                        functionDepth = depth;
                        functionStart = start;
                        inFunction = true;
                    }

                    lineDepth = Math.Max(lineDepth, ++depth);
                }
                else
                {
                    depth = Math.Max(0, depth - 1);
                }
            }

            if (inFunction)
                branchCount += line.Branches;

            if (lineDepth > maxDepth) maxDepth = lineDepth;
            if (lineDepth >= 3) deepLines++;

            // The function ends on the line its body closes, or a body-less declaration (a trait
            // method, an extern fn) on the line ending it with ';' or closing its enclosing scope.
            if (functionDepth is { } functionAt
                && (depth < functionAt || (depth == functionAt && (lineDepth > functionAt || line.EndsStatement))))
            {
                longestFunction = Math.Max(longestFunction, index - functionStart + 1);
                functionDepth = null;
            }
        }

        if (functionDepth is not null)
            longestFunction = Math.Max(longestFunction, lines.Length - functionStart);

        return new VitalsAnalyzer.ComplexityData
        {
            Score = VitalsAnalyzer.ComputeComplexityScore(maxDepth, deepLines, longestFunction,
                lines.Length, branchCount, functionCount),
            MaxNestingDepth = maxDepth,
            DeepNestingLines = deepLines,
            FunctionCount = functionCount,
            LongestFunction = longestFunction,
            BranchCount = branchCount
        };
    }
}

/// <summary>
/// Brace languages: blocks from braces in the code <see cref="BraceSourceLexer"/> leaves. A
/// function is declared by a keyword on its line (Swift <c>func</c>, Rust <c>fn</c>, Go
/// <c>func</c>, PHP <c>function</c>) or, in C/C++, by a body brace following a declarator
/// <c>name(…)</c>.
/// </summary>
internal sealed class BraceBlockScanner : IBlockScanner
{
    private static readonly RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex SwiftFunction = new(@"(^|[\s(])(func\s+[^\s(<]+|init[?!]?\s*[(<]|deinit\b)", Options);
    private static readonly Regex SwiftBranch = new(@"\b(if|guard|switch|case|for|while|repeat|catch)\b|&&|\|\||\?\?|\s\?\s", Options);
    private static readonly Regex RustFunction = new(@"\bfn\s+[A-Za-z_]", Options);
    private static readonly Regex RustBranch = new(@"\b(if|match|for|while|loop)\b|=>|&&|\|\||\?(?=[\s;.)?,]|$)", Options);
    private static readonly Regex CBranch = new(@"\b(if|for|while|switch|case|catch)\b|&&|\|\||\s\?\s", Options);
    /// <summary>A function or method (<c>func (r T) Name</c>); not a function literal.</summary>
    private static readonly Regex GoFunction = new(@"^func\s*(\([^)]*\)\s*)?[A-Za-z_]", Options);
    private static readonly Regex GoBranch = new(@"\b(if|for|switch|select|case)\b|&&|\|\|", Options);
    /// <summary>A named function or method; not a closure, nor a <c>use function</c> import.</summary>
    private static readonly Regex PhpFunction = new(@"(?<!\buse\s+)\bfunction\s+&?\s*[A-Za-z_\u0080-\uffff]", Options | RegexOptions.IgnoreCase);
    private static readonly Regex PhpBranch = new(
        @"(?<!->|::|\$)\b(if|elseif|for|foreach|while|switch|case|catch|match|and|or)\b|&&|\|\||\?\?|\s\?\s",
        Options | RegexOptions.IgnoreCase);

    // C/C++ declarators.
    private static readonly Regex TrailingReturnType = new(@"\s*->[^()]*$", Options);
    private static readonly Regex TrailingQualifier = new(@"\s*(\b(const|volatile|noexcept|override|final|mutable|try)\b|&&|&)\s*$", Options);
    private static readonly Regex DeclaratorName = new(@"(~?[A-Za-z_]\w*|\boperator\s*[^\s\w(]+)\s*$", Options);
    /// <summary>A K&amp;R definition's head at its first parameter declaration: <c>int add(a, b) int a</c>.</summary>
    private static readonly Regex KrDefinition = new(
        @"^(?<declarator>.*?\b[A-Za-z_]\w*\s*\(\s*[A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*\s*\))\s+[A-Za-z_][\w\s*,\[\]]*$", Options);
    /// <summary>A K&amp;R parameter declaration: <c>char *s</c>, <c>int a, b</c>.</summary>
    private static readonly Regex KrParameter = new(@"^[A-Za-z_][\w\s*,\[\]]*$", Options);
    /// <summary>The colon opening a constructor's member initializer list: <c>Widget() : a_{1}</c>.</summary>
    private static readonly Regex InitializerList = new(@"\)\s*(?:noexcept\s*)?:(?!:)", Options);
    private static readonly Regex EndsWithName = new(@"[A-Za-z_][\w:<>]*\s*$", Options);
    private static readonly Regex AccessSpecifier = new(@"^(public|private|protected)(\s+(slots|Q_SLOTS))?$|^(signals|Q_SIGNALS)$", Options);
    private static readonly HashSet<string> NotDeclarators = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "return", "sizeof", "else", "do", "case", "new", "delete",
        "throw", "alignof", "decltype", "typeid", "static_assert", "co_return", "co_await", "co_yield"
    };

    private readonly BraceSourceLexer _lexer;
    private readonly Regex? _function;
    private readonly Regex _branch;
    private readonly bool _cDeclarators;
    private readonly StringBuilder _head = new();
    private int _headStart;
    /// <summary>The start line of a K&amp;R definition whose parameter declarations are being read.</summary>
    private int? _krStart;
    /// <summary>The brace depth of a member brace-initializer (<c>a_{1}</c>) in a constructor's head.</summary>
    private int _initializerBraces;

    private BraceBlockScanner(BraceDialect dialect, Regex? function, Regex branch)
    {
        _lexer = new BraceSourceLexer(dialect);
        _function = function;
        _branch = branch;
        _cDeclarators = dialect == BraceDialect.C;
    }

    public static BraceBlockScanner Swift() => new(BraceDialect.Swift, SwiftFunction, SwiftBranch);
    public static BraceBlockScanner Rust() => new(BraceDialect.Rust, RustFunction, RustBranch);
    public static BraceBlockScanner C() => new(BraceDialect.C, null, CBranch);
    public static BraceBlockScanner Go() => new(BraceDialect.Go, GoFunction, GoBranch);
    public static BraceBlockScanner Php() => new(BraceDialect.Php, PhpFunction, PhpBranch);

    public BlockLine Scan(string line, int index)
    {
        var code = _lexer.StripLine(line).Trim();
        if (code.Length == 0)
            return new BlockLine(false, [], false, 0, false);

        var events = new List<BlockEvent>();
        foreach (var c in code)
        {
            if (_initializerBraces > 0)
            {
                if (c == '{')
                    _initializerBraces++;
                else if (c == '}' && --_initializerBraces == 0)
                    _head.Append("()");
                continue;
            }

            if (c == '{')
            {
                int? functionStart = null;
                if (_cDeclarators)
                {
                    var head = _head.ToString();
                    if (IsMemberInitializer(head))
                    {
                        _initializerBraces = 1;
                        continue;
                    }

                    functionStart = IsFunctionDeclarator(head) ? _headStart
                        : _krStart is { } krStart && head.Trim().Length == 0 ? krStart
                        : null;
                }

                events.Add(new BlockEvent(1, functionStart));
                ResetHead();
                _krStart = null;
            }
            else if (c == '}')
            {
                events.Add(new BlockEvent(-1));
                ResetHead();
                _krStart = null;
            }
            else if (_cDeclarators)
            {
                if (c == ';')
                {
                    TrackKrDefinition(_head.ToString().Trim());
                    ResetHead();
                    continue;
                }

                if (c == ':' && _head.Length <= 32 && AccessSpecifier.IsMatch(_head.ToString().Trim()))
                {
                    ResetHead();
                    continue;
                }

                if (_head.Length == 0)
                {
                    if (char.IsWhiteSpace(c))
                        continue;
                    _headStart = index;
                }

                _head.Append(c);
            }
        }

        if (_head.Length > 0)
            _head.Append(' ');

        return new BlockLine(true, events, _function?.IsMatch(code) == true, _branch.Matches(code).Count, code.EndsWith(';'));
    }

    private void ResetHead()
    {
        _head.Clear();
    }

    /// <summary>
    /// A K&amp;R definition (<c>int add(a, b)</c>, then <c>int a;</c> … before its body) keeps its start
    /// across its parameter declarations; anything else ends it.
    /// </summary>
    private void TrackKrDefinition(string statement)
    {
        if (_krStart is not null)
        {
            if (!KrParameter.IsMatch(statement))
                _krStart = null;
        }
        else if (KrDefinition.Match(statement) is { Success: true } definition
                 && IsFunctionDeclarator(definition.Groups["declarator"].Value))
        {
            _krStart = _headStart;
        }
    }

    /// <summary>Whether a brace opens a member brace-initializer in a constructor's initializer list.</summary>
    private static bool IsMemberInitializer(string head)
    {
        var colon = InitializerList.Match(head);
        return colon.Success && EndsWithName.IsMatch(head[(colon.Index + colon.Length)..])
               && IsFunctionDeclarator(head[..(colon.Index + 1)]);
    }

    /// <summary>
    /// Whether the code since the last <c>;</c>, <c>{</c> or <c>}</c> is a C/C++ function declarator
    /// whose body the next brace opens: <c>name(…)</c>, optionally qualified (<c>const</c>,
    /// <c>noexcept</c>, <c>-&gt; T</c>, a constructor initializer list), not a control statement or a
    /// lambda.
    /// </summary>
    internal static bool IsFunctionDeclarator(string head)
    {
        var declarator = TrailingReturnType.Replace(head.Trim(), "");
        while (TrailingQualifier.Match(declarator) is { Success: true } qualifier)
            declarator = declarator[..qualifier.Index];
        if (!declarator.EndsWith(')'))
            return false;

        var paren = declarator.IndexOf('(');
        return paren > 0 && DeclaratorName.Match(declarator[..paren]) is { Success: true } name
            && !NotDeclarators.Contains(name.Groups[1].Value);
    }
}
