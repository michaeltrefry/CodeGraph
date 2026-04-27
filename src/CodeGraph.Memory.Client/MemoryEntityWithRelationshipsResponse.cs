using CodeGraph.Models.Memory;

namespace CodeGraph.Memory.Client;

public sealed record MemoryEntityWithRelationshipsResponse(
    MemoryEntity Entity,
    IReadOnlyList<MemoryRelationshipDetail> Relationships);
