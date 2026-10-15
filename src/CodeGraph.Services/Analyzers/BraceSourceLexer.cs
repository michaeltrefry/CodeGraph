using System.Text;
using System.Text.RegularExpressions;

namespace CodeGraph.Services.Analyzers;

/// <summary>The literal and comment rules <see cref="BraceSourceLexer"/> applies.</summary>
internal enum BraceDialect
{
    Swift,
    Rust,
    /// <summary>C and C++: preprocessor directives, raw strings, digit separators.</summary>
    C,
    Go,
    /// <summary>PHP: only the code inside <c>&lt;?php … ?&gt;</c> tags is code; inline HTML is not.</summary>
    Php
}

/// <summary>
/// Reduces brace-language source, line by line, to its code: comments and the contents of string
/// and character literals are removed (each literal leaves a single space), so braces and
/// control-flow tokens inside them are never counted. One left-to-right scan with state carried
/// across lines. Per dialect: Swift/Rust nested block comments, multi-line and raw strings, Swift
/// string interpolation (whose contents are code), Rust char literals (a lifetime such as
/// <c>'a</c> is code); C/C++ non-nesting block comments, preprocessor directives (not code,
/// continued by a trailing backslash), raw strings <c>R"d(…)d"</c>, char literals (a digit
/// separator <c>1'000</c> is code); Go raw backtick strings and runes; PHP <c>#</c> comments (not
/// <c>#[</c> attributes), single- and double-quoted strings with <c>{$…}</c>/<c>${…}</c>
/// interpolation, heredoc/nowdoc, and <c>?&gt;</c>/<c>&lt;?php</c> mode switches.
/// </summary>
internal sealed class BraceSourceLexer(BraceDialect dialect)
{
    private sealed class Frame
    {
        public bool IsString { get; init; }
        /// <summary>The '#' count of a raw string's delimiter (Swift <c>#"…"#</c>, Rust <c>r#"…"#</c>).</summary>
        public int Hashes { get; init; }
        public bool Multiline { get; init; }
        /// <summary>A Rust raw string: no escapes.</summary>
        public bool RustRaw { get; init; }
        /// <summary>A code frame inside a string interpolation (Swift <c>\( … )</c>, PHP <c>{$ … }</c>).</summary>
        public bool Interpolation { get; init; }
        public char Opener { get; init; } = '(';
        public char Closer { get; init; } = ')';
        public int Parens { get; set; }

        // C, Go and PHP strings.
        /// <summary>The closing quote; '\0' for a heredoc, which ends at its terminator line.</summary>
        public char Quote { get; init; } = '"';
        /// <summary>No escapes (Go backtick, C++ raw string, PHP nowdoc).</summary>
        public bool Raw { get; init; }
        /// <summary>A C++ raw string's closing <c>)delim"</c>.</summary>
        public string? Terminator { get; init; }
        /// <summary>A PHP heredoc/nowdoc's closing identifier.</summary>
        public string? Heredoc { get; init; }
        /// <summary>PHP <c>"…"</c>, <c>`…`</c> and heredoc interpolate <c>{$…}</c> and <c>${…}</c>.</summary>
        public bool Interpolates { get; init; }
    }

    private static readonly Regex PhpHeredocStart = new(
        @"\G<<<[ \t]*([""']?)([A-Za-z_\u0080-\uffff][\w\u0080-\uffff]*)\1",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Stack<Frame> _frames = new([new Frame()]);
    private readonly List<Frame> _pendingHeredocs = [];
    private int _commentDepth;
    /// <summary>C: a <c>//</c> comment ending in a backslash continues onto the next line.</summary>
    private bool _lineCommentContinues;
    /// <summary>C: a preprocessor directive ending in a backslash continues onto the next line.</summary>
    private bool _directiveContinues;
    /// <summary>PHP: outside <c>&lt;?php … ?&gt;</c>, in inline HTML.</summary>
    private bool _phpHtml = dialect == BraceDialect.Php;

    private bool Swift => dialect == BraceDialect.Swift;

    /// <summary>The code of the next source line.</summary>
    public string StripLine(string line)
    {
        line = line.TrimEnd('\r');
        var code = new StringBuilder(line.Length);
        var i = 0;

        var directive = false;
        if (dialect == BraceDialect.C)
        {
            if (_lineCommentContinues)
            {
                _lineCommentContinues = line.EndsWith('\\');
                return "";
            }

            directive = _directiveContinues
                || (_commentDepth == 0 && _frames.Count == 1 && line.TrimStart().StartsWith('#'));
        }

        // A PHP heredoc/nowdoc ends at a line starting (after indentation) with its identifier.
        if (_frames.Peek().Heredoc is { } heredoc)
        {
            var body = line.TrimStart(' ', '\t');
            if (body.StartsWith(heredoc, StringComparison.Ordinal) && !IsIdentifierChar(At(body, heredoc.Length)))
            {
                _frames.Pop();
                i = line.Length - body.Length + heredoc.Length;
            }
        }

        while (i < line.Length)
        {
            if (_phpHtml)
            {
                var afterTag = FindPhpOpenTag(line, i);
                if (afterTag < 0)
                    break;
                _phpHtml = false;
                i = afterTag;
                continue;
            }

            var c = line[i];
            var next = At(line, i + 1);
            if (_commentDepth > 0)
            {
                if (c == '/' && next == '*' && dialect is BraceDialect.Swift or BraceDialect.Rust) { _commentDepth++; i += 2; }
                else if (c == '*' && next == '/') { _commentDepth--; i += 2; }
                else i++;
                continue;
            }

            var top = _frames.Peek();
            if (top.IsString)
            {
                i = dialect is BraceDialect.Swift or BraceDialect.Rust ? ScanString(line, i, top) : ScanCString(line, i, top);
                continue;
            }

            if (dialect == BraceDialect.Php && c == '?' && next == '>')
            {
                _phpHtml = true;
                i += 2;
                continue;
            }

            if ((c == '/' && next == '/') || (dialect == BraceDialect.Php && c == '#' && next != '['))
            {
                // A PHP line comment ends at the line's end or at ?>.
                var close = dialect == BraceDialect.Php ? line.IndexOf("?>", i, StringComparison.Ordinal) : -1;
                if (close >= 0)
                {
                    _phpHtml = true;
                    i = close + 2;
                    continue;
                }

                if (dialect == BraceDialect.C)
                    _lineCommentContinues = line.EndsWith('\\');
                break;
            }

            if (c == '/' && next == '*')
            {
                _commentDepth = 1;
                i += 2;
                continue;
            }

            if (top.Interpolation)
            {
                if (c == top.Opener)
                {
                    top.Parens++;
                }
                else if (c == top.Closer)
                {
                    if (top.Parens == 0)
                    {
                        _frames.Pop();
                        i++;
                        continue;
                    }

                    top.Parens--;
                }
            }

            if (dialect == BraceDialect.Php && c == '<' && PhpHeredocStart.Match(line, i) is { Success: true } heredocStart)
            {
                var nowdoc = heredocStart.Groups[1].Value == "'";
                _pendingHeredocs.Add(new Frame
                {
                    IsString = true, Quote = '\0', Heredoc = heredocStart.Groups[2].Value,
                    Raw = nowdoc, Interpolates = !nowdoc, Multiline = true
                });
                code.Append(' ');
                i += heredocStart.Length;
                continue;
            }

            if (TryOpenString(line, i, out var afterOpen, out var frame))
            {
                _frames.Push(frame);
                code.Append(' ');
                i = afterOpen;
                continue;
            }

            if (c == '\'' && dialect switch
                {
                    BraceDialect.Rust => TrySkipRustCharLiteral(line, i, out var afterChar) && Skip(afterChar, out i),
                    BraceDialect.C or BraceDialect.Go => TrySkipCharLiteral(line, i, out var afterChar) && Skip(afterChar, out i),
                    _ => false
                })
            {
                code.Append(' ');
                continue;
            }

            code.Append(c);
            i++;
        }

        // A Swift single-line string (and any interpolation in it) cannot continue on the next line.
        if (Swift)
        {
            while (_frames.Count > 1 && !(_frames.Peek().IsString && _frames.Peek().Multiline))
                _frames.Pop();
        }

        // A C or Go string ends with its line, unless (C) the newline is escaped.
        if (dialect is BraceDialect.C or BraceDialect.Go && !(dialect == BraceDialect.C && line.EndsWith('\\')))
        {
            while (_frames.Peek() is { IsString: true, Multiline: false })
                _frames.Pop();
        }

        // A heredoc's body starts on the next line; the first one started on this line is read first.
        for (var k = _pendingHeredocs.Count - 1; k >= 0; k--)
            _frames.Push(_pendingHeredocs[k]);
        _pendingHeredocs.Clear();

        if (directive)
        {
            _directiveContinues = line.EndsWith('\\');
            return "";
        }

        return code.ToString();
    }

    private static bool Skip(int to, out int i)
    {
        i = to;
        return true;
    }

    private static char At(string line, int index) => index >= 0 && index < line.Length ? line[index] : '\0';

    private static bool HashesAt(string line, int index, int count)
    {
        for (var k = 0; k < count; k++)
        {
            if (At(line, index + k) != '#')
                return false;
        }

        return true;
    }

    /// <summary>The index after the next PHP open tag (<c>&lt;?php</c>, <c>&lt;?=</c>, <c>&lt;?</c>), or -1.</summary>
    private static int FindPhpOpenTag(string line, int start)
    {
        for (var tag = line.IndexOf("<?", start, StringComparison.Ordinal); tag >= 0;
             tag = line.IndexOf("<?", tag + 2, StringComparison.Ordinal))
        {
            if (string.Compare(line, tag + 2, "php", 0, 3, StringComparison.OrdinalIgnoreCase) == 0
                && !IsIdentifierChar(At(line, tag + 5)))
                return tag + 5;
            if (At(line, tag + 2) == '=')
                return tag + 3;
            if (At(line, tag + 2) is '\0' or ' ' or '\t')
                return tag + 2;
        }

        return -1;
    }

    private int ScanString(string line, int i, Frame frame)
    {
        var c = line[i];
        if (!Swift)
        {
            if (frame.RustRaw)
            {
                if (c == '"' && HashesAt(line, i + 1, frame.Hashes))
                {
                    _frames.Pop();
                    return i + 1 + frame.Hashes;
                }

                return i + 1;
            }

            if (c == '\\')
                return i + 2;
            if (c == '"')
            {
                _frames.Pop();
                return i + 1;
            }

            return i + 1;
        }

        var quotes = frame.Multiline ? 3 : 1;
        if (c == '"' && (quotes == 1 || (At(line, i + 1) == '"' && At(line, i + 2) == '"'))
            && HashesAt(line, i + quotes, frame.Hashes))
        {
            _frames.Pop();
            return i + quotes + frame.Hashes;
        }

        if (c == '\\' && HashesAt(line, i + 1, frame.Hashes))
        {
            var afterEscape = i + 1 + frame.Hashes;
            if (At(line, afterEscape) == '(')
            {
                _frames.Push(new Frame { Interpolation = true });
                return afterEscape + 1;
            }

            return afterEscape + 1;
        }

        return i + 1;
    }

    /// <summary>A C, Go or PHP string literal, heredoc or nowdoc.</summary>
    private int ScanCString(string line, int i, Frame frame)
    {
        if (frame.Terminator is { } terminator)
        {
            if (string.CompareOrdinal(line, i, terminator, 0, terminator.Length) == 0)
            {
                _frames.Pop();
                return i + terminator.Length;
            }

            return i + 1;
        }

        var c = line[i];
        if (c == '\\' && !frame.Raw)
            return i + 2;
        if (c == frame.Quote)
        {
            _frames.Pop();
            return i + 1;
        }

        if (frame.Interpolates && ((c == '{' && At(line, i + 1) == '$') || (c == '$' && At(line, i + 1) == '{')))
        {
            // {$expr} and ${expr}: the expression is code; the delimiting braces are not.
            _frames.Push(new Frame { Interpolation = true, Opener = '{', Closer = '}' });
            return c == '{' ? i + 1 : i + 2;
        }

        return i + 1;
    }

    private bool TryOpenString(string line, int i, out int next, out Frame frame)
    {
        next = i;
        frame = null!;
        var c = line[i];
        switch (dialect)
        {
            case BraceDialect.Swift:
            {
                var hashes = 0;
                while (At(line, i + hashes) == '#')
                    hashes++;
                if (At(line, i + hashes) != '"')
                    return false;
                var start = i + hashes;
                var multiline = At(line, start + 1) == '"' && At(line, start + 2) == '"';
                frame = new Frame { IsString = true, Hashes = hashes, Multiline = multiline };
                next = start + (multiline ? 3 : 1);
                return true;
            }
            case BraceDialect.C:
            {
                if (c != '"')
                    return false;
                // R"delim( … )delim", also with an encoding prefix (u8R, LR, uR, UR).
                if (PrecedingIdentifier(line, i) is "R" or "u8R" or "LR" or "uR" or "UR")
                {
                    var open = line.IndexOf('(', i + 1);
                    if (open > i && open - i - 1 <= 16)
                    {
                        frame = new Frame { IsString = true, Raw = true, Multiline = true, Terminator = ")" + line[(i + 1)..open] + "\"" };
                        next = open + 1;
                        return true;
                    }
                }

                frame = new Frame { IsString = true };
                next = i + 1;
                return true;
            }
            case BraceDialect.Go:
                if (c == '"')
                    frame = new Frame { IsString = true };
                else if (c == '`')
                    frame = new Frame { IsString = true, Quote = '`', Raw = true, Multiline = true };
                else
                    return false;
                next = i + 1;
                return true;
            case BraceDialect.Php:
                if (c is not ('"' or '\'' or '`'))
                    return false;
                frame = new Frame { IsString = true, Quote = c, Multiline = true, Interpolates = c != '\'' };
                next = i + 1;
                return true;
        }

        if (c == '"')
        {
            frame = new Frame { IsString = true };
            next = i + 1;
            return true;
        }

        // Prefixed literals: b"…", c"…", r#"…"#, br#"…"#, cr"…" — only at the start of a token.
        if (c is not ('b' or 'c' or 'r') || (i > 0 && IsIdentifierChar(line[i - 1])))
            return false;
        var j = i;
        if (line[j] is 'b' or 'c')
            j++;
        var raw = At(line, j) == 'r';
        if (raw)
            j++;
        var rawHashes = 0;
        while (raw && At(line, j + rawHashes) == '#')
            rawHashes++;
        if (At(line, j + rawHashes) != '"')
            return false;
        frame = new Frame { IsString = true, RustRaw = raw, Hashes = rawHashes, Multiline = true };
        next = j + rawHashes + 1;
        return true;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The identifier (or number) immediately before <paramref name="i"/>.</summary>
    private static string PrecedingIdentifier(string line, int i)
    {
        var start = i;
        while (start > 0 && IsIdentifierChar(line[start - 1]))
            start--;
        return line[start..i];
    }

    /// <summary>A Rust char literal (<c>'x'</c>, <c>'\n'</c>, <c>'\u{1F600}'</c>); a lifetime or label is not one.</summary>
    private static bool TrySkipRustCharLiteral(string line, int i, out int next)
    {
        next = i;
        if (At(line, i + 1) == '\\')
        {
            var close = line.IndexOf('\'', i + 3);
            if (close < 0)
                return false;
            next = close + 1;
            return true;
        }

        if (At(line, i + 1) is not ('\'' or '\0') && At(line, i + 2) == '\'')
        {
            next = i + 3;
            return true;
        }

        if (char.IsHighSurrogate(At(line, i + 1)) && At(line, i + 3) == '\'')
        {
            next = i + 4;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A C/C++ char literal (<c>'x'</c>, <c>'\''</c>, <c>L'x'</c>, <c>u8'x'</c>) or Go rune, closed on its
    /// line. After a number or identifier other than an encoding prefix, a quote is a C++14 digit
    /// separator (<c>1'000</c>), which is code.
    /// </summary>
    private static bool TrySkipCharLiteral(string line, int i, out int next)
    {
        next = i;
        if (i > 0 && IsIdentifierChar(line[i - 1]) && PrecedingIdentifier(line, i) is not ("L" or "u" or "U" or "u8"))
            return false;

        for (var j = i + 1; j < line.Length; j++)
        {
            if (line[j] == '\\')
            {
                j++;
                continue;
            }

            if (line[j] == '\'')
            {
                next = j + 1;
                return true;
            }
        }

        return false;
    }
}
