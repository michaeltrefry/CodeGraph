using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CodeGraph.Services.Assistant;

public class MemoryMcpEntityInput
{
    [JsonPropertyName("id")]
    [Description("Required stable entity identifier.")]
    public string? Id { get; set; }

    [JsonPropertyName("label")]
    [Description("Required human-readable entity label.")]
    public string? Label { get; set; }

    [JsonPropertyName("type")]
    [Description("Required entity type, such as person, project, or concept.")]
    public string? Type { get; set; }

    [JsonPropertyName("externalId")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("canonicalName")]
    public string? CanonicalName { get; set; }

    [JsonPropertyName("aliases")]
    public List<string?>? Aliases { get; set; }

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}

public class MemoryMcpClaimInput
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("subject")]
    [Description("Required entity id for the claim subject.")]
    public string? Subject { get; set; }

    [JsonPropertyName("predicate")]
    [Description("Required predicate describing the claim relationship or fact.")]
    public string? Predicate { get; set; }

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

public class MemoryMcpEvidenceInput
{
    [JsonPropertyName("claimId")]
    public string? ClaimId { get; set; }

    [JsonPropertyName("observationId")]
    public string? ObservationId { get; set; }

    [JsonPropertyName("evidenceType")]
    [Description("Required evidence type, such as conversation or document.")]
    public string? EvidenceType { get; set; }

    [JsonPropertyName("sourceRef")]
    [Description("Required reference identifying the evidence source.")]
    public string? SourceRef { get; set; }

    [JsonPropertyName("snippet")]
    public string? Snippet { get; set; }

    [JsonPropertyName("metadataJson")]
    public string? MetadataJson { get; set; }
}
