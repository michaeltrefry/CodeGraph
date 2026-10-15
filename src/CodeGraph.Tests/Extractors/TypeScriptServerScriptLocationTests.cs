using CodeGraph.Extractors.TypeScript;
using Shouldly;

namespace CodeGraph.Tests.Extractors;

/// <summary>
/// sc-26145: the sidecar script is found next to the binary first, else in the repository the
/// binary was built in, where a git worktree's <c>.git</c> file marks the root as much as a main
/// checkout's <c>.git</c> directory does.
/// </summary>
public sealed class TypeScriptServerScriptLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-ts-script-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Script(string checkout)
    {
        var script = Path.Combine(_root, checkout, "tools", "ts-extractor", "dist", "server.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "// sidecar");
        return script;
    }

    private string BinaryDirectory(string checkout)
    {
        var directory = Path.Combine(_root, checkout, "src", "App", "bin", "Debug", "net10.0");
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Fact]
    public void FindServerScript_InAGitWorktree_UsesTheWorktreesOwnSidecar()
    {
        // The main checkout (.git directory) holds the worktree, whose .git is a file.
        Directory.CreateDirectory(Path.Combine(_root, "main", ".git"));
        Script("main");
        const string worktree = "main/.claude/worktrees/agent";
        File.WriteAllText(Path.Combine(_root, "main", ".claude", "worktrees", "agent", ".git").EnsureDirectory(),
            "gitdir: ../../../.git/worktrees/agent\n");
        var worktreeScript = Script(worktree);

        TypeScriptServerManager.FindServerScript(BinaryDirectory(worktree)).ShouldBe(worktreeScript);
    }

    [Fact]
    public void FindServerScript_InAMainCheckout_UsesItsSidecar()
    {
        Directory.CreateDirectory(Path.Combine(_root, "main", ".git"));
        var script = Script("main");

        TypeScriptServerManager.FindServerScript(BinaryDirectory("main")).ShouldBe(script);
    }

    [Fact]
    public void FindServerScript_PrefersTheSidecarNextToTheBinary()
    {
        Directory.CreateDirectory(Path.Combine(_root, "main", ".git"));
        Script("main");
        var binary = BinaryDirectory("main");
        var published = Path.Combine(binary, "tools", "ts-extractor", "dist", "server.js");
        Directory.CreateDirectory(Path.GetDirectoryName(published)!);
        File.WriteAllText(published, "// published");

        TypeScriptServerManager.FindServerScript(binary).ShouldBe(published);
    }

    [Fact]
    public void FindServerScript_WorktreeWithoutABuiltSidecar_DoesNotFallBackToTheMainCheckout()
    {
        Directory.CreateDirectory(Path.Combine(_root, "main", ".git"));
        Script("main");
        const string worktree = "main/.claude/worktrees/agent";
        File.WriteAllText(Path.Combine(_root, "main", ".claude", "worktrees", "agent", ".git").EnsureDirectory(),
            "gitdir: ../../../.git/worktrees/agent\n");

        TypeScriptServerManager.FindServerScript(BinaryDirectory(worktree)).ShouldBeNull();
    }
}

file static class PathExtensions
{
    public static string EnsureDirectory(this string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return filePath;
    }
}
