namespace CodeGraph.Models.Work;

/// <summary>The prevalence reported for the currently executing dream write; never model policy.</summary>
public static class DreamWriteMetadata
{
    private static readonly AsyncLocal<string?> Value = new();
    public static string? Prevalence => Value.Value;
    public static IDisposable Begin(string prevalence)
    {
        var previous = Value.Value;
        Value.Value = prevalence;
        return new Restore(() => Value.Value = previous);
    }
    private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
}
