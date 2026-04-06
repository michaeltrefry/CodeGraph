using Microsoft.Extensions.Logging;
using CodeGraph.Models;

namespace CodeGraph.Services.Pipeline;

public partial class IndexingPipeline
{
    private void CreateStructuralNodes(string projectName, string rootPath,
        List<string> files, string[] csprojFiles, GraphBuffer buffer)
    {
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
            var relPath = Path.GetRelativePath(rootPath, file);
            var relDir = Path.GetDirectoryName(relPath) ?? "";

            // File node
            buffer.AddNode(new GraphNode
            {
                Project = projectName,
                Label = NodeLabel.File,
                Name = Path.GetFileName(file),
                QualifiedName = $"{projectName}:{relPath}",
                FilePath = relPath
            });

            // Folder nodes (walk up the directory tree)
            var dir = relDir;
            while (!string.IsNullOrEmpty(dir) && folders.Add(dir))
            {
                buffer.AddNode(new GraphNode
                {
                    Project = projectName,
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

    private void ExtractNuGetReferences(string projectName, string[] csprojFiles,
        GraphBuffer buffer)
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
            }
        }
    }
}
