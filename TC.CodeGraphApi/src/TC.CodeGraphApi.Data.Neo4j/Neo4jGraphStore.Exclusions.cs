using Neo4j.Driver;

namespace TC.CodeGraphApi.Data.Neo4j;

public partial class Neo4jGraphStore
{
    // ── Exclusion Rules ───────────────────────────────────────────────────

    public async Task<IReadOnlyList<ExclusionRuleEntity>> ListExclusionRulesAsync()
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (e:ExclusionRule) RETURN e ORDER BY e.targetType, e.targetValue");
            var results = new List<ExclusionRuleEntity>();
            await foreach (var record in cursor)
                results.Add(MapExclusionRuleNode(record["e"].As<INode>()));
            return results;
        });
    }

    public async Task<ExclusionRuleEntity?> GetExclusionRuleAsync(long id)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(
                "MATCH (e:ExclusionRule {appId: $id}) RETURN e",
                new { id });
            if (await cursor.FetchAsync())
                return MapExclusionRuleNode(cursor.Current["e"].As<INode>());
            return null;
        });
    }

    public async Task<ExclusionRuleEntity> CreateExclusionRuleAsync(ExclusionRuleEntity rule)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MERGE (seq:Sequence {name: 'exclusion_rule_id'})
                ON CREATE SET seq.value = 0
                SET seq.value = seq.value + 1
                WITH seq.value AS newId
                CREATE (e:ExclusionRule {
                    appId: newId,
                    targetType: $targetType,
                    targetValue: $targetValue,
                    exclusionType: $exclusionType,
                    reason: $reason,
                    createdBy: $createdBy,
                    createdAt: datetime(),
                    updatedAt: datetime()
                })
                RETURN e
                """,
                new
                {
                    targetType = rule.TargetType,
                    targetValue = rule.TargetValue,
                    exclusionType = rule.ExclusionType,
                    reason = rule.Reason,
                    createdBy = rule.CreatedBy
                });
            await cursor.FetchAsync();
            return MapExclusionRuleNode(cursor.Current["e"].As<INode>());
        });
    }

    public async Task<ExclusionRuleEntity?> UpdateExclusionRuleAsync(long id, string exclusionType, string? reason)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (e:ExclusionRule {appId: $id})
                SET e.exclusionType = $exclusionType,
                    e.reason = $reason,
                    e.updatedAt = datetime()
                RETURN e
                """,
                new { id, exclusionType, reason });
            if (await cursor.FetchAsync())
                return MapExclusionRuleNode(cursor.Current["e"].As<INode>());
            return null;
        });
    }

    public async Task<bool> DeleteExclusionRuleAsync(long id)
    {
        await using var session = sessionFactory.GetSession();
        return await session.ExecuteWriteAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (e:ExclusionRule {appId: $id})
                DELETE e
                RETURN count(e) AS deleted
                """,
                new { id });
            await cursor.FetchAsync();
            return cursor.Current["deleted"].As<int>() > 0;
        });
    }

    public async Task<HashSet<string>> GetSecretFilePathsAsync(string project)
    {
        await using var session = sessionFactory.GetSession(AccessMode.Read);
        return await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync("""
                MATCH (f:SecurityFinding {project: $project, category: 'secret'})
                WHERE f.filePath IS NOT NULL
                RETURN DISTINCT f.filePath AS filePath
                """,
                new { project });

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var record in cursor)
                paths.Add(record["filePath"].As<string>());
            return paths;
        });
    }

    private static ExclusionRuleEntity MapExclusionRuleNode(INode node) => new()
    {
        Id = node["appId"].As<long>(),
        TargetType = node["targetType"].As<string>(),
        TargetValue = node["targetValue"].As<string>(),
        ExclusionType = node["exclusionType"].As<string>(),
        Reason = GetStringOrNull(node, "reason"),
        CreatedBy = GetStringOrNull(node, "createdBy") ?? "",
        CreatedAt = GetDateTimeOrNull(node, "createdAt") ?? DateTime.MinValue,
        UpdatedAt = GetDateTimeOrNull(node, "updatedAt") ?? DateTime.MinValue
    };
}
