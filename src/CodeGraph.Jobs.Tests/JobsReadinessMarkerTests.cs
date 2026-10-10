using Shouldly;

namespace CodeGraph.Jobs.Tests;

public sealed class JobsReadinessMarkerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cg-jobs-ready-{Guid.NewGuid():N}");

    [Fact]
    public void MarkerIsAbsentAfterClearAndPresentAndEmptyAfterMarkReady()
    {
        File.WriteAllText(_path, "stale from a previous process");

        JobsReadinessMarker.Clear(_path);
        File.Exists(_path).ShouldBeFalse();

        JobsReadinessMarker.MarkReady(_path);
        File.Exists(_path).ShouldBeTrue();
        File.ReadAllText(_path).ShouldBeEmpty();
    }

    [Fact]
    public void ClearIsANoOpWhenNoMarkerExists() =>
        Should.NotThrow(() => JobsReadinessMarker.Clear(_path));

    [Fact]
    public void DefaultPathIsTheFileTheProductionHealthcheckTests() =>
        JobsReadinessMarker.DefaultPath.ShouldBe(Path.Combine(Path.GetTempPath(), "codegraph-jobs.ready"));

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }
}
