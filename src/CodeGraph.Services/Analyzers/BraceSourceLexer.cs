using System.Text;

namespace CodeGraph.Services.Analyzers;

/// <summary>
/// Reduces Swift or Rust source, line by line, to its code: comments and the contents of string
/// and character literals are removed (each literal leaves a single space), so braces and
/// control-flow tokens inside them are never counted. One left-to-right scan with state carried
/// across lines: nested block comments, multi-line and raw strings, Swift string interpolation
/// (whose contents are code), Rust char literals (a lifetime such as <c>'a</c> is code).
/// </summary>
internal sealed class BraceSourceLexer(bool swift)
{
    private sealed class Frame
    {
        public bool IsString { get; init; }
        /// <summary>The '#' count of a raw string's delimiter (Swift <c>#"…"#</c>, Rust <c>r#"…"#</c>).</summary>
        public int Hashes { get; init; }
        public bool Multiline { get; init; }
        /// <summary>A Rust raw string: no escapes.</summary>
        public bool RustRaw { get; init; }
        /// <summary>A code frame inside a Swift string interpolation <c>\( … )</c>.</summary>
        public bool Interpolation { get; init; }
        public int Parens { get; set; }
    }

    private readonly Stack<Frame> _frames = new([new Frame()]);
    private int _commentDepth;

    /// <summary>The code of the next source line.</summary>
    public string StripLine(string line)
    {
        var code = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            var next = At(line, i + 1);
            if (_commentDepth > 0)
            {
                if (c == '/' && next == '*') { _commentDepth++; i += 2; }
                else if (c == '*' && next == '/') { _commentDepth--; i += 2; }
                else i++;
                continue;
            }

            var top = _frames.Peek();
            if (top.IsString)
            {
                i = ScanString(line, i, top);
                continue;
            }

            if (c == '/' && next == '/')
                break;
            if (c == '/' && next == '*')
            {
                _commentDepth = 1;
                i += 2;
                continue;
            }

            if (top.Interpolation)
            {
                if (c == '(')
                {
                    top.Parens++;
                }
                else if (c == ')')
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

            if (TryOpenString(line, i, out var afterOpen, out var frame))
            {
                _frames.Push(frame);
                code.Append(' ');
                i = afterOpen;
                continue;
            }

            if (!swift && c == '\'' && TrySkipCharLiteral(line, i, out var afterChar))
            {
                code.Append(' ');
                i = afterChar;
                continue;
            }

            code.Append(c);
            i++;
        }

        // A Swift single-line string (and any interpolation in it) cannot continue on the next line.
        if (swift)
        {
            while (_frames.Count > 1 && !(_frames.Peek().IsString && _frames.Peek().Multiline))
                _frames.Pop();
        }

        return code.ToString();
    }

    private static char At(string line, int index) => index < line.Length ? line[index] : '\0';

    private static bool HashesAt(string line, int index, int count)
    {
        for (var k = 0; k < count; k++)
        {
            if (At(line, index + k) != '#')
                return false;
        }

        return true;
    }

    private int ScanString(string line, int i, Frame frame)
    {
        var c = line[i];
        if (!swift)
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

    private bool TryOpenString(string line, int i, out int next, out Frame frame)
    {
        next = i;
        frame = null!;
        var c = line[i];
        if (swift)
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

    /// <summary>A Rust char literal (<c>'x'</c>, <c>'\n'</c>, <c>'\u{1F600}'</c>); a lifetime or label is not one.</summary>
    private static bool TrySkipCharLiteral(string line, int i, out int next)
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
}
