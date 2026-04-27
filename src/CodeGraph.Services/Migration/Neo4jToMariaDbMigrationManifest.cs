namespace CodeGraph.Services.Migration;

public sealed record Neo4jToMariaDbMigrationManifest(
    IReadOnlyList<Neo4jToMariaDbMigrationArea> Areas)
{
    public static Neo4jToMariaDbMigrationManifest Current { get; } = new(
    [
        new("repositories", 10, "Project metadata, groups, local paths, repo URLs, default branches, and commit SHAs."),
        new("graph", 20, "Graph nodes, edges, cross-repo edges, file hashes, clusters, and sync state."),
        new("wiki", 30, "Convention/wiki pages, revisions, attachments, and section metadata."),
        new("analysis", 40, "Repository analyses, analysis batches, analysis requests, node analysis, and generated CODEGRAPH metadata."),
        new("reviews", 50, "Project diagnostics, project review runs/findings, repository review runs/findings, and sections."),
        new("metrics", 60, "File metrics, project health/security summaries, security findings, usage metrics, and telemetry rollups."),
        new("vectors", 70, "Embeddings and vector metadata for nodes, memory, and semantic search surfaces."),
        new("memory", 80, "Claim-centric memory entities, claims, evidence, observations, claim/entity edges, and write receipts."),
        new("assistant", 90, "Assistant runs, chat messages, debug exchanges, trace audits, MCP tokens, and tool invocation telemetry."),
        new("jobs", 100, "Job schedules, active schedule state, and indexer run history.")
    ]);
}

public sealed record Neo4jToMariaDbMigrationArea(
    string Key,
    int Order,
    string Description);

