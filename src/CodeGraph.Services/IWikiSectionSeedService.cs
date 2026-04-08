namespace CodeGraph.Services;

public interface IWikiSectionSeedService
{
    Task EnsureDefaultSectionsAsync();
}
