namespace CodeGraph.Data;

public static class AnalysisBatchErrorDetails
{
    // MariaDB TEXT is limited to 65,535 bytes. At most 16,000 UTF-16 code units need
    // 48,000 UTF-8 bytes, including replacement characters for malformed input.
    public const int MaxLength = 16_000;
    private const string TruncationMarker = " [additional error details truncated]";

    public static string? Limit(string? error)
    {
        if (error is null || error.Length <= MaxLength)
            return error;

        var length = MaxLength - TruncationMarker.Length;
        if (char.IsHighSurrogate(error[length - 1])) length--;
        return error[..length] + TruncationMarker;
    }
}
