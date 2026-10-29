using System.Text.Json;

namespace CodeGraph.Tests.Extractors;

/// <summary>
/// A Cargo workspace on disk plus shell stand-ins for cargo, rustc, rust-analyzer and scip.
/// The fake rust-analyzer records every rust-project.json it is given and fails for the crates
/// in <see cref="FailingCratesVariable"/>; the fake scip prints the canned SCIP documents of
/// the crates that are workspace members of the analyzed rust-project.json.
/// </summary>
internal sealed class FakeRustToolchain : IDisposable
{
    public const string FailingCratesVariable = "CODEGRAPH_TEST_FAILING_CRATES";
    public const string SlowCratesVariable = "CODEGRAPH_TEST_SLOW_CRATES";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codegraph-rust-groups-{Guid.NewGuid():N}");
    private readonly List<FakeCrate> _crates = [];
    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);

    public string RootPath => Path.Combine(_root, "repo");
    public string ManifestPath => Path.Combine(RootPath, "Cargo.toml");
    public string ToolDirectory => Path.Combine(_root, "tools");
    public string CargoLogPath => Path.Combine(_root, "cargo.log");
    public string ProjectJsonDirectory => Path.Combine(_root, "repository-json");

    /// <summary>Created by the fake rust-analyzer once a slow crate's group is recorded and sleeping.</summary>
    public string SlowAnalyzerStartedPath => Path.Combine(_root, "slow-analyzer-started");
    private string ScipDirectory => Path.Combine(_root, "scip");
    private string MetadataPath => Path.Combine(_root, "metadata.json");
    private string CheckOutputPath => Path.Combine(_root, "check-output.json");

    public FakeRustToolchain()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(ToolDirectory);
        Directory.CreateDirectory(ProjectJsonDirectory);
        Directory.CreateDirectory(ScipDirectory);
        File.WriteAllText(CheckOutputPath, "");
    }

    public FakeRustToolchain AddCrate(
        string name,
        string source,
        string scipDocument,
        string[]? dependencies = null,
        bool procMacro = false)
    {
        var directory = Path.Combine(RootPath, "crates", name);
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "Cargo.toml"),
            $"[package]\nname = \"{name}\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
        File.WriteAllText(Path.Combine(directory, "src", "lib.rs"), source);
        File.WriteAllText(Path.Combine(ScipDirectory, $"{name}.json"), scipDocument);
        _crates.Add(new FakeCrate(name, directory, dependencies ?? [], procMacro));
        return this;
    }

    public string PackageId(string name) =>
        $"path+file://{Path.Combine(RootPath, "crates", name)}#0.1.0";

    public void SetCheckOutput(params object[] messages) =>
        File.WriteAllText(CheckOutputPath, string.Join("\n", messages.Select(message => JsonSerializer.Serialize(message))) + "\n");

    /// <summary>Writes the workspace manifest, cargo metadata and tools, and puts them on PATH.</summary>
    public void Install()
    {
        File.WriteAllText(ManifestPath,
            "[workspace]\nresolver = \"2\"\nmembers = [" +
            string.Join(", ", _crates.Select(crate => $"\"crates/{crate.Name}\"")) + "]\n");
        File.WriteAllText(MetadataPath, JsonSerializer.Serialize(new
        {
            packages = _crates.Select(crate => new Dictionary<string, object?>
            {
                ["name"] = crate.Name,
                ["version"] = "0.1.0",
                ["id"] = PackageId(crate.Name),
                ["source"] = null,
                ["manifest_path"] = Path.Combine(crate.Directory, "Cargo.toml"),
                ["edition"] = "2021",
                ["repository"] = null,
                ["targets"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["kind"] = new[] { crate.ProcMacro ? "proc-macro" : "lib" },
                        ["name"] = crate.Name,
                        ["src_path"] = Path.Combine(crate.Directory, "src", "lib.rs"),
                        ["edition"] = "2021"
                    }
                }
            }),
            workspace_members = _crates.Select(crate => PackageId(crate.Name)),
            workspace_root = RootPath,
            resolve = new
            {
                nodes = _crates.Select(crate => new
                {
                    id = PackageId(crate.Name),
                    deps = crate.Dependencies.Select(dependency => new
                    {
                        name = dependency,
                        pkg = PackageId(dependency),
                        dep_kinds = new[] { new Dictionary<string, object?> { ["kind"] = null } }
                    }),
                    features = new[] { "default" }
                })
            }
        }));

        AddTool("cargo",
            "#!/bin/sh\n" +
            $"printf '%s\\n' \"$*\" >> '{CargoLogPath}'\n" +
            "case \"$1\" in\n" +
            $"  metadata) /bin/cat '{MetadataPath}' ;;\n" +
            $"  check) /bin/cat '{CheckOutputPath}' ;;\n" +
            "  *) exit 2 ;;\n" +
            "esac\n");
        AddTool("rustc", $"#!/bin/sh\necho '{Path.Combine(_root, "sysroot")}'\n");
        AddTool("rust-analyzer",
            "#!/bin/sh\n" +
            "root=\"$2\"\nout=\"\"\n" +
            "while [ \"$#\" -gt 0 ]; do\n  if [ \"$1\" = \"--output\" ]; then out=\"$2\"; shift 2; else shift; fi\ndone\n" +
            $"n=$(/bin/ls '{ProjectJsonDirectory}' | /usr/bin/wc -l | /usr/bin/tr -d ' ')\n" +
            $"/bin/cp \"$root/rust-project.json\" '{ProjectJsonDirectory}'/\"$n.json\"\n" +
            $"for crate in ${SlowCratesVariable}; do\n" +
            $"  if /usr/bin/grep -q \"\\\"display_name\\\":\\\"$crate\\\".*\\\"is_workspace_member\\\":true\" \"$root/rust-project.json\"; then : > '{SlowAnalyzerStartedPath}'; /bin/sleep 30; fi\n" +
            "done\n" +
            $"for crate in ${FailingCratesVariable}; do\n" +
            "  if /usr/bin/grep -q \"\\\"display_name\\\":\\\"$crate\\\".*\\\"is_workspace_member\\\":true\" \"$root/rust-project.json\"; then\n" +
            "    echo \"synthetic analyzer failure for $crate\" >&2; exit 3\n  fi\n" +
            "done\n" +
            "/bin/cp \"$root/rust-project.json\" \"$out\"\n");
        AddTool("scip",
            "#!/bin/sh\nfile=\"$3\"\nprintf '{\"documents\":['\nsep=\"\"\n" +
            $"for crate in {string.Join(' ', _crates.Select(crate => crate.Name))}; do\n" +
            "  if /usr/bin/grep -q \"\\\"display_name\\\":\\\"$crate\\\".*\\\"is_workspace_member\\\":true\" \"$file\"; then\n" +
            $"    printf '%s' \"$sep\"; /bin/cat '{ScipDirectory}'/\"$crate.json\"; sep=\",\"\n" +
            "  fi\n" +
            "done\nprintf ']}'\n");

        SetEnvironment("PATH", ToolDirectory);
    }

    /// <summary>Waits (bounded) until a slow crate's fake rust-analyzer is running.</summary>
    public async Task WaitForSlowAnalyzerAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(SlowAnalyzerStartedPath) && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        if (!File.Exists(SlowAnalyzerStartedPath))
            throw new TimeoutException("The slow fake rust-analyzer never started.");
    }

    public void SetEnvironment(string name, string? value)
    {
        if (!_originalEnvironment.ContainsKey(name))
            _originalEnvironment[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public IReadOnlyList<string> ProjectJsonFiles() =>
        Directory.EnumerateFiles(ProjectJsonDirectory, "*.json")
            .OrderBy(path => int.Parse(Path.GetFileNameWithoutExtension(path)))
            .Select(File.ReadAllText)
            .ToList();

    /// <summary>Workspace-member display names of one recorded rust-project.json.</summary>
    public static IReadOnlyList<string> MemberCrates(string projectJson)
    {
        using var document = JsonDocument.Parse(projectJson);
        return document.RootElement.GetProperty("crates").EnumerateArray()
            .Where(crate => crate.GetProperty("is_workspace_member").GetBoolean())
            .Select(crate => crate.GetProperty("display_name").GetString()!)
            .ToList();
    }

    private void AddTool(string name, string content)
    {
        var path = Path.Combine(ToolDirectory, name);
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _originalEnvironment)
            Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private sealed record FakeCrate(string Name, string Directory, string[] Dependencies, bool ProcMacro);

    /// <summary>A SCIP document defining one function, optionally calling another symbol.</summary>
    public static string ScipDocument(string crate, string function, string? calledSymbol = null)
    {
        var symbol = $"rust-analyzer cargo {crate} 0.1.0 {function}().";
        var occurrences = new List<object>
        {
            new Dictionary<string, object>
            {
                ["symbol"] = symbol,
                ["symbolRoles"] = 1,
                ["range"] = new[] { 0, 7, 7 + function.Length },
                ["enclosingRange"] = new[] { 0, 0, 2, 1 }
            }
        };
        if (calledSymbol is not null)
        {
            occurrences.Add(new Dictionary<string, object>
            {
                ["symbol"] = calledSymbol,
                ["symbolRoles"] = 0,
                ["range"] = new[] { 1, 4, 16 }
            });
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["language"] = "rust",
            ["relativePath"] = $"crates/{crate}/src/lib.rs",
            ["symbols"] = new[]
            {
                new Dictionary<string, object>
                {
                    ["symbol"] = symbol,
                    ["kind"] = "Function",
                    ["displayName"] = function
                }
            },
            ["occurrences"] = occurrences
        });
    }

    public static string Symbol(string crate, string function) => $"rust-analyzer cargo {crate} 0.1.0 {function}().";
}
