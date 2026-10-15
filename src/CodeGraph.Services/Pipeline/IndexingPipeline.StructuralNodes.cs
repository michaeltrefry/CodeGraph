using Microsoft.Extensions.Logging;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    private void CreateStructuralNodes(string projectName, string rootPath,
        List<string> files, string[] csprojFiles, GraphBuffer buffer)
    {
        var projectRoots = BuildProjectRoots(rootPath, csprojFiles);

        // Repository node
        buffer.AddNode(new GraphNode
        {
            Project = projectName,
            Label = NodeLabel.Repository,
            Name = projectName,
            QualifiedName = projectName
        });

        // DotnetProject nodes — one per .csproj found in the repo
        foreach (var csproj in csprojFiles)
        {
            var csprojName = Path.GetFileNameWithoutExtension(csproj);
            var dotnetProjectQN = $"{projectName}:project:{csprojName}";
            buffer.AddNode(new GraphNode
            {
                Project = projectName,
                DotnetProject = csprojName,
                Label = NodeLabel.DotnetProject,
                Name = csprojName,
                QualifiedName = dotnetProjectQN
            });
            buffer.AddEdge(new PendingEdge(
                projectName,
                dotnetProjectQN,
                EdgeType.CONTAINS_PROJECT));
        }

        // Folder and File nodes, with CONTAINS edges
        var folders = new HashSet<string>();
        foreach (var file in files)
        {
            var relPath = NormalizeRelativePath(Path.GetRelativePath(rootPath, file));
            var relDir = NormalizeRelativePath(Path.GetDirectoryName(relPath) ?? "");
            var owningProject = FindOwningProject(relPath, projectRoots);

            // File node
            buffer.AddNode(new GraphNode
            {
                Project = projectName,
                DotnetProject = owningProject,
                Label = NodeLabel.File,
                Name = Path.GetFileName(file),
                QualifiedName = $"{projectName}:{relPath}",
                FilePath = relPath
            });

            // Folder nodes (walk up the directory tree)
            var dir = relDir;
            while (!string.IsNullOrEmpty(dir) && folders.Add(dir))
            {
                var folderOwningProject = FindOwningProject(dir, projectRoots);
                buffer.AddNode(new GraphNode
                {
                    Project = projectName,
                    DotnetProject = folderOwningProject,
                    Label = NodeLabel.Folder,
                    Name = Path.GetFileName(dir),
                    QualifiedName = $"{projectName}:{dir}"
                });

                var parentDir = Path.GetDirectoryName(dir) ?? "";
                var parentQN = string.IsNullOrEmpty(parentDir)
                    ? projectName
                    : $"{projectName}:{parentDir}";
                buffer.AddEdge(new PendingEdge(
                    parentQN,
                    $"{projectName}:{dir}",
                    EdgeType.CONTAINS_FOLDER));

                dir = parentDir;
            }

            // File containment edge
            var folderQN = string.IsNullOrEmpty(relDir)
                ? projectName
                : $"{projectName}:{relDir}";
            buffer.AddEdge(new PendingEdge(
                folderQN,
                $"{projectName}:{relPath}",
                EdgeType.CONTAINS_FILE));
        }
    }

    private static Dictionary<string, string> BuildProjectRoots(string rootPath, IEnumerable<string> csprojFiles)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var csproj in csprojFiles)
        {
            var projectName = Path.GetFileNameWithoutExtension(csproj);
            var projectDir = NormalizeRelativePath(Path.GetRelativePath(rootPath, Path.GetDirectoryName(csproj) ?? rootPath));
            result[projectDir] = projectName;
        }

        return result;
    }

    private static string? FindOwningProject(string relativePath, IReadOnlyDictionary<string, string> projectRoots)
    {
        if (projectRoots.Count == 0)
            return null;

        var normalized = NormalizeRelativePath(relativePath);
        string? bestMatch = null;
        var bestLength = -1;

        foreach (var (projectRoot, projectName) in projectRoots)
        {
            var matches = string.IsNullOrEmpty(projectRoot) ||
                          normalized.Equals(projectRoot, StringComparison.OrdinalIgnoreCase) ||
                          normalized.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase);
            if (!matches)
                continue;

            if (projectRoot.Length <= bestLength)
                continue;

            bestMatch = projectName;
            bestLength = projectRoot.Length;
        }

        return bestMatch;
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace('\\', '/').Trim('/');

    /// <summary>
    /// Reserved slice for DotnetProject nodes being removed; no file lives there. A node left on it
    /// by an interrupted run is removed by the next one.
    /// </summary>
    internal const string StaleDotnetProjectSlice = ".codegraph/stale-dotnet-project";

    /// <summary>
    /// Stored DotnetProject nodes whose .csproj discovery no longer yields (deleted, or now
    /// excluded: gitignored, inside a submodule, a dependency directory). They have no file slice,
    /// so they are moved onto <see cref="StaleDotnetProjectSlice"/>, which the incremental write
    /// replaces with nothing. Returns whether there were any, and the project's NuGet package nodes
    /// as orphan candidates (deleted when the removal leaves them without references).
    /// </summary>
    private async Task<(bool Retired, IReadOnlyList<string> OrphanCandidates)> RetireStaleDotnetProjectsAsync(
        string projectName, IEnumerable<string> csprojFiles)
    {
        var current = csprojFiles
            .Select(csproj => $"{projectName}:project:{Path.GetFileNameWithoutExtension(csproj)}")
            .ToHashSet(StringComparer.Ordinal);
        var stale = (await _store.FindNodesByLabelAsync(projectName, NodeLabel.DotnetProject, int.MaxValue))
            .Where(node => !current.Contains(node.QualifiedName))
            .ToList();
        if (stale.Count == 0)
            return (false, []);

        _logger.LogInformation(
            "Removing {Count} DotnetProject node(s) of {Project} whose .csproj discovery no longer yields: {Projects}",
            stale.Count, projectName, string.Join(", ", stale.Select(node => node.Name)));
        foreach (var node in stale)
            await _store.UpsertNodeAsync(node with { FilePath = StaleDotnetProjectSlice });
        var packages = (await _store.FindNodesByLabelAsync(projectName, NodeLabel.NuGetPackage, int.MaxValue))
            .Select(node => node.QualifiedName)
            .ToList();
        return (true, packages);
    }

    private void ExtractNuGetReferences(string projectName, string[] csprojFiles,
        GraphBuffer buffer, bool failOnExtractionError)
    {
        foreach (var csproj in csprojFiles)
        {
            try
            {
                var csprojName = Path.GetFileNameWithoutExtension(csproj);
                var sourceQN = $"{projectName}:project:{csprojName}";
                var csprojXml = _fileSystem.ReadAllText(csproj);
                var refs = _nugetExtractor!.ExtractFromProjectXml(csprojXml);
                foreach (var (packageName, version) in refs)
                {
                    buffer.AddNode(new GraphNode
                    {
                        Project = projectName,
                        Label = NodeLabel.NuGetPackage,
                        Name = packageName,
                        QualifiedName = $"nuget:{packageName}",
                        Properties = new() { ["version"] = version }
                    });
                    buffer.AddEdge(new PendingEdge(
                        sourceQN,
                        $"nuget:{packageName}",
                        EdgeType.REFERENCES_PACKAGE,
                        new() { ["version"] = version }));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract NuGet refs from {Csproj}", csproj);
                if (failOnExtractionError)
                    throw;
            }
        }
    }
}
