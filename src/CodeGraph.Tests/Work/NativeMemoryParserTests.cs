using CodeGraph.Models.Work;
using CodeGraph.Services.Work;
using Shouldly;

namespace CodeGraph.Tests.Work;

/// <summary>The native-memory candidate parser of the verify-before-import flow (D18, design §10.3).</summary>
public sealed class NativeMemoryParserTests
{
    [Fact]
    public void Claude_memory_files_become_candidates_with_a_path_pre_check()
    {
        var root = Path.Combine(Path.GetTempPath(), "cg-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src", "Present"));
        File.WriteAllText(Path.Combine(root, "CodeGraph.sln"), "");
        try
        {
            var files = new[]
            {
                new MemoryFile("/m/MEMORY.md", "- [index](a.md)"),
                new MemoryFile("/m/commands.md", "---\nname: Build commands\ndescription: How to build\ntype: reference\n---\nRun `dotnet build CodeGraph.sln` in src/Present."),
                new MemoryFile("/m/stale.md", "---\nname: Old layout\ndescription: Where the extractor lives\ntype: project\n---\nSee src/Gone/Extractor.cs."),
                new MemoryFile("/m/infra.md", "---\nname: Infra\ndescription: The database host\ntype: project\n---\nMariaDB runs on the shared host."),
                new MemoryFile("/m/pref.md", "---\nname: flow\ndescription: \"Prefers continuous work\"\nmetadata:\n  node_type: memory\n  type: feedback\n---\nKeep going.")
            };

            var candidates = NativeMemoryParser.ParseClaude(files, new RepositoryCheckout(root));

            candidates.Count.ShouldBe(4);
            var byFile = candidates.ToDictionary(candidate => candidate.Candidate.SourceFile);
            byFile["/m/commands.md"].Candidate.Verification.ShouldBe(ImportVerification.Verified);
            byFile["/m/commands.md"].Candidate.Entry.Paths.ShouldBe(["CodeGraph.sln", "src/Present"], ignoreOrder: true);
            byFile["/m/commands.md"].Candidate.Entry.Summary.ShouldBe("How to build");
            byFile["/m/stale.md"].Candidate.Verification.ShouldBe(ImportVerification.Stale);
            byFile["/m/stale.md"].Missing.ShouldBe(["src/Gone/Extractor.cs"]);
            byFile["/m/infra.md"].Candidate.Verification.ShouldBe(ImportVerification.Unverifiable);
            byFile["/m/pref.md"].Candidate.Verification.ShouldBe(ImportVerification.Personal);
            byFile["/m/pref.md"].Candidate.Entry.Kind.ShouldBe(KnowledgeKind.Lesson);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void The_checkout_index_never_resolves_tokens_outside_the_repository()
    {
        var root = Path.Combine(Path.GetTempPath(), "cg-checkout-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(Path.GetTempPath(), "cg-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "pkg"));
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "");
        File.WriteAllText(Path.Combine(root, "node_modules", "pkg", "index.js"), "");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "");
        Directory.CreateSymbolicLink(Path.Combine(root, "linked"), outside);
        try
        {
            var checkout = new RepositoryCheckout(root);
            checkout.Resolve("src/A.cs").ShouldBe((true, "src/A.cs"));
            checkout.Resolve("A.cs").ShouldBe((true, "src/A.cs"));
            // Absolute, parent, drive and backslash tokens are never paths, and nothing outside the root resolves.
            checkout.Resolve(Path.Combine(outside, "secret.txt")).IsPath.ShouldBeFalse();
            checkout.Resolve("/etc/passwd").IsPath.ShouldBeFalse();
            checkout.Resolve("../" + Path.GetFileName(outside) + "/secret.txt").IsPath.ShouldBeFalse();
            checkout.Resolve("C:/Windows/win.ini").IsPath.ShouldBeFalse();
            checkout.Resolve("src\\A.cs").IsPath.ShouldBeFalse();
            // A symbolic link is not followed; skipped directories are not indexed.
            checkout.Resolve("linked/secret.txt").Found.ShouldBeNull();
            checkout.Resolve("secret.txt").IsPath.ShouldBeFalse();
            checkout.Resolve("node_modules/pkg/index.js").Found.ShouldBeNull();
            checkout.Truncated.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Codex_task_groups_become_candidates_filtered_by_repository()
    {
        var memory = new MemoryFile("/c/MEMORY.md",
            "# Task Group: CodeGraph imports\n\nscope: CodeGraph work\napplies_to: cwd=/Users/x/Repos/CodeGraph\n\n## Task 1: thing\n\n- a task line\n\n" +
            "## User preferences\n\n- the user wants short answers\n\n## Reusable knowledge\n\n- Migrations live in sql/migrations.\n\n" +
            "## Failures and how to do differently\n\n- Symptom: build fails -> fix: restore first\n\n" +
            "# Task Group: SceneWorks review\n\napplies_to: cwd=/Users/x/SceneWorks\n\n## Reusable knowledge\n\n- Not this repository.\n");

        var candidates = NativeMemoryParser.ParseCodex(memory, null, "CodeGraph");

        candidates.Select(candidate => (candidate.Candidate.Entry.Kind, candidate.Candidate.Verification)).ShouldBe(
        [
            (KnowledgeKind.Lesson, ImportVerification.Personal),
            (KnowledgeKind.Fact, ImportVerification.Unverifiable),
            (KnowledgeKind.Gotcha, ImportVerification.Unverifiable)
        ]);
        candidates[1].Candidate.SourceFile.ShouldBe("/c/MEMORY.md#CodeGraph imports");
        candidates[1].Referenced.ShouldBe(["sql/migrations"]);
    }
}
