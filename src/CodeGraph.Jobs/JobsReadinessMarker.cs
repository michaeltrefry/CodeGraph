namespace CodeGraph.Jobs;

/// <summary>
/// The jobs host has no HTTP listener, so readiness is a marker file the production docker
/// healthcheck tests for (<c>/tmp/codegraph-jobs.ready</c> in the Linux container). It is cleared
/// at process start, so a restarted container never reports a stale ready state, and written only
/// after <see cref="Startup.InitializeAsync"/> has applied MariaDB migrations. The file is empty.
/// </summary>
public static class JobsReadinessMarker
{
    public const string FileName = "codegraph-jobs.ready";

    public static string DefaultPath => System.IO.Path.Combine(System.IO.Path.GetTempPath(), FileName);

    public static void Clear(string? path = null)
    {
        path ??= DefaultPath;
        if (File.Exists(path))
            File.Delete(path);
    }

    public static void MarkReady(string? path = null) =>
        File.WriteAllText(path ?? DefaultPath, string.Empty);
}
