using CodeGraph.Data;
using Microsoft.Extensions.DependencyInjection;

namespace CodeGraph.Mcp.Hub;

/// <summary>
/// Resolves and gates MySQL database sources for the MCP Hub. Replaces the previous weak
/// `allowedSources` CSV config — a source must be explicitly opted in (`mcp_hub_enabled`) and,
/// for read-only SQL, set to the `ReadOnlySql` exposure mode. Non-exposed or ambiguous
/// identifiers fail closed — see Shortcut sc-1058.
/// </summary>
public sealed class MySqlSourceExposurePolicy(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Resolves an MCP-exposed source by identifier: numeric id, MCP display name,
    /// "server/database", database name, or server name. Throws when nothing matches or the
    /// identifier is ambiguous.
    /// </summary>
    public async Task<DatabaseSourceEntity> ResolveExposedSourceAsync(string identifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new McpHubProviderPolicyException("A MySQL source identifier is required.");

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDatabaseSourceStore>();
        var exposed = (await store.ListAsync())
            .Where(source => source.McpHubEnabled)
            .ToList();

        return ResolveSource(exposed, identifier.Trim())
            ?? throw new McpHubProviderPolicyException(
                $"No MCP-exposed MySQL source matches '{identifier}'.");
    }

    /// <summary>Resolves a source that is exposed AND set to the <c>ReadOnlySql</c> mode.</summary>
    public async Task<DatabaseSourceEntity> ResolveReadOnlySqlSourceAsync(string identifier, CancellationToken ct)
    {
        var source = await ResolveExposedSourceAsync(identifier, ct);
        if (!string.Equals(source.McpExposureMode, McpSourceExposureModes.ReadOnlySql, StringComparison.OrdinalIgnoreCase))
            throw new McpHubProviderPolicyException(
                $"MySQL source '{identifier}' is not configured for read-only SQL " +
                $"(exposure mode: {source.McpExposureMode}).");
        return source;
    }

    private static DatabaseSourceEntity? ResolveSource(IReadOnlyList<DatabaseSourceEntity> sources, string identifier)
    {
        if (long.TryParse(identifier, out var numericId))
        {
            var byId = sources.FirstOrDefault(source => source.Id == numericId);
            if (byId is not null)
                return byId;
        }

        var byDisplayName = sources.FirstOrDefault(source =>
            !string.IsNullOrWhiteSpace(source.McpDisplayName) &&
            string.Equals(source.McpDisplayName, identifier, StringComparison.OrdinalIgnoreCase));
        if (byDisplayName is not null)
            return byDisplayName;

        var byQualifiedName = sources.FirstOrDefault(source =>
            string.Equals($"{source.ServerName}/{source.DatabaseName}", identifier, StringComparison.OrdinalIgnoreCase));
        if (byQualifiedName is not null)
            return byQualifiedName;

        // Bare database / server names are only accepted when they resolve unambiguously.
        return SingleOrNull(sources, source => source.DatabaseName, identifier)
            ?? SingleOrNull(sources, source => source.ServerName, identifier);
    }

    private static DatabaseSourceEntity? SingleOrNull(
        IReadOnlyList<DatabaseSourceEntity> sources,
        Func<DatabaseSourceEntity, string> selector,
        string identifier)
    {
        var matches = sources
            .Where(source => string.Equals(selector(source), identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}
