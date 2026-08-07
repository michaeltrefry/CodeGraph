using CodeGraph.Data;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Assistant;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Query;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Options;
using Shouldly;
using System.Runtime.InteropServices;

namespace CodeGraph.Tests.Services;

public sealed class RepoFileResolverTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"codegraph-resolver-{Guid.NewGuid():N}");

    public RepoFileResolverTests() => Directory.CreateDirectory(tempRoot);

    [Theory]
    [InlineData("")]
    [InlineData("/etc/passwd")]
    [InlineData("\\etc\\passwd")]
    [InlineData("../secret.txt")]
    [InlineData("src/../../secret.txt")]
    [InlineData("src\\..\\secret.txt")]
    [InlineData("src/..\\secret.txt")]
    [InlineData("src\\nested/file.cs")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("C:Windows\\win.ini")]
    [InlineData("\\\\server\\share\\file.txt")]
    [InlineData("//server/share/file.txt")]
    [InlineData("\\\\?\\C:\\Windows\\win.ini")]
    [InlineData("\\\\.\\PhysicalDrive0")]
    [InlineData("\\??\\C:\\Windows\\win.ini")]
    [InlineData("file.txt:secret")]
    [InlineData("NUL")]
    [InlineData("con.txt")]
    [InlineData("COM1.log")]
    [InlineData("COM¹")]
    [InlineData("com².txt")]
    [InlineData("COM³.log")]
    [InlineData("LPT¹")]
    [InlineData("lpt².txt")]
    [InlineData("LPT³.log")]
    [InlineData("folder//file.txt")]
    [InlineData("folder/./file.txt")]
    [InlineData("folder./file.txt")]
    [InlineData("folder /file.txt")]
    public void IsSafeRelativePath_RejectsPortableEscapeAndAmbiguousSyntax(string path)
    {
        RepoFileResolver.IsSafeRelativePath(path).ShouldBeFalse();
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("src/Feature/File.cs")]
    [InlineData("src\\Feature\\File.cs")]
    public void IsSafeRelativePath_AcceptsOrdinaryRepositoryPaths(string path)
    {
        RepoFileResolver.IsSafeRelativePath(path).ShouldBeTrue();
    }

    [Fact]
    public async Task ReadAllTextAsync_RequiresIndexedRepository()
    {
        var cache = Path.Combine(tempRoot, "cache");
        var repo = Path.Combine(cache, "IndexedRepo");
        Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "cache contents");
        var store = new InMemoryGraphStore();
        var options = new RepositorySourceOptions { ReposCachePath = cache };

        (await RepoFileResolver.ReadAllTextAsync("IndexedRepo", "README.md", options, store))
            .ShouldBeNull();

        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = "IndexedRepo" });

        (await RepoFileResolver.ReadAllTextAsync("IndexedRepo", "README.md", options, store))
            .ShouldBe("cache contents");
    }

    [Fact]
    public async Task ReadAllTextAsync_UsesCacheThenIndexedLocalRoot()
    {
        var cache = Path.Combine(tempRoot, "cache");
        var cachedRepo = Path.Combine(cache, "Repo");
        var localRepo = Path.Combine(tempRoot, "local");
        Directory.CreateDirectory(cachedRepo);
        Directory.CreateDirectory(localRepo);
        await File.WriteAllTextAsync(Path.Combine(cachedRepo, "same.txt"), "cache");
        await File.WriteAllTextAsync(Path.Combine(localRepo, "same.txt"), "local");
        await File.WriteAllTextAsync(Path.Combine(localRepo, "local-only.txt"), "fallback");
        var store = await CreateStoreAsync("Repo", localRepo);
        var options = new RepositorySourceOptions { ReposCachePath = cache };

        (await RepoFileResolver.ReadAllTextAsync("Repo", "same.txt", options, store)).ShouldBe("cache");
        (await RepoFileResolver.ReadAllTextAsync("Repo", "local-only.txt", options, store)).ShouldBe("fallback");
    }

    [Fact]
    public async Task ReadAllTextAsync_RejectsFileAndDirectorySymlinksOutsideRoot()
    {
        var repo = Path.Combine(tempRoot, "repo");
        var outside = Path.Combine(tempRoot, "outside");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(outside);
        var secret = Path.Combine(outside, "secret.txt");
        await File.WriteAllTextAsync(secret, "do not read");

        var createdFileLink = TryCreateFileSymlink(Path.Combine(repo, "file-link.txt"), secret);
        var createdDirectoryLink = TryCreateDirectorySymlink(Path.Combine(repo, "dir-link"), outside);
        if (!createdFileLink || !createdDirectoryLink)
        {
            RequireSymlinkSupportInCi(createdFileLink && createdDirectoryLink);
            return;
        }

        var store = await CreateStoreAsync("Repo", repo);
        var options = new RepositorySourceOptions();

        (await RepoFileResolver.ReadAllTextAsync("Repo", "file-link.txt", options, store)).ShouldBeNull();
        (await RepoFileResolver.ReadAllTextAsync("Repo", "dir-link/secret.txt", options, store)).ShouldBeNull();
    }

    [Fact]
    public async Task ReadAllTextAsync_AllowsSymlinkWhoseFinalTargetStaysInsideRoot()
    {
        var repo = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "actual"));
        await File.WriteAllTextAsync(Path.Combine(repo, "actual", "source.cs"), "safe");
        if (!TryCreateDirectorySymlink(Path.Combine(repo, "alias"), Path.Combine(repo, "actual")))
        {
            RequireSymlinkSupportInCi(supported: false);
            return;
        }

        var store = await CreateStoreAsync("Repo", repo);

        (await RepoFileResolver.ReadAllTextAsync(
            "Repo", "alias/source.cs", new RepositorySourceOptions(), store)).ShouldBe("safe");
    }

    [Fact]
    public async Task OpenRead_RejectsResolvedTargetSwapEvenWhenPathIsRestoredBeforeValidation()
    {
        var repo = Path.Combine(tempRoot, "repo");
        var outside = Path.Combine(tempRoot, "outside");
        var raceDirectory = Path.Combine(repo, "race");
        var heldDirectory = Path.Combine(repo, "held");
        Directory.CreateDirectory(raceDirectory);
        Directory.CreateDirectory(outside);
        var safe = Path.Combine(raceDirectory, "target.txt");
        var secret = Path.Combine(outside, "target.txt");
        await File.WriteAllTextAsync(safe, "safe");
        await File.WriteAllTextAsync(secret, "secret");
        var probeLink = Path.Combine(repo, "symlink-probe");
        if (!TryCreateDirectorySymlink(probeLink, outside))
        {
            RequireSymlinkSupportInCi(supported: false);
            return;
        }
        Directory.Delete(probeLink);

        using var stream = RepoFileResolver.OpenReadForTesting(
            "Repo",
            "race/target.txt",
            cachePath: null,
            localPath: repo,
            beforeOpen: () =>
            {
                Directory.Move(raceDirectory, heldDirectory);
                Directory.CreateSymbolicLink(raceDirectory, outside);
            },
            afterOpenBeforeValidation: () =>
            {
                Directory.Delete(raceDirectory);
                Directory.Move(heldDirectory, raceDirectory);
            });

        stream.ShouldBeNull();
    }

    [Fact]
    public async Task OpenRead_RejectsFifoWithoutBlocking()
    {
        if (OperatingSystem.IsWindows())
            return;

        var repo = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(repo);
        var fifo = Path.Combine(repo, "source.pipe");
        MkFifo(fifo, Convert.ToUInt32("600", 8)).ShouldBe(0);

        var openTask = Task.Run(() => RepoFileResolver.OpenRead(
            "Repo",
            "source.pipe",
            cachePath: null,
            localPath: repo));
        var completed = await Task.WhenAny(openTask, Task.Delay(TimeSpan.FromSeconds(2)));

        completed.ShouldBe(openTask, "opening a FIFO for validation must not wait for a writer");
        (await openTask).ShouldBeNull();
    }

    [Fact]
    public async Task McpAndReadmeSurfaces_UseContainedResolver()
    {
        var repo = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# Safe readme");
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "secret.txt"), "secret material");
        var store = await CreateStoreAsync("Repo", repo);
        var options = Options.Create(new RepositorySourceOptions());
        var mcp = new CodeGraphMcpServer(null!, null!, store, null!, options, null!, null!);
        var projects = new ProjectQueryService(store, options);

        (await mcp.GetCodeSnippet("Repo", "README.md")).ShouldContain("Safe readme");
        (await mcp.GetCodeSnippet("Repo", "../secret.txt")).ShouldContain("File not found");
        (await mcp.GetCodeSnippet("MissingRepo", "README.md")).ShouldContain("File not found");
        (await projects.GetReadmeAsync("Repo")).ShouldBe("# Safe readme");
        (await projects.GetReadmeAsync("MissingRepo")).ShouldBeNull();
    }

    [Fact]
    public async Task NodeSourceSurface_RejectsUnsafeStoredPath()
    {
        var repo = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(repo);
        await File.WriteAllTextAsync(Path.Combine(tempRoot, "secret.txt"), "secret material");
        var store = await CreateStoreAsync("Repo", repo);
        var nodeId = store.AddNode(new GraphNode
        {
            Project = "Repo",
            Label = NodeLabel.Class,
            Name = "Escaped",
            QualifiedName = "Escaped",
            FilePath = "../secret.txt"
        });
        var query = new NodeQueryService(store, Options.Create(new RepositorySourceOptions()));

        (await query.GetNodeSourceAsync(nodeId)).ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
            Directory.Delete(tempRoot, recursive: true);
    }

    private static async Task<InMemoryGraphStore> CreateStoreAsync(string name, string localPath)
    {
        var store = new InMemoryGraphStore();
        await store.UpsertRepositoryAsync(new RepositoryEntity { Name = name, LocalPath = localPath });
        return store;
    }

    private static bool TryCreateFileSymlink(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectorySymlink(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static void RequireSymlinkSupportInCi(bool supported)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        {
            supported.ShouldBeTrue(
                "hosted security validation must exercise symlink/reparse-point behavior");
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(string path, uint mode);
}
