using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace CodeGraph.Host.Shared.Auth;

internal static class InternalServiceTokenCodec
{
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        var buffer = new byte[Base64.GetMaxEncodedToUtf8Length(bytes.Length)];
        Base64.EncodeToUtf8(bytes, buffer, out _, out var written);
        return Encoding.UTF8.GetString(buffer.AsSpan(0, written)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    public static byte[] Sign(string payload, string hmacKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(hmacKey));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
    }
}
