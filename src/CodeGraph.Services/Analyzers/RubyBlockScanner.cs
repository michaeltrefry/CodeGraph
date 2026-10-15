using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.Services.Analyzers;

/// <summary>
/// Reduces Ruby source, line by line, to its code: comments (<c>#</c>, <c>=begin … =end</c>, the
/// data after <c>__END__</c>) are removed and every literal — strings, <c>%q{}</c>/<c>%w[]</c>-style
/// literals, regular expressions, character literals (<c>?x</c>), heredocs — leaves a single
/// <c>0</c>, so keywords and braces inside them are never code while the literal still reads as a
/// value (a following <c>if</c> stays a modifier). Interpolation (<c>#{…}</c>) is code.
/// </summary>
internal sealed class RubySourceLexer
{
    private sealed class Frame
    {
        public bool IsString { get; init; }
        /// <summary>A code frame inside <c>#{ … }</c>; its braces are counted to find the closing one.</summary>
        public bool Interpolation { get; init; }
        public bool Interpolates { get; init; }
        /// <summary>The opening delimiter of a bracket-delimited literal, which nests (<c>%q{a {b}}</c>).</summary>
        public char Opener { get; init; }
        public char Closer { get; init; }
        public int Nesting { get; set; }
        public bool Regex { get; init; }
        /// <summary>A heredoc's terminator; its body ends at a line holding only the terminator.</summary>
        public string? Heredoc { get; init; }
        /// <summary>A <c>&lt;&lt;~</c>/<c>&lt;&lt;-</c> heredoc: the terminator may be indented.</summary>
        public bool Indented { get; init; }
    }

    private static readonly Regex HeredocStart = new(@"\G<<([~-]?)([""'`]?)([A-Za-z_]\w*)\2", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PercentLiteral = new(@"\G%([qQwWiIrsx]?)([^\w\s=])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ExpressionKeywords = new(StringComparer.Ordinal)
    {
        "if", "unless", "elsif", "when", "while", "until", "and", "or", "not", "return", "then",
        "else", "do", "case", "in", "yield", "begin"
    };

    private readonly Stack<Frame> _frames = new([new Frame()]);
    private readonly List<Frame> _pendingHeredocs = [];
    private bool _blockComment;
    private bool _data;

    /// <summary>The code of the next source line.</summary>
    public string StripLine(string line)
    {
        line = line.TrimEnd('\r');
        if (_data)
            return "";

        if (_frames.Count == 1)
        {
            if (_blockComment)
            {
                if (IsMarker(line, "=end"))
                    _blockComment = false;
                return "";
            }

            if (IsMarker(line, "=begin"))
            {
                _blockComment = true;
                return "";
            }

            if (line == "__END__")
            {
                _data = true;
                return "";
            }
        }

        if (_frames.Peek().Heredoc is { } terminator)
        {
            if ((_frames.Peek().Indented ? line.Trim() : line) == terminator)
            {
                _frames.Pop();
                return "";
            }
        }

        var code = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            var top = _frames.Peek();
            var c = line[i];
            if (top.IsString)
            {
                i = ScanString(line, i, top);
                continue;
            }

            if (top.Interpolation)
            {
                if (c == '{')
                {
                    top.Nesting++;
                }
                else if (c == '}')
                {
                    if (top.Nesting == 0)
                    {
                        _frames.Pop();
                        i++;
                        continue;
                    }

                    top.Nesting--;
                }
            }

            if (TryOpenLiteral(line, i, code, out var next))
            {
                code.Append('0');
                i = next;
                continue;
            }

            if (c == '#')
                break;

            code.Append(c);
            i++;
        }

        // A heredoc's body starts on the next line; the first one started on this line is read first.
        for (var k = _pendingHeredocs.Count - 1; k >= 0; k--)
            _frames.Push(_pendingHeredocs[k]);
        _pendingHeredocs.Clear();

        return code.ToString();
    }

    private static bool IsMarker(string line, string marker)
        => line.StartsWith(marker, StringComparison.Ordinal) && (line.Length == marker.Length || char.IsWhiteSpace(line[marker.Length]));

    private static char At(string line, int index) => index >= 0 && index < line.Length ? line[index] : '\0';

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private int ScanString(string line, int i, Frame frame)
    {
        var c = line[i];
        if (c == '\\')
            return i + 2;
        if (frame.Interpolates && c == '#' && At(line, i + 1) == '{')
        {
            _frames.Push(new Frame { Interpolation = true });
            return i + 2;
        }

        if (frame.Opener != '\0' && c == frame.Opener)
        {
            frame.Nesting++;
            return i + 1;
        }

        if (c == frame.Closer && frame.Heredoc is null)
        {
            if (frame.Nesting > 0)
            {
                frame.Nesting--;
                return i + 1;
            }

            _frames.Pop();
            i++;
            while (frame.Regex && i < line.Length && char.IsAsciiLetterLower(line[i]))
                i++;
            return i;
        }

        return i + 1;
    }

    /// <summary>Opens the literal starting at <paramref name="i"/>, if one does.</summary>
    private bool TryOpenLiteral(string line, int i, StringBuilder code, out int next)
    {
        next = i;
        var c = line[i];
        var prev = LastSignificant(code, out var prevWord);
        var spaceBefore = i > 0 && char.IsWhiteSpace(line[i - 1]);
        var nextChar = At(line, i + 1);
        // Where a value starts: after an operator, an opening bracket, a keyword, or nothing.
        var valueStart = prev == '\0' || "=([{,;!~|&?:<>+-*/%^".Contains(prev)
            || (prevWord is not null && ExpressionKeywords.Contains(prevWord));
        // "foo /x/", "puts %(x)": an argument after a method name and a space.
        var argumentStart = valueStart || (prevWord is not null && spaceBefore && !char.IsWhiteSpace(nextChar) && nextChar != '=');

        switch (c)
        {
            case '"' or '`':
                _frames.Push(new Frame { IsString = true, Closer = c, Interpolates = true });
                next = i + 1;
                return true;
            case '\'':
                _frames.Push(new Frame { IsString = true, Closer = c });
                next = i + 1;
                return true;
            case '$' when nextChar != '\0' && !IsIdentifierChar(nextChar) && nextChar != '{':
                // $', $", $/, $; … are global variables, not literals or comments.
                code.Append('$');
                next = i + 2;
                return true;
            case '/' when argumentStart:
                _frames.Push(new Frame { IsString = true, Closer = '/', Interpolates = true, Regex = true });
                next = i + 1;
                return true;
            case '?' when (valueStart || spaceBefore) && nextChar != '\0' && !char.IsWhiteSpace(nextChar)
                          && (nextChar == '\\' || !IsIdentifierChar(At(line, i + 2))):
                next = nextChar == '\\' ? i + 3 : i + 2;
                return true;
            case '%':
            {
                var literal = PercentLiteral.Match(line, i);
                if (!literal.Success)
                    return false;
                var type = literal.Groups[1].Value;
                var delimiter = literal.Groups[2].Value[0];
                var isLiteral = type.Length > 0
                    ? spaceBefore || prev == '\0' || !(IsIdentifierChar(prev) || prev is ')' or ']')
                    : "([{<|!/^".Contains(delimiter) && argumentStart;
                if (!isLiteral)
                    return false;
                var closer = delimiter switch { '(' => ')', '[' => ']', '{' => '}', '<' => '>', _ => delimiter };
                _frames.Push(new Frame
                {
                    IsString = true,
                    Opener = closer != delimiter ? delimiter : '\0',
                    Closer = closer,
                    Interpolates = type is "" or "Q" or "W" or "I" or "r" or "x",
                    Regex = type == "r"
                });
                next = i + literal.Length;
                return true;
            }
            case '<' when nextChar == '<':
            {
                var heredoc = HeredocStart.Match(line, i);
                if (!heredoc.Success)
                    return false;
                var bare = heredoc.Groups[1].Length == 0 && heredoc.Groups[2].Length == 0;
                if (bare && !(Regex.IsMatch(heredoc.Groups[3].Value, "^[A-Z_][A-Z0-9_]*$") && (valueStart || spaceBefore)))
                    return false;
                _pendingHeredocs.Add(new Frame
                {
                    IsString = true,
                    Heredoc = heredoc.Groups[3].Value,
                    Indented = heredoc.Groups[1].Length > 0,
                    Interpolates = heredoc.Groups[2].Value != "'"
                });
                next = i + heredoc.Length;
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>The last non-blank character of the code so far, and the word it ends, if any.</summary>
    private static char LastSignificant(StringBuilder code, out string? word)
    {
        word = null;
        var end = code.Length;
        while (end > 0 && char.IsWhiteSpace(code[end - 1]))
            end--;
        if (end == 0)
            return '\0';
        var last = code[end - 1];
        var start = end;
        while (start > 0 && (IsIdentifierChar(code[start - 1]) || (start == end && code[start - 1] is '?' or '!')))
            start--;
        if (start < end && (char.IsLetter(code[start]) || code[start] == '_'))
            word = code.ToString(start, end - start);
        return last;
    }
}

/// <summary>
/// Ruby blocks: <c>def</c>, <c>class</c>, <c>module</c>, <c>begin</c>, <c>case</c>, <c>for</c>, a
/// <c>do</c> block, and <c>if</c>/<c>unless</c>/<c>while</c>/<c>until</c> where a statement or value
/// starts (in modifier form, <c>x if y</c>, they open nothing) — each closed by <c>end</c> — and
/// brace blocks/hashes. The optional <c>do</c> of a <c>while</c>/<c>until</c>/<c>for</c> opens
/// nothing. A word after <c>.</c>/<c>::</c>, a symbol (<c>:if</c>) or a label (<c>if:</c>) is not a
/// keyword. Functions are <c>def</c> definitions; an endless <c>def f = x</c> is a one-line function.
/// </summary>
internal sealed class RubyBlockScanner : IBlockScanner
{
    private static readonly Regex EndlessDef = new(
        @"^\s+(?:[A-Za-z_]\w*\.)?[A-Za-z_]\w*[?!]?(?:\s*\([^)]*\)\s*|\s+)=(?![=~>])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> StatementWords = new(StringComparer.Ordinal)
    {
        "then", "else", "do", "begin", "and", "or", "not", "ensure", "elsif", "when", "in"
    };

    private static readonly HashSet<string> BranchWords = new(StringComparer.Ordinal)
    {
        "if", "unless", "elsif", "while", "until", "for", "case", "when", "rescue", "and", "or"
    };

    private readonly RubySourceLexer _lexer = new();

    public BlockLine Scan(string line, int index)
    {
        var code = _lexer.StripLine(line);
        if (code.Trim().Length == 0)
            return new BlockLine(false, [], false, 0, false);

        var events = new List<BlockEvent>();
        var branches = 0;
        var loopAwaitsDo = false;
        string? previous = null;
        for (var i = 0; i < code.Length;)
        {
            var c = code[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            string token;
            var start = i;
            if (char.IsLetter(c) || c == '_' || ((c is '@' or '$') && i + 1 < code.Length))
            {
                i++;
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] is '_' or '@'))
                    i++;
                if (i < code.Length && code[i] is '?' or '!' && At(code, i + 1) != '=')
                    i++;
                token = code[start..i];
            }
            else if (char.IsDigit(c))
            {
                while (i < code.Length && (char.IsLetterOrDigit(code[i]) || code[i] == '_'))
                    i++;
                token = "0";
            }
            else if ((c == '&' && At(code, i + 1) == '&') || (c == '|' && At(code, i + 1) == '|'))
            {
                token = code.Substring(i, 2);
                i += 2;
                branches++;
            }
            else
            {
                token = c.ToString();
                i++;
                if (c == '{') events.Add(new BlockEvent(1));
                else if (c == '}') events.Add(new BlockEvent(-1));
                else if (c == '?' && start > 0 && char.IsWhiteSpace(code[start - 1]) && char.IsWhiteSpace(At(code, i)))
                    branches++;
            }

            var keyword = IsKeywordPosition(code, start, i) ? token : null;
            switch (keyword)
            {
                case "def":
                    events.Add(EndlessDef.IsMatch(code[i..])
                        ? new BlockEvent(0, InlineFunction: true)
                        : new BlockEvent(1, FunctionStart: index));
                    break;
                case "class" or "module" or "begin" or "case":
                    events.Add(new BlockEvent(1));
                    break;
                case "for":
                    events.Add(new BlockEvent(1));
                    loopAwaitsDo = true;
                    break;
                case "if" or "unless" or "while" or "until" when StartsStatement(previous):
                    events.Add(new BlockEvent(1));
                    loopAwaitsDo = keyword is "while" or "until";
                    break;
                case "do":
                    if (loopAwaitsDo)
                        loopAwaitsDo = false;
                    else
                        events.Add(new BlockEvent(1));
                    break;
                case "end":
                    events.Add(new BlockEvent(-1));
                    break;
                case "in" when previous is null:
                    branches++;
                    break;
            }

            if (keyword is not null && BranchWords.Contains(keyword))
                branches++;
            previous = token;
        }

        return new BlockLine(true, events, false, branches, false);
    }

    private static char At(string code, int index) => index >= 0 && index < code.Length ? code[index] : '\0';

    /// <summary>A word that is not a method call (<c>x.class</c>, <c>A::end</c>), a symbol, a variable or a label.</summary>
    private static bool IsKeywordPosition(string code, int start, int end)
    {
        if (!char.IsLetter(code[start]))
            return false;
        var before = At(code, start - 1);
        if (before is '.' or ':' or '@' or '$')
            return false;
        return !(At(code, end) == ':' && At(code, end + 1) != ':');
    }

    /// <summary>Whether a statement or value starts after <paramref name="previous"/> (so <c>if</c> is not a modifier).</summary>
    private static bool StartsStatement(string? previous)
        => previous is null
           || StatementWords.Contains(previous)
           || (!char.IsLetterOrDigit(previous[0]) && previous[0] is not (')' or ']' or '}' or '_' or '@' or '$'));
}
