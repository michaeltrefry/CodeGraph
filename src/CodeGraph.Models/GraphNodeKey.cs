namespace CodeGraph.Models;

public static class GraphNodeKey
{
    public static string Create(string repository, string qualifiedName)
        => $"{repository.Length}:{repository}{qualifiedName}";
}
