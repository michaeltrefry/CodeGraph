namespace CodeGraph.Models;

public static class GraphNodeKey
{
    public static string Create(string project, string qualifiedName)
        => $"{project.Length}:{project}{qualifiedName}";
}
