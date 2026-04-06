namespace TC.CodeGraphApi.Services;

public interface IMcpDocService
{
    /// <summary>
    /// Regenerate MCP documentation wiki pages from current tool metadata.
    /// Creates new pages for new tools, updates existing pages (preserving manual content), removes pages for deleted tools.
    /// </summary>
    Task RegenerateAsync();
}
