using CodeGraph.Data;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services;

public class WikiSectionSeedService(IWikiStore store, ILogger<WikiSectionSeedService> logger) : IWikiSectionSeedService
{
    private static readonly IReadOnlyList<WikiSectionEntity> DefaultSections =
    [
        CreateDefault("general", "General", "General documentation and guides", "book", 0, isSystem: false, allowUserPages: true, hasRawContent: false),
        CreateDefault("conventions", "Conventions", "Team conventions and coding standards", "scale", 1, isSystem: false, allowUserPages: true, hasRawContent: false),
        CreateDefault("skills", "Skills", "Claude Code skills with installable artifacts", "zap", 2, isSystem: false, allowUserPages: true, hasRawContent: false),
        CreateDefault("agents", "Agents", "Claude Code subagent configurations", "bot", 3, isSystem: false, allowUserPages: true, hasRawContent: false),
        CreateDefault("mcp-documentation", "MCP Documentation", "Auto-generated MCP tool documentation", "cpu", 4, isSystem: true, allowUserPages: false, hasRawContent: false)
    ];

    public async Task EnsureDefaultSectionsAsync()
    {
        var created = 0;
        var updated = 0;

        foreach (var section in DefaultSections)
        {
            var existing = await store.GetSectionBySlugAsync(section.Slug);
            if (existing is null)
            {
                var now = DateTime.UtcNow;
                await store.CreateSectionAsync(new WikiSectionEntity
                {
                    Slug = section.Slug,
                    Title = section.Title,
                    Description = section.Description,
                    Icon = section.Icon,
                    SortOrder = section.SortOrder,
                    IsSystem = section.IsSystem,
                    AllowUserPages = section.AllowUserPages,
                    HasRawContent = section.HasRawContent,
                    CreatedAt = now,
                    UpdatedAt = now
                });

                created++;
                logger.LogInformation("Created missing default wiki section {Slug}", section.Slug);
                continue;
            }

            if (!NeedsUpdate(existing, section))
                continue;

            existing.Title = section.Title;
            existing.Description = section.Description;
            existing.Icon = section.Icon;
            existing.SortOrder = section.SortOrder;
            existing.IsSystem = section.IsSystem;
            existing.AllowUserPages = section.AllowUserPages;
            existing.HasRawContent = section.HasRawContent;
            existing.UpdatedAt = DateTime.UtcNow;

            await store.UpdateSectionAsync(existing);
            updated++;
            logger.LogInformation("Reconciled default wiki section {Slug}", section.Slug);
        }

        logger.LogInformation(
            "Wiki section reconciliation complete: {Created} created, {Updated} updated",
            created, updated);
    }

    private static bool NeedsUpdate(WikiSectionEntity existing, WikiSectionEntity expected)
    {
        return !string.Equals(existing.Title, expected.Title, StringComparison.Ordinal) ||
               !string.Equals(existing.Description, expected.Description, StringComparison.Ordinal) ||
               !string.Equals(existing.Icon, expected.Icon, StringComparison.Ordinal) ||
               existing.SortOrder != expected.SortOrder ||
               existing.IsSystem != expected.IsSystem ||
               existing.AllowUserPages != expected.AllowUserPages ||
               existing.HasRawContent != expected.HasRawContent;
    }

    private static WikiSectionEntity CreateDefault(
        string slug,
        string title,
        string description,
        string icon,
        int sortOrder,
        bool isSystem,
        bool allowUserPages,
        bool hasRawContent)
    {
        return new WikiSectionEntity
        {
            Slug = slug,
            Title = title,
            Description = description,
            Icon = icon,
            SortOrder = sortOrder,
            IsSystem = isSystem,
            AllowUserPages = allowUserPages,
            HasRawContent = hasRawContent
        };
    }
}
