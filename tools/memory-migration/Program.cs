using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Neo4j.Driver;

var options = Options.Parse(args);
if (options.ShowHelp)
{
    Options.PrintUsage();
    return 0;
}

var errors = options.Validate().ToList();
if (errors.Count > 0)
{
    foreach (var error in errors)
        Console.Error.WriteLine($"error: {error}");

    Console.Error.WriteLine();
    Options.PrintUsage();
    return 1;
}

Console.WriteLine(options.Apply
    ? "Applying MemoryGraph -> CodeGraph migration..."
    : "Running dry-run for MemoryGraph -> CodeGraph migration...");

await using var sourceDriver = GraphDatabase.Driver(
    options.SourceUri,
    AuthTokens.Basic(options.SourceUser, options.SourcePassword));
await using var targetDriver = GraphDatabase.Driver(
    options.TargetUri,
    AuthTokens.Basic(options.TargetUser, options.TargetPassword));

await sourceDriver.VerifyConnectivityAsync();
await targetDriver.VerifyConnectivityAsync();

var sourceUsernames = await LoadSourceUsernamesAsync(sourceDriver, options.SourceDatabase);
Console.WriteLine($"Source usernames: {(sourceUsernames.Count == 0 ? "<none>" : string.Join(", ", sourceUsernames))}");

if (!string.IsNullOrWhiteSpace(options.SourceUsername) &&
    !sourceUsernames.Contains(options.SourceUsername, StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"error: source username '{options.SourceUsername}' was not found in the old MemoryGraph store.");
    return 1;
}

var crossUserCollisions = await CountCrossUserEntityCollisionsAsync(
    sourceDriver,
    options.SourceDatabase,
    options.SourceUsername);

if (crossUserCollisions > 0 && !options.PrefixIdsWithUsername)
{
    Console.Error.WriteLine(
        "error: source data contains entity ids shared across multiple usernames. " +
        "Re-run with --prefix-ids-with-username or migrate a single username with --source-username.");
    return 1;
}

var sourceEntities = await LoadEntitiesAsync(sourceDriver, options.SourceDatabase, options.SourceUsername);
var entityIdMap = BuildEntityIdMap(sourceEntities, options.PrefixIdsWithUsername);
var sourceRelationships = await LoadRelationshipsAsync(sourceDriver, options.SourceDatabase, options.SourceUsername, entityIdMap);
var sourceObservations = await LoadObservationsAsync(sourceDriver, options.SourceDatabase, options.SourceUsername, entityIdMap);
var targetBefore = await LoadTargetCountsAsync(targetDriver, options.TargetDatabase);
var overlap = await LoadTargetOverlapAsync(
    targetDriver,
    options.TargetDatabase,
    sourceEntities.Select(e => entityIdMap[(e.Username, e.Id)]).Distinct().ToList(),
    sourceObservations.Select(o => o.Id).Distinct().ToList());

Console.WriteLine();
Console.WriteLine("Source snapshot");
Console.WriteLine($"  entities: {sourceEntities.Count}");
Console.WriteLine($"  relationships: {sourceRelationships.Count}");
Console.WriteLine($"  observations: {sourceObservations.Count}");
Console.WriteLine($"  cross-user id collisions in scope: {crossUserCollisions}");

Console.WriteLine();
Console.WriteLine("Target before migration");
Console.WriteLine($"  memory entities: {targetBefore.Entities}");
Console.WriteLine($"  memory relationships: {targetBefore.Relationships}");
Console.WriteLine($"  memory observations: {targetBefore.Observations}");
Console.WriteLine($"  overlapping entity ids: {overlap.EntityIds}");
Console.WriteLine($"  overlapping observation ids: {overlap.ObservationIds}");

if (!options.Apply)
{
    Console.WriteLine();
    Console.WriteLine("Dry-run only. Re-run with --apply to write migrated data.");
    return 0;
}

if ((overlap.EntityIds > 0 || overlap.ObservationIds > 0) && !options.AllowTargetOverlap)
{
    Console.Error.WriteLine(
        "error: target already contains overlapping entity or observation ids. " +
        "Review the dry-run output and re-run with --allow-target-overlap only if that merge is intentional.");
    return 1;
}

await UpsertEntitiesAsync(targetDriver, options.TargetDatabase, sourceEntities, entityIdMap, options.BatchSize);
await UpsertRelationshipsAsync(targetDriver, options.TargetDatabase, sourceRelationships, options.BatchSize);
await UpsertObservationsAsync(targetDriver, options.TargetDatabase, sourceObservations, options.BatchSize);

var targetAfter = await LoadTargetCountsAsync(targetDriver, options.TargetDatabase);

Console.WriteLine();
Console.WriteLine("Target after migration");
Console.WriteLine($"  memory entities: {targetAfter.Entities}");
Console.WriteLine($"  memory relationships: {targetAfter.Relationships}");
Console.WriteLine($"  memory observations: {targetAfter.Observations}");

return 0;

static async Task<List<string>> LoadSourceUsernamesAsync(IDriver driver, string database)
{
    const string query = """
        MATCH (e:Entity)
        RETURN DISTINCT e.username AS username
        ORDER BY username
        """;

    await using var session = driver.AsyncSession(config => config.WithDatabase(database));
    var result = await session.RunAsync(query);

    var usernames = new List<string>();
    await foreach (var record in result)
        usernames.Add(record["username"].As<string>());

    return usernames;
}

static async Task<int> CountCrossUserEntityCollisionsAsync(IDriver driver, string database, string? username)
{
    var query = """
        MATCH (e:Entity)
        WHERE $username IS NULL OR e.username = $username
        WITH e.id AS id, collect(DISTINCT e.username) AS usernames
        WHERE size(usernames) > 1
        RETURN count(*) AS count
        """;

    await using var session = driver.AsyncSession(config => config.WithDatabase(database));
    var result = await session.RunAsync(query, new { username });
    var record = await result.SingleAsync();
    return record["count"].As<int>();
}

static async Task<List<SourceEntity>> LoadEntitiesAsync(IDriver driver, string database, string? username)
{
    var query = """
        MATCH (e:Entity)
        WHERE $username IS NULL OR e.username = $username
        RETURN e.id AS id,
               e.label AS label,
               e.type AS type,
               e.summary AS summary,
               e.source AS source,
               e.username AS username,
               e.embedding AS embedding,
               e.createdAt AS createdAt,
               e.updatedAt AS updatedAt
        ORDER BY e.updatedAt, e.id
        """;

    await using var session = driver.AsyncSession(config => config.WithDatabase(database));
    var result = await session.RunAsync(query, new { username });

    var entities = new List<SourceEntity>();
    await foreach (var record in result)
    {
        entities.Add(new SourceEntity(
            Id: record["id"].As<string>(),
            Label: record["label"].As<string>(),
            Type: record["type"].As<string>(),
            Summary: record["summary"].As<string>(),
            Source: record["source"].As<string>(),
            Username: record["username"].As<string>(),
            Embedding: ToFloatArray(record["embedding"]),
            CreatedAt: record["createdAt"].As<DateTimeOffset>().UtcDateTime,
            UpdatedAt: record["updatedAt"].As<DateTimeOffset>().UtcDateTime));
    }

    return entities;
}

static Dictionary<(string Username, string SourceId), string> BuildEntityIdMap(
    IReadOnlyCollection<SourceEntity> entities,
    bool prefixIdsWithUsername)
{
    var map = new Dictionary<(string Username, string SourceId), string>();
    foreach (var entity in entities)
    {
        var targetId = prefixIdsWithUsername
            ? $"{entity.Username.ToLowerInvariant()}__{entity.Id}"
            : entity.Id;

        map[(entity.Username, entity.Id)] = targetId;
    }

    return map;
}

static async Task<List<SourceRelationship>> LoadRelationshipsAsync(
    IDriver driver,
    string database,
    string? username,
    IReadOnlyDictionary<(string Username, string SourceId), string> entityIdMap)
{
    var query = """
        MATCH (a:Entity)-[r:RELATES_TO]->(b:Entity)
        WHERE $username IS NULL OR (a.username = $username AND b.username = $username)
        RETURN a.id AS fromId,
               a.username AS fromUsername,
               b.id AS toId,
               b.username AS toUsername,
               r.relationship AS relationship,
               r.context AS context,
               r.source AS source,
               r.timestamp AS timestamp,
               r.supersedes AS supersedes,
               r.embedding AS embedding
        ORDER BY r.timestamp, fromId, toId
        """;

    await using var session = driver.AsyncSession(config => config.WithDatabase(database));
    var result = await session.RunAsync(query, new { username });

    var relationships = new List<SourceRelationship>();
    await foreach (var record in result)
    {
        var fromUsername = record["fromUsername"].As<string>();
        var fromId = record["fromId"].As<string>();
        var toUsername = record["toUsername"].As<string>();
        var toId = record["toId"].As<string>();

        relationships.Add(new SourceRelationship(
            FromId: entityIdMap[(fromUsername, fromId)],
            ToId: entityIdMap[(toUsername, toId)],
            RelationshipType: record["relationship"].As<string>(),
            Context: record["context"].As<string?>(),
            Source: record["source"].As<string>(),
            Timestamp: record["timestamp"].As<DateTimeOffset>().UtcDateTime,
            Supersedes: record["supersedes"].As<string?>(),
            Embedding: ToFloatArray(record["embedding"])));
    }

    return relationships;
}

static async Task<List<SourceObservation>> LoadObservationsAsync(
    IDriver driver,
    string database,
    string? username,
    IReadOnlyDictionary<(string Username, string SourceId), string> entityIdMap)
{
    var query = """
        MATCH (o:Observation)
        WHERE $username IS NULL OR o.username = $username
        OPTIONAL MATCH (o)-[:OBSERVES]->(e:Entity)
        WHERE $username IS NULL OR e.username = $username
        RETURN o.id AS id,
               o.claim AS claim,
               o.conflictsWith AS conflictsWith,
               o.source AS source,
               o.timestamp AS timestamp,
               o.resolved AS resolved,
               o.resolution AS resolution,
               o.resolvedByMemoryId AS resolvedByMemoryId,
               o.username AS username,
               collect(DISTINCT { id: e.id, username: e.username }) AS observedEntities
        ORDER BY o.timestamp, o.id
        """;

    await using var session = driver.AsyncSession(config => config.WithDatabase(database));
    var result = await session.RunAsync(query, new { username });

    var observations = new List<SourceObservation>();
    await foreach (var record in result)
    {
        var observed = new List<string>();
        foreach (var item in record["observedEntities"].As<List<object>>())
        {
            if (item is not IDictionary<string, object> dict)
                continue;

            var observedId = dict.TryGetValue("id", out var idValue) ? idValue?.ToString() : null;
            var observedUsername = dict.TryGetValue("username", out var usernameValue) ? usernameValue?.ToString() : null;
            if (string.IsNullOrWhiteSpace(observedId) || string.IsNullOrWhiteSpace(observedUsername))
                continue;

            observed.Add(entityIdMap[(observedUsername!, observedId!)]);
        }

        var sourceUsername = record["username"].As<string>();
        var resolvedByMemoryId = record["resolvedByMemoryId"].As<string?>();
        if (!string.IsNullOrWhiteSpace(resolvedByMemoryId) &&
            entityIdMap.TryGetValue((sourceUsername, resolvedByMemoryId), out var mappedResolvedById))
        {
            resolvedByMemoryId = mappedResolvedById;
        }

        observations.Add(new SourceObservation(
            Id: record["id"].As<string>(),
            Claim: record["claim"].As<string>(),
            ConflictsWith: record["conflictsWith"].As<string>(),
            Source: record["source"].As<string>(),
            Timestamp: record["timestamp"].As<DateTimeOffset>().UtcDateTime,
            Resolved: record["resolved"].As<bool>(),
            Resolution: record["resolution"].As<string?>(),
            ResolvedByMemoryId: resolvedByMemoryId,
            ObservedEntityIds: observed));
    }

    return observations;
}

static async Task<TargetCounts> LoadTargetCountsAsync(IDriver driver, string database)
{
    await using var session = driver.AsyncSession(config => config.WithDatabase(database));

    var entities = await ScalarAsync(session, "MATCH (e:MemoryEntity) RETURN count(e) AS count");
    var relationships = await ScalarAsync(
        session,
        "MATCH (:MemoryEntity)-[r:RELATES_TO]->(:MemoryEntity) RETURN count(r) AS count");
    var observations = await ScalarAsync(session, "MATCH (o:MemoryObservation) RETURN count(o) AS count");

    return new TargetCounts(entities, relationships, observations);
}

static async Task<TargetOverlap> LoadTargetOverlapAsync(
    IDriver driver,
    string database,
    IReadOnlyList<string> entityIds,
    IReadOnlyList<string> observationIds)
{
    await using var session = driver.AsyncSession(config => config.WithDatabase(database));

    var overlappingEntities = await ScalarAsync(
        session,
        """
        UNWIND $ids AS id
        MATCH (e:MemoryEntity {id: id})
        RETURN count(e) AS count
        """,
        new { ids = entityIds });

    var overlappingObservations = await ScalarAsync(
        session,
        """
        UNWIND $ids AS id
        MATCH (o:MemoryObservation {id: id})
        RETURN count(o) AS count
        """,
        new { ids = observationIds });

    return new TargetOverlap(overlappingEntities, overlappingObservations);
}

static async Task UpsertEntitiesAsync(
    IDriver driver,
    string database,
    IReadOnlyList<SourceEntity> entities,
    IReadOnlyDictionary<(string Username, string SourceId), string> entityIdMap,
    int batchSize)
{
    const string query = """
        UNWIND $batch AS item
        MERGE (e:MemoryEntity {id: item.id})
        ON CREATE SET
            e.label = item.label,
            e.type = item.type,
            e.summary = item.summary,
            e.source = item.source,
            e.embedding = item.embedding,
            e.createdAt = datetime(item.createdAt),
            e.updatedAt = datetime(item.updatedAt)
        ON MATCH SET
            e.label = item.label,
            e.type = item.type,
            e.summary = item.summary,
            e.source = item.source,
            e.embedding = COALESCE(item.embedding, e.embedding),
            e.createdAt = coalesce(e.createdAt, datetime(item.createdAt)),
            e.updatedAt = datetime(item.updatedAt)
        """;

    await using var session = driver.AsyncSession(config => config.WithDatabase(database));
    foreach (var batch in Batch(entities, batchSize))
    {
        var payload = batch.Select(entity => new
        {
            id = entityIdMap[(entity.Username, entity.Id)],
            label = entity.Label,
            type = entity.Type,
            summary = entity.Summary,
            source = entity.Source,
            embedding = entity.Embedding,
            createdAt = entity.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            updatedAt = entity.UpdatedAt.ToString("O", CultureInfo.InvariantCulture)
        }).ToList();

        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { batch = payload }));
    }
}

static async Task UpsertRelationshipsAsync(
    IDriver driver,
    string database,
    IReadOnlyList<SourceRelationship> relationships,
    int batchSize)
{
    const string query = """
        UNWIND $batch AS item
        MATCH (a:MemoryEntity {id: item.fromId})
        MATCH (b:MemoryEntity {id: item.toId})
        MERGE (a)-[rel:RELATES_TO {migrationKey: item.migrationKey}]->(b)
        SET rel.relationship = item.relationship,
            rel.context = item.context,
            rel.source = item.source,
            rel.timestamp = datetime(item.timestamp),
            rel.supersedes = item.supersedes,
            rel.embedding = item.embedding
        """;

    await using var session = driver.AsyncSession(config => config.WithDatabase(database));
    foreach (var batch in Batch(relationships, batchSize))
    {
        var payload = batch.Select(relationship => new
        {
            fromId = relationship.FromId,
            toId = relationship.ToId,
            relationship = relationship.RelationshipType,
            context = relationship.Context,
            source = relationship.Source,
            timestamp = relationship.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            supersedes = relationship.Supersedes,
            embedding = relationship.Embedding,
            migrationKey = ComputeRelationshipMigrationKey(relationship)
        }).ToList();

        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { batch = payload }));
    }
}

static async Task UpsertObservationsAsync(
    IDriver driver,
    string database,
    IReadOnlyList<SourceObservation> observations,
    int batchSize)
{
    const string query = """
        UNWIND $batch AS item
        MERGE (o:MemoryObservation {id: item.id})
        SET o.claim = item.claim,
            o.conflictsWith = item.conflictsWith,
            o.source = item.source,
            o.timestamp = datetime(item.timestamp),
            o.resolved = item.resolved,
            o.resolution = item.resolution,
            o.resolvedByMemoryId = item.resolvedByMemoryId
        FOREACH (observedId IN item.observedEntityIds |
            FOREACH (_ IN CASE WHEN observedId IS NULL OR observedId = '' THEN [] ELSE [1] END |
                MERGE (e:MemoryEntity {id: observedId})
                MERGE (o)-[:OBSERVES]->(e)))
        """;

    await using var session = driver.AsyncSession(config => config.WithDatabase(database));
    foreach (var batch in Batch(observations, batchSize))
    {
        var payload = batch.Select(observation => new
        {
            id = observation.Id,
            claim = observation.Claim,
            conflictsWith = observation.ConflictsWith,
            source = observation.Source,
            timestamp = observation.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            resolved = observation.Resolved,
            resolution = observation.Resolution,
            resolvedByMemoryId = observation.ResolvedByMemoryId,
            observedEntityIds = observation.ObservedEntityIds
        }).ToList();

        await session.ExecuteWriteAsync(tx => tx.RunAsync(query, new { batch = payload }));
    }
}

static async Task<int> ScalarAsync(IAsyncSession session, string query, object? parameters = null)
{
    var result = await session.RunAsync(query, parameters ?? new { });
    var record = await result.SingleAsync();
    return record["count"].As<int>();
}

static IEnumerable<List<T>> Batch<T>(IReadOnlyList<T> items, int size)
{
    for (var index = 0; index < items.Count; index += size)
        yield return items.Skip(index).Take(size).ToList();
}

static float[]? ToFloatArray(object value)
{
    if (value is null)
        return null;

    if (value is float[] floats)
        return floats;

    if (value is IEnumerable<object> objects)
        return objects.Select(ConvertToFloat).ToArray();

    if (value is IEnumerable<float> floatEnumerable)
        return floatEnumerable.ToArray();

    if (value is IEnumerable<double> doubleEnumerable)
        return doubleEnumerable.Select(item => (float)item).ToArray();

    return null;
}

static float ConvertToFloat(object? value)
{
    return value switch
    {
        null => 0,
        float floatValue => floatValue,
        double doubleValue => (float)doubleValue,
        long longValue => longValue,
        int intValue => intValue,
        decimal decimalValue => (float)decimalValue,
        _ => Convert.ToSingle(value, CultureInfo.InvariantCulture)
    };
}

static string ComputeRelationshipMigrationKey(SourceRelationship relationship)
{
    var raw = string.Join("|", [
        relationship.FromId,
        relationship.ToId,
        relationship.RelationshipType,
        relationship.Context ?? "",
        relationship.Source,
        relationship.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        relationship.Supersedes ?? ""
    ]);

    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

sealed class Options
{
    public string SourceUri { get; private set; } = "";
    public string SourceUser { get; private set; } = "";
    public string SourcePassword { get; private set; } = "";
    public string SourceDatabase { get; private set; } = "neo4j";
    public string? SourceUsername { get; private set; }
    public string TargetUri { get; private set; } = "";
    public string TargetUser { get; private set; } = "";
    public string TargetPassword { get; private set; } = "";
    public string TargetDatabase { get; private set; } = "neo4j";
    public bool Apply { get; private set; }
    public bool PrefixIdsWithUsername { get; private set; }
    public bool AllowTargetOverlap { get; private set; }
    public bool ShowHelp { get; private set; }
    public int BatchSize { get; private set; } = 250;

    public static Options Parse(string[] args)
    {
        var options = new Options();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--source-uri":
                    options.SourceUri = ReadValue(args, ref index, arg);
                    break;
                case "--source-user":
                    options.SourceUser = ReadValue(args, ref index, arg);
                    break;
                case "--source-password":
                    options.SourcePassword = ReadValue(args, ref index, arg);
                    break;
                case "--source-database":
                    options.SourceDatabase = ReadValue(args, ref index, arg);
                    break;
                case "--source-username":
                    options.SourceUsername = ReadValue(args, ref index, arg);
                    break;
                case "--target-uri":
                    options.TargetUri = ReadValue(args, ref index, arg);
                    break;
                case "--target-user":
                    options.TargetUser = ReadValue(args, ref index, arg);
                    break;
                case "--target-password":
                    options.TargetPassword = ReadValue(args, ref index, arg);
                    break;
                case "--target-database":
                    options.TargetDatabase = ReadValue(args, ref index, arg);
                    break;
                case "--batch-size":
                    options.BatchSize = int.Parse(ReadValue(args, ref index, arg), CultureInfo.InvariantCulture);
                    break;
                case "--apply":
                    options.Apply = true;
                    break;
                case "--prefix-ids-with-username":
                    options.PrefixIdsWithUsername = true;
                    break;
                case "--allow-target-overlap":
                    options.AllowTargetOverlap = true;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        return options;
    }

    public IEnumerable<string> Validate()
    {
        if (string.IsNullOrWhiteSpace(SourceUri))
            yield return "--source-uri is required";
        if (string.IsNullOrWhiteSpace(SourceUser))
            yield return "--source-user is required";
        if (string.IsNullOrWhiteSpace(SourcePassword))
            yield return "--source-password is required";
        if (string.IsNullOrWhiteSpace(TargetUri))
            yield return "--target-uri is required";
        if (string.IsNullOrWhiteSpace(TargetUser))
            yield return "--target-user is required";
        if (string.IsNullOrWhiteSpace(TargetPassword))
            yield return "--target-password is required";
        if (BatchSize <= 0)
            yield return "--batch-size must be greater than 0";
    }

    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            Usage:
              dotnet run --project tools/memory-migration/CodeGraph.MemoryMigration.csproj -- \
                --source-uri bolt://localhost:17687 \
                --source-user neo4j \
                --source-password memorygraph \
                --source-username michael \
                --target-uri bolt://localhost:7687 \
                --target-user neo4j \
                --target-password codegraph \
                [--apply] [--prefix-ids-with-username] [--allow-target-overlap]

            Notes:
              - The tool is dry-run by default.
              - Use --apply to actually write migrated data into CodeGraph.
              - Use --prefix-ids-with-username when importing multi-user legacy stores.
              - The wrapper script at tools/memory-migration/run-memorygraph-migration.sh
                starts a temporary source Neo4j container from the old Docker volume.
            """);
    }

    private static string ReadValue(string[] args, ref int index, string arg)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for '{arg}'.");

        index++;
        return args[index];
    }
}

sealed record SourceEntity(
    string Id,
    string Label,
    string Type,
    string Summary,
    string Source,
    string Username,
    float[]? Embedding,
    DateTime CreatedAt,
    DateTime UpdatedAt);

sealed record SourceRelationship(
    string FromId,
    string ToId,
    string RelationshipType,
    string? Context,
    string Source,
    DateTime Timestamp,
    string? Supersedes,
    float[]? Embedding);

sealed record SourceObservation(
    string Id,
    string Claim,
    string ConflictsWith,
    string Source,
    DateTime Timestamp,
    bool Resolved,
    string? Resolution,
    string? ResolvedByMemoryId,
    IReadOnlyList<string> ObservedEntityIds);

sealed record TargetCounts(int Entities, int Relationships, int Observations);

sealed record TargetOverlap(int EntityIds, int ObservationIds);
