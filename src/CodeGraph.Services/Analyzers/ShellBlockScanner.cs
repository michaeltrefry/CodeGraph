using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.Services.Analyzers;

/// <summary>
/// Reduces shell (Bash/zsh) source, line by line, to its code: <c>#</c> comments (only at the start
/// of a word, so <c>$#</c>, <c>${#x}</c> and <c>x#y</c> are not comments) and heredoc bodies are
/// removed; quoted strings, <c>${…}</c> parameter expansions, <c>$((…))</c> arithmetic and
/// backslash-escaped characters each leave a single <c>0</c> (a word). A <c>$( … )</c> command
/// substitution is code, even inside double quotes, and is set off as <c> ( … ) </c>.
/// </summary>
internal sealed class ShellSourceLexer
{
    private enum Kind { Code, Single, AnsiC, Double, Substitution, Parameter, Arithmetic, Heredoc }

    private sealed class Frame
    {
        public Kind Kind { get; init; }
        public int Nesting { get; set; }
        public string? Terminator { get; init; }
        /// <summary><c>&lt;&lt;-</c>: leading tabs are stripped from the terminator line.</summary>
        public bool StripTabs { get; init; }
    }

    private static readonly Regex HeredocStart = new(
        @"\G<<(-?)[ \t]*(?:'([^']*)'|""([^""]*)""|\\?([^\s;&|()<>'""]+))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Stack<Frame> _frames = new([new Frame { Kind = Kind.Code }]);
    private readonly List<Frame> _pendingHeredocs = [];

    /// <summary>The last line ended with an escaped newline outside quotes: the command continues.</summary>
    public bool Continues { get; private set; }

    /// <summary>The code of the next source line.</summary>
    public string StripLine(string line)
    {
        line = line.TrimEnd('\r');
        Continues = false;
        if (_frames.Peek() is { Kind: Kind.Heredoc } heredoc)
        {
            if ((heredoc.StripTabs ? line.TrimStart('\t') : line) == heredoc.Terminator)
                _frames.Pop();
            return "";
        }

        var code = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            var next = At(line, i + 1);
            var top = _frames.Peek();
            switch (top.Kind)
            {
                case Kind.Single:
                    if (c == '\'') _frames.Pop();
                    i++;
                    continue;
                case Kind.AnsiC:
                    if (c == '\\') { i += 2; continue; }
                    if (c == '\'') _frames.Pop();
                    i++;
                    continue;
                case Kind.Double:
                    if (c == '\\') { i += 2; continue; }
                    if (c == '"') { _frames.Pop(); i++; continue; }
                    if (TryOpenExpansion(line, i, code, out var afterExpansion)) { i = afterExpansion; continue; }
                    i++;
                    continue;
                case Kind.Parameter:
                    if (c == '\\') { i += 2; continue; }
                    if (c == '{') top.Nesting++;
                    else if (c == '}' && top.Nesting-- == 0) _frames.Pop();
                    else if (c == '\'') _frames.Push(new Frame { Kind = Kind.Single });
                    else if (c == '"') _frames.Push(new Frame { Kind = Kind.Double });
                    i++;
                    continue;
                case Kind.Arithmetic:
                    if (c == '(') top.Nesting++;
                    else if (c == ')' && --top.Nesting == 0)
                    {
                        _frames.Pop();
                        i += 2;
                        continue;
                    }

                    i++;
                    continue;
            }

            // Code, or a command substitution's code.
            if (top.Kind == Kind.Substitution)
            {
                if (c == '(')
                {
                    top.Nesting++;
                }
                else if (c == ')' && top.Nesting-- == 0)
                {
                    _frames.Pop();
                    code.Append(" ) ");
                    i++;
                    continue;
                }
            }

            if (c == '\\')
            {
                if (i == line.Length - 1)
                {
                    Continues = true;
                    break;
                }

                code.Append('0');
                i += 2;
                continue;
            }

            if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]) || ";&|()<>".Contains(line[i - 1])))
                break;

            if (c == '\'')
            {
                _frames.Push(new Frame { Kind = Kind.Single });
                code.Append('0');
                i++;
                continue;
            }

            if (c == '"')
            {
                _frames.Push(new Frame { Kind = Kind.Double });
                code.Append('0');
                i++;
                continue;
            }

            if (c == '$' && next == '\'')
            {
                _frames.Push(new Frame { Kind = Kind.AnsiC });
                code.Append('0');
                i += 2;
                continue;
            }

            if (TryOpenExpansion(line, i, code, out var after))
            {
                i = after;
                continue;
            }

            if (c == '(' && next == '(')
            {
                // (( arithmetic )) command
                _frames.Push(new Frame { Kind = Kind.Arithmetic, Nesting = 1 });
                code.Append('0');
                i += 2;
                continue;
            }

            if (c == '<' && next == '<')
            {
                if (At(line, i + 2) == '<')
                {
                    code.Append("<<<");
                    i += 3;
                    continue;
                }

                var start = HeredocStart.Match(line, i);
                if (start.Success)
                {
                    var terminator = start.Groups[2].Success ? start.Groups[2].Value
                        : start.Groups[3].Success ? start.Groups[3].Value
                        : start.Groups[4].Value;
                    _pendingHeredocs.Add(new Frame { Kind = Kind.Heredoc, Terminator = terminator, StripTabs = start.Groups[1].Length > 0 });
                    code.Append(' ');
                    i += start.Length;
                    continue;
                }
            }

            code.Append(c);
            i++;
        }

        // A heredoc's body starts on the next line; the first one started on this line is read first.
        for (var k = _pendingHeredocs.Count - 1; k >= 0; k--)
            _frames.Push(_pendingHeredocs[k]);
        _pendingHeredocs.Clear();

        return code.ToString();
    }

    private static char At(string line, int index) => index >= 0 && index < line.Length ? line[index] : '\0';

    /// <summary><c>$(…)</c> (code), <c>$((…))</c> and <c>${…}</c> (values).</summary>
    private bool TryOpenExpansion(string line, int i, StringBuilder code, out int next)
    {
        next = i;
        if (line[i] != '$')
            return false;
        var open = At(line, i + 1);
        if (open == '(' && At(line, i + 2) == '(')
        {
            _frames.Push(new Frame { Kind = Kind.Arithmetic, Nesting = 1 });
            code.Append('0');
            next = i + 3;
            return true;
        }

        if (open == '(')
        {
            _frames.Push(new Frame { Kind = Kind.Substitution });
            code.Append(" ( ");
            next = i + 2;
            return true;
        }

        if (open == '{')
        {
            _frames.Push(new Frame { Kind = Kind.Parameter });
            code.Append('0');
            next = i + 2;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Shell blocks: <c>if … fi</c>, <c>case … esac</c>, <c>for</c>/<c>select</c>/<c>while</c>/<c>until</c>
/// <c>… done</c> and <c>{ … }</c> groups (function bodies). Reserved words count only in command
/// position (<c>echo if</c> is not a block), and <c>{</c>/<c>}</c> only as whole words (brace
/// expansion <c>{a,b}</c> is not a block). Functions are <c>name()</c> and <c>function name</c>
/// definitions. Branches: <c>if</c>, <c>elif</c>, loops, <c>case</c> and each case arm,
/// <c>&amp;&amp;</c> and <c>||</c>.
/// </summary>
internal sealed class ShellBlockScanner : IBlockScanner
{
    private readonly ShellSourceLexer _lexer = new();
    /// <summary>Per open <c>case</c>: whether a pattern (rather than a command) comes next.</summary>
    private readonly Stack<bool> _cases = new();
    private bool _commandPosition = true;
    private bool _caseAwaitsIn;
    private bool _functionAwaitsName;

    public BlockLine Scan(string line, int index)
    {
        var continued = _lexer.Continues;
        var code = _lexer.StripLine(line);
        if (!continued)
            _commandPosition = true;
        var tokens = Tokenize(code);
        if (tokens.Count == 0)
            return new BlockLine(false, [], false, 0, false);

        var events = new List<BlockEvent>();
        var branches = 0;
        var declares = false;
        for (var k = 0; k < tokens.Count; k++)
        {
            var token = tokens[k];
            var expectsPattern = _cases.Count > 0 && _cases.Peek();
            if (IsOperator(token))
            {
                switch (token)
                {
                    case ";;" or ";&" or ";;&":
                        if (_cases.Count > 0)
                        {
                            _cases.Pop();
                            _cases.Push(true);
                        }

                        _commandPosition = true;
                        break;
                    case ")" when expectsPattern:
                        // the end of a case arm's pattern
                        branches++;
                        _cases.Pop();
                        _cases.Push(false);
                        _commandPosition = true;
                        break;
                    case "(" or "|" when expectsPattern:
                        break;
                    case "<" or ">" or ">>" or "<&" or ">&" or "&>" or "<>" or ">|":
                        break;
                    default:
                        if (token is "&&" or "||")
                            branches++;
                        _commandPosition = true;
                        break;
                }

                continue;
            }

            if (expectsPattern)
            {
                if (token == "esac")
                {
                    _cases.Pop();
                    events.Add(new BlockEvent(-1));
                    _commandPosition = false;
                }

                continue;
            }

            if (_caseAwaitsIn)
            {
                if (token == "in")
                {
                    _caseAwaitsIn = false;
                    _cases.Push(true);
                }

                continue;
            }

            if (_functionAwaitsName)
            {
                _functionAwaitsName = false;
                _commandPosition = true;
                continue;
            }

            if (!_commandPosition)
                continue;

            switch (token)
            {
                case "if":
                    events.Add(new BlockEvent(1));
                    branches++;
                    break;
                case "elif":
                    branches++;
                    break;
                case "then" or "else" or "do" or "!" or "time":
                    break;
                case "while" or "until":
                    events.Add(new BlockEvent(1));
                    branches++;
                    break;
                case "for" or "select":
                    events.Add(new BlockEvent(1));
                    branches++;
                    _commandPosition = false;
                    break;
                case "case":
                    events.Add(new BlockEvent(1));
                    branches++;
                    _caseAwaitsIn = true;
                    _commandPosition = false;
                    break;
                case "fi" or "done" or "}":
                    events.Add(new BlockEvent(-1));
                    _commandPosition = false;
                    break;
                case "esac":
                    if (_cases.Count > 0)
                        _cases.Pop();
                    events.Add(new BlockEvent(-1));
                    _commandPosition = false;
                    break;
                case "{":
                    events.Add(new BlockEvent(1));
                    break;
                case "function":
                    declares = true;
                    _functionAwaitsName = true;
                    break;
                default:
                    if (k + 2 < tokens.Count && tokens[k + 1] == "(" && tokens[k + 2] == ")")
                    {
                        // name() — a function definition; its body follows.
                        declares = true;
                        k += 2;
                    }
                    else
                    {
                        _commandPosition = false;
                    }

                    break;
            }
        }

        return new BlockLine(true, events, declares, branches, false);
    }

    private static bool IsOperator(string token) => ";&|()<>".Contains(token[0]);

    /// <summary>Words and the operators <c>; ;; ;&amp; ;;&amp; &amp; &amp;&amp; | || |&amp; ( ) &lt; &gt;</c>…</summary>
    private static List<string> Tokenize(string code)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < code.Length)
        {
            var c = code[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (";&|()<>".Contains(c))
            {
                var length = 1;
                var two = i + 1 < code.Length ? code.Substring(i, 2) : "";
                if (i + 2 < code.Length && code.Substring(i, 3) == ";;&")
                    length = 3;
                else if (two is ";;" or ";&" or "&&" or "||" or "|&" or ">>" or "<&" or ">&" or "&>" or "<>" or ">|")
                    length = 2;
                tokens.Add(code.Substring(i, length));
                i += length;
                continue;
            }

            var start = i;
            while (i < code.Length && !char.IsWhiteSpace(code[i]) && !";&|()<>".Contains(code[i]))
                i++;
            tokens.Add(code[start..i]);
        }

        return tokens;
    }
}
