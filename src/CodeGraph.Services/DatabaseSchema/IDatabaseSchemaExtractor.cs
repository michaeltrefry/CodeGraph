using CodeGraph.Data;

namespace CodeGraph.Services.DatabaseSchema;

public interface IDatabaseSchemaExtractor
{
    Task SyncAsync(DatabaseSourceEntity source, CancellationToken ct = default);
    Task SyncAllAsync(CancellationToken ct = default);
}
