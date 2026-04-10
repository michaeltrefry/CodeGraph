using CodeGraph.Data;
using CodeGraph.Models.Memory;
using Microsoft.Extensions.Logging;

namespace CodeGraph.Services.Memory;

public class MemoryObservationMigrationService(
    IMemoryGraphStore store,
    ILogger<MemoryObservationMigrationService> logger)
{
    public async Task<MemoryObservationMigrationResult> MigrateAsync()
    {
        var observations = await store.GetAllObservationsAsync();
        var result = new MemoryObservationMigrationResult
        {
            ObservationsRead = observations.Count,
        };

        foreach (var observation in observations)
        {
            var originalEntityIds = observation.AboutEntityIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var originalClaimIds = observation.AboutClaimIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var matchedClaimIds = await FindMatchingClaimIdsAsync(observation);
            foreach (var claimId in matchedClaimIds)
                observation.AboutClaimIds.Add(claimId);

            observation.AboutEntityIds = observation.AboutEntityIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            observation.AboutClaimIds = observation.AboutClaimIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var addedEntityLinks = observation.AboutEntityIds.Count(id => !originalEntityIds.Contains(id));
            var addedClaimLinks = observation.AboutClaimIds.Count(id => !originalClaimIds.Contains(id));

            if (addedEntityLinks == 0 && addedClaimLinks == 0)
                continue;

            await store.CreateObservationAsync(observation);
            result.ObservationsUpdated++;
            result.EntityLinksAdded += addedEntityLinks;
            result.ClaimLinksAdded += addedClaimLinks;
        }

        logger.LogInformation(
            "Refit {UpdatedCount}/{ReadCount} memory observations to ABOUT links ({EntityLinkCount} entity links, {ClaimLinkCount} claim links)",
            result.ObservationsUpdated,
            result.ObservationsRead,
            result.EntityLinksAdded,
            result.ClaimLinksAdded);

        return result;
    }

    private async Task<List<string>> FindMatchingClaimIdsAsync(MemoryObservation observation)
    {
        var claimIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await MatchClaimTextAsync(observation.Claim, claimIds);
        await MatchClaimTextAsync(observation.ConflictsWith, claimIds);

        return claimIds.ToList();
    }

    private async Task MatchClaimTextAsync(string text, ISet<string> claimIds)
    {
        var normalizedText = MemoryClaimIngestionService.NormalizeFreeText(text);
        if (string.IsNullOrWhiteSpace(normalizedText))
            return;

        var seeds = await store.SearchClaimsAsync(normalizedText, null, limit: 10, includeSuperseded: true);
        foreach (var (claim, _, _) in seeds)
        {
            if (string.Equals(claim.NormalizedText, normalizedText, StringComparison.OrdinalIgnoreCase))
                claimIds.Add(claim.Id);
        }
    }
}
