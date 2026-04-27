using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CodeGraph.Data;

public static class ProjectDiagnosticKey
{
    public const int MaxLength = 255;

    public static string Create(
        string? dotnetProject,
        string? diagnosticId,
        string? filePath,
        int? lineStart,
        int? lineEnd,
        string? message)
    {
        var normalizedId = NormalizePart(diagnosticId, "diagnostic", 64);
        var material = string.Join("|",
            dotnetProject ?? "",
            diagnosticId ?? "",
            filePath ?? "",
            lineStart?.ToString(CultureInfo.InvariantCulture) ?? "",
            lineEnd?.ToString(CultureInfo.InvariantCulture) ?? "",
            message ?? "");

        return $"{normalizedId}|{Hash(material)}";
    }

    public static string EnsureWithinLimit(string? diagnosticKey)
    {
        if (string.IsNullOrWhiteSpace(diagnosticKey))
            return Create(null, null, null, null, null, null);

        if (diagnosticKey.Length <= MaxLength)
            return diagnosticKey;

        var hash = Hash(diagnosticKey);
        var prefixLength = MaxLength - hash.Length - 1;
        return $"{diagnosticKey[..Math.Max(prefixLength, 0)]}|{hash}";
    }

    private static string NormalizePart(string? value, string fallback, int maxLength)
    {
        var part = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return part.Length <= maxLength ? part : part[..maxLength];
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
