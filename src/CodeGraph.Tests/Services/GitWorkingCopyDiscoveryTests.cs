using System.Diagnostics;
using CodeGraph.Extractors.TreeSitter;
using CodeGraph.Models;
using CodeGraph.Services;
using CodeGraph.Services.Configuration;
using CodeGraph.Services.Extractors;
using CodeGraph.Services.Pipeline;
using CodeGraph.Tests.Extractors;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace CodeGraph.Tests.Services;

/// <summary>
/// sc-26199 (LensWorks): a folder source over a git working copy indexed a gitignored
/// vendor/PhotoCatalog and an initialized submodule vendor/Compositor. Inside a git work tree,
/// discovery yields what git lists: tracked files plus untracked files git does not ignore.
/// </summary>
public class GitWorkingCopyDiscoveryTests : IDisposable
{
    private const string Project = "Lens";

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), $"codegraph-git-discovery-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_scratch))
            return;
        // git marks object files read-only, which Directory.Delete refuses on Windows.
        foreach (var file in Directory.EnumerateFiles(_scratch, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_scratch, recursive: true);
    }

    private string Repo => Path.Combine(_scratch, "lens");

    [Fact]
    public void DiscoverPaths_GitWorkingCopy_YieldsTrackedAndUntrackedUnignoredFilesOnly()
    {
        CreateWorkingCopy();

        Discover(Repo).ShouldBe([
            ".gitignore",
            ".gitmodules",
            "generated/kept.py",
            "src/app.py",
            "src/nested/.gitignore",
            "src/nested/keep.py",
            "untracked.py"
        ]);
    }

    [Fact]
    public void DiscoverPaths_RootIsASubdirectoryOfTheWorkTree_AppliesTheEnclosingRepositoryRules()
    {
        CreateWorkingCopy();

        Discover(Path.Combine(Repo, "src")).ShouldBe(["app.py", "nested/.gitignore", "nested/keep.py"]);
    }

    [Fact]
    public void DiscoverPaths_RootIsASymlinkToTheWorkTree_YieldsTheSameFiles()
    {
        CreateWorkingCopy();
        var link = Path.Combine(_scratch, "lens-link");
        try
        {
            Directory.CreateSymbolicLink(link, Repo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Windows without symlink privilege.
        }

        Discover(link).ShouldBe(Discover(Repo));
    }

    /// <summary>A folder git ignores as a whole (or one that is no git repository) is walked as before.</summary>
    [Fact]
    public void DiscoverPaths_RootInsideAnIgnoredDirectory_WalksTheDisk()
    {
        CreateWorkingCopy();

        Discover(Path.Combine(Repo, "vendor", "PhotoCatalog")).ShouldBe(["Cargo.toml", "catalog.py"]);
    }

    [Fact]
    public void DiscoverPaths_NotAGitRepository_WalksEveryFileAsBefore()
    {
        var folder = Path.Combine(_scratch, "plain");
        Write(folder, ".gitignore", "vendor/\n");
        Write(folder, "src/app.py", "def app(): pass\n");
        Write(folder, "vendor/lib.py", "def lib(): pass\n");

        Discover(folder).ShouldBe([".gitignore", "src/app.py", "vendor/lib.py"]);
    }

    [Fact]
    public void RepositoryTree_GitWorkingCopy_HidesIgnoredFilesAndSubmoduleContents()
    {
        CreateWorkingCopy();
        var tree = RepositoryTree.FromFileSystem(
            Repo, new LocalFileSystem(), GitWorkingTreeFiles.TryList(Repo).ShouldNotBeNull());

        tree.FileExists("src/app.py").ShouldBeTrue();
        tree.FileExists("generated/kept.py").ShouldBeTrue();
        tree.FileExists("vendor/PhotoCatalog/catalog.py").ShouldBeFalse();
        tree.FileExists("vendor/Compositor/compositor.py").ShouldBeFalse();
        tree.FileExists("src/deleted.py").ShouldBeFalse();
        tree.ListDirectory("").ShouldNotContain("ignored.py");
        tree.ListDirectory("vendor").ShouldBeEmpty();
        tree.ListDirectory("vendor/Compositor").ShouldBeEmpty();
    }

    /// <summary>
    /// Every discovery user sees the same universe: per-file discovery, Cargo and Swift manifest
    /// discovery, and the stored graph (no node under an ignored path or a submodule).
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_GitWorkingCopy_IndexesNothingUnderIgnoredPathsOrSubmodules()
    {
        CreateWorkingCopy();
        AddManifests();
        var store = new InMemoryGraphStore();

        await CreatePipeline(store).IndexProjectAsync(Project, Repo, ct: CancellationToken.None);

        var indexed = (await store.GetFileHashesAsync(Project)).Keys.ToList();
        indexed.ShouldContain("src/app.py");
        indexed.ShouldContain("generated/kept.py");
        indexed.ShouldContain("untracked.py");
        indexed.ShouldContain("Cargo.toml");
        indexed.ShouldContain("Package.swift");
        indexed.ShouldAllBe(path => !path.StartsWith("vendor/") && path != "ignored.py"
                                    && path != "excluded.py" && path != "src/nested/local.py"
                                    && path != "generated/other.py" && path != "scratch/notes.py");
        store.Nodes.ShouldNotContain(node => node.FilePath != null && node.FilePath.StartsWith("vendor/"));
        store.Nodes.ShouldContain(node => node.Label == NodeLabel.Package && node.Name == "lens");
        store.Nodes.ShouldNotContain(node => node.Label == NodeLabel.Package && node.Name == "photo-catalog");
        store.Nodes.ShouldNotContain(node => node.Name == "Compositor");
    }

    /// <summary>
    /// An index written by the disk walk holds the ignored vendor copy and the submodule's files
    /// (here: indexed before the folder was a git repository); the next full scan removes them.
    /// </summary>
    [Fact]
    public async Task IndexProjectAsync_PreviouslyIndexedIgnoredFiles_AreRemovedOnTheNextRun()
    {
        Write(Repo, ".gitignore", "vendor/PhotoCatalog/\nignored.py\n");
        Write(Repo, "src/app.py", "def app(): pass\n");
        Write(Repo, "vendor/PhotoCatalog/catalog.py", "def catalog(): pass\n");
        Write(Repo, "vendor/Compositor/compositor.py", "def compose(): pass\n");
        Write(Repo, "ignored.py", "def ignored(): pass\n");
        var store = new InMemoryGraphStore();
        await CreatePipeline(store).IndexProjectAsync(Project, Repo, ct: CancellationToken.None);
        (await store.GetFileHashesAsync(Project)).Keys.ShouldContain("vendor/PhotoCatalog/catalog.py");
        (await store.GetFileHashesAsync(Project)).Keys.ShouldContain("vendor/Compositor/compositor.py");
        (await store.GetFileHashesAsync(Project)).Keys.ShouldContain("ignored.py");

        var compositor = Path.Combine(Repo, "vendor", "Compositor");
        Git(compositor, "init", "-q");
        Git(compositor, "add", ".");
        Commit(compositor);
        Git(Repo, "init", "-q");
        Git(Repo, "add", ".");
        Commit(Repo);

        await CreatePipeline(store).IndexProjectAsync(Project, Repo, ct: CancellationToken.None);

        (await store.GetFileHashesAsync(Project)).Keys.ShouldBe(["src/app.py"]);
        store.Nodes.ShouldNotContain(node => node.FilePath != null
                                             && (node.FilePath.StartsWith("vendor/") || node.FilePath == "ignored.py"));
    }

    [Fact]
    public void ExtractionFingerprint_IncludesTheGitWorkingCopyRule()
    {
        var pipeline = CreatePipeline(new InMemoryGraphStore());

        pipeline.DiscoveryExcludePatterns.ShouldContain(IndexingPipeline.GitWorkingCopyRule);
    }

    private static IReadOnlyList<string> Discover(string root) =>
        IndexingPipeline.DiscoverPaths(root, new LocalFileSystem(), new IndexingOptions().SkipPatterns, "**/*")
            .Select(path => Path.GetRelativePath(Path.GetFullPath(root), path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A working copy with every git rule in play: a root and a nested .gitignore,
    /// .git/info/exclude, a tracked file an ignore pattern matches, untracked files (one ignored,
    /// one not), a file deleted from the working tree, a populated submodule (gitlink) and an
    /// untracked nested repository.
    /// </summary>
    private void CreateWorkingCopy()
    {
        Write(Repo, ".gitignore", "vendor/PhotoCatalog/\nignored.py\ngenerated/\n");
        Write(Repo, "src/app.py", "def app(): pass\n");
        Write(Repo, "src/deleted.py", "def deleted(): pass\n");
        Write(Repo, "src/nested/.gitignore", "local.py\n");
        Write(Repo, "src/nested/local.py", "def local(): pass\n");
        Write(Repo, "src/nested/keep.py", "def keep(): pass\n");
        Write(Repo, "generated/kept.py", "def kept(): pass\n");
        Write(Repo, "generated/other.py", "def other(): pass\n");
        Git(Repo, "init", "-q");
        Git(Repo, "add", ".");
        Git(Repo, "add", "-f", "generated/kept.py");
        Commit(Repo);

        var compositor = Path.Combine(Repo, "vendor", "Compositor");
        Write(compositor, "compositor.py", "def compose(): pass\n");
        Write(compositor, "Package.swift", "// swift-tools-version:5.9\nimport PackageDescription\nlet package = Package(name: \"Compositor\")\n");
        Git(compositor, "init", "-q");
        Git(compositor, "add", ".");
        Commit(compositor);
        Write(Repo, ".gitmodules", "[submodule \"vendor/Compositor\"]\n\tpath = vendor/Compositor\n\turl = ../Compositor\n");
        Git(Repo, "add", ".gitmodules", "vendor/Compositor");
        Commit(Repo);

        File.Delete(Path.Combine(Repo, "src", "deleted.py"));
        Write(Repo, "vendor/PhotoCatalog/catalog.py", "def catalog(): pass\n");
        Write(Repo, "vendor/PhotoCatalog/Cargo.toml", "[package]\nname = \"photo-catalog\"\nversion = \"0.1.0\"\n");
        Write(Repo, "ignored.py", "def ignored(): pass\n");
        Write(Repo, "excluded.py", "def excluded(): pass\n");
        File.AppendAllText(Path.Combine(Repo, ".git", "info", "exclude"), "excluded.py\n");
        Write(Repo, "untracked.py", "def untracked(): pass\n");

        var scratch = Path.Combine(Repo, "scratch");
        Write(scratch, "notes.py", "def notes(): pass\n");
        Git(scratch, "init", "-q");
    }

    private void AddManifests()
    {
        Write(Repo, "Cargo.toml", "[package]\nname = \"lens\"\nversion = \"0.1.0\"\n");
        Write(Repo, "Package.swift", "// swift-tools-version:5.9\nimport PackageDescription\nlet package = Package(name: \"Lens\")\n");
    }

    private static IndexingPipeline CreatePipeline(InMemoryGraphStore store) =>
        new(
            store,
            [new TreeSitterExtractor(NullLogger<TreeSitterExtractor>.Instance)],
            Options.Create(new IndexingOptions { MaxParallelFiles = 1 }),
            new LocalFileSystem(),
            NullLogger<IndexingPipeline>.Instance,
            cargoManifestExtractor: new CargoManifestExtractor(),
            swiftPackageManifestExtractor: new SwiftPackageManifestExtractor());

    internal static void Commit(string repository) =>
        Git(repository, "-c", "user.name=CodeGraph Tests", "-c", "user.email=codegraph-tests@example.com",
            "-c", "commit.gpgsign=false", "commit", "-q", "-m", "commit");

    internal static void Git(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi).ShouldNotBeNull();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)} failed: {stdout.Result}{stderr}");
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
