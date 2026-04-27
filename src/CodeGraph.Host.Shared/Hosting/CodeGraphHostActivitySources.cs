using System.Diagnostics;

namespace CodeGraph.Host.Shared.Hosting;

public static class CodeGraphHostActivitySources
{
    public static readonly ActivitySource Hosting = new("CodeGraph.Hosting");
    public static readonly ActivitySource InternalServiceAuth = new("CodeGraph.InternalServiceAuth");
}
