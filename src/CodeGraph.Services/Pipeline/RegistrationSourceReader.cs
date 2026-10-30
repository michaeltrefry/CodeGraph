using System.IO.Hashing;
using System.Text;
using CodeGraph.Services.Configuration;

namespace CodeGraph.Services.Pipeline;

/// <summary>A run-scoped, shared budget for native registration analysis. Limits use decoded
/// UTF-16 characters, like ordinary extraction; hashes always cover the original bytes.</summary>
internal sealed class RegistrationSourceReader(IFileSystem files, IndexingOptions options)
{
    internal sealed record Source(string Content, string Hash);
    private readonly Dictionary<string, Source?> _cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private long _characters;

    public async Task<Source?> ReadAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        if (_cache.TryGetValue(path, out var cached)) return cached;
        var limit = Math.Max(0L, (long)options.MaxFileSizeKbFor(Path.GetExtension(path)) * 1024);
        var budget = Math.Max(0L, (long)options.MaxRegistrationAnalysisTextKb * 1024);
        using var input = files.OpenRead(path);
        // Every supported BOM encoding needs at most four bytes per decoded character.
        // A definite oversize file is rejected without reading any of its body.
        if (input.CanSeek && input.Length > limit * 4 + 4)
        {
            _cache[path] = null;
            return null;
        }
        var hash = new XxHash3();
        using var hashed = new HashingStream(input, hash);
        using var reader = new StreamReader(hashed, Encoding.UTF8, true, 4096, leaveOpen: true);
        var content = new StringBuilder();
        var chunk = new char[4096];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = Math.Min(limit - content.Length, budget - _characters);
            var count = (int)Math.Min(chunk.Length, Math.Max(1, remaining + 1));
            var read = await reader.ReadAsync(chunk.AsMemory(0, count), ct);
            if (read == 0) break;
            _characters += read;
            if (_characters > budget)
                throw new InvalidOperationException("Native registration analysis text budget exhausted; prior graph and source hashes are preserved.");
            if (content.Length + read > limit)
            {
                _cache[path] = null;
                return null;
            }
            content.Append(chunk, 0, read);
        }
        var result = new Source(content.ToString(), Convert.ToHexString(hash.GetCurrentHash()));
        _cache[path] = result;
        return result;
    }

    private sealed class HashingStream(Stream input, XxHash3 hash) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = input.Read(buffer, offset, count);
            hash.Append(buffer.AsSpan(offset, read));
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await input.ReadAsync(buffer, ct);
            hash.Append(buffer.Span[..read]);
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
