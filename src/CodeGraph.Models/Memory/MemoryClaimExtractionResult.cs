using System.Text.Json.Serialization;

namespace CodeGraph.Models.Memory;

public class MemoryClaimExtractionResult
{
    [JsonPropertyName("entities")]
    public List<MemoryExtractedEntity> Entities { get; set; } = [];

    [JsonPropertyName("claims")]
    public List<MemoryExtractedClaim> Claims { get; set; } = [];

    [JsonPropertyName("evidence")]
    public List<MemoryExtractedEvidence> Evidence { get; set; } = [];
}

public class MemoryExtractedEntity
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("label")]
    public required string Label { get; set; }

    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("canonicalName")]
    public string? CanonicalName { get; set; }

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = [];

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

public class MemoryExtractedClaim
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("subject")]
    public required string Subject { get; set; }

    [JsonPropertyName("predicate")]
    public required string Predicate { get; set; }

    [JsonPropertyName("object")]
    public string? Object { get; set; }

    [JsonPropertyName("valueText")]
    public string? ValueText { get; set; }

    [JsonPropertyName("valueJson")]
    public string? ValueJson { get; set; }

    [JsonPropertyName("normalizedText")]
    public string? NormalizedText { get; set; }

    [JsonPropertyName("confidence")]
    public decimal? Confidence { get; set; }

    [JsonPropertyName("effectiveAt")]
    public string? EffectiveAt { get; set; }

    [JsonPropertyName("recordedAt")]
    public string? RecordedAt { get; set; }

    [JsonPropertyName("supersedes")]
    public string? Supersedes { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

public class MemoryExtractedEvidence
{
    [JsonPropertyName("claimId")]
    public string? ClaimId { get; set; }

    [JsonPropertyName("observationId")]
    public string? ObservationId { get; set; }

    [JsonPropertyName("evidenceType")]
    public required string EvidenceType { get; set; }

    [JsonPropertyName("sourceRef")]
    public required string SourceRef { get; set; }

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }

    [JsonPropertyName("metadataJson")]
    public string? MetadataJson { get; set; }
}
