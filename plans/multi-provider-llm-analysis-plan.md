# Multi-Provider LLM Analysis Plan

Last updated: 2026-04-06

## Goal

Refactor CodeGraph's analysis layer so repository analysis can run against any supported LLM provider, including:

- Anthropic / Claude
- OpenAI / Codex-family models
- Google / Gemini
- local models exposed through an OpenAI-compatible or custom endpoint

The design should preserve the current analysis outputs:

- per-project summaries
- repo-level summaries
- node descriptions
- confidence values
- generated `CODEGRAPH.md`

while removing provider lock-in and removing hardcoded business-domain assumptions.

## Status

This plan is implemented in the current codebase.

Repository analysis now supports Anthropic, OpenAI, Gemini, and local OpenAI-compatible backends through a shared provider abstraction, and the batch-shaped workflow is the long-term orchestration front door with direct replay as an internal fallback for non-batch providers.

The historical sections below intentionally capture the pre-refactor starting state that this plan replaced.

## Non-Negotiable Rule

Analysis must be domain-agnostic by default.

The model should infer project purpose from:

- graph structure
- source code
- project naming
- conventions wiki
- repo-local docs such as `README.md`
- configuration and dependency context

It must not assume the codebase belongs to any specific business vertical such as domain resale, games, finance, embedded systems, or anything else unless the repository evidence actually supports that conclusion.

This means:

- no hardcoded reseller/domain-auction prompt framing
- no hardcoded company/product names in the system prompt
- no provider implementation should embed product-specific context

## Original Starting State

Today there are two overlapping analysis paths:

1. `IBatchAnalysisService`
   - this is the active path in the current runtime
   - it submits batch requests, processes results, synthesizes repo summaries, and writes `CODEGRAPH.md`

2. `ICodeAnalyzer` / `ClaudeCodeAnalyzer`
   - this appears to be an older direct-analysis path
   - it is still registered in DI
   - it is Anthropic-specific
   - it still contains stale business-domain prompt content
   - it currently looks like a better architectural seam than a deletion target

## Problems This Plan Was Solving

### 1. Provider lock-in

Current analysis behavior is tied directly to Anthropic request/response formats, endpoints, retry rules, and model naming.

### 2. Prompt lock-in

Prompt construction is mixed into provider-specific code, and one path still contains hardcoded domain reseller framing.

### 3. Transport lock-in

Batch APIs, synchronous message APIs, and local model APIs all have different capabilities. The current implementation does not model those differences cleanly.

### 4. Orchestration ambiguity

It is not yet clear whether direct analysis, batch analysis, and incremental change analysis should be separate services or multiple execution modes of one orchestration layer.

### 5. Configuration bias

`AnalysisOptions` is currently Anthropic-shaped. That makes it hard to express provider-specific settings without growing one giant option bag.

## Design Principles

### 1. Separate orchestration from provider execution

CodeGraph should decide:

- what to analyze
- how to group inputs
- when to batch
- when to synthesize
- when to write docs

Providers should decide:

- how prompts are packaged
- how requests are sent
- how retries/backoff are handled
- how responses are parsed

### 2. Separate prompts from transports

Prompt-building should be independent of Anthropic, OpenAI, Gemini, or local runtime formats.

### 3. Model capabilities must be explicit

Some providers support:

- batch inference
- structured JSON output
- tool use
- very large context windows
- streaming

Some do not.

The orchestration layer should branch based on declared provider capabilities, not on concrete provider type checks scattered through the code.

### 4. Keep outputs stable

The rest of the codebase should continue to work with stable internal result models such as:

- `ProjectAnalysis`
- `RepoAnalysis`
- `ProjectAnalysisResult`
- `RepoAnalysisResult`

### 5. Default to evidence-based analysis

Prompts should instruct the model to:

- describe what the code appears to do
- distinguish code evidence from inference
- state uncertainty clearly
- avoid inventing business context

## Proposed Architecture

## Layer 1: Orchestration

Introduce one orchestration-facing service for repository analysis.

Suggested interface:

```csharp
public interface IRepositoryAnalysisOrchestrator
{
    Task<RepoAnalysis> AnalyzeRepositoryAsync(
        string repoName,
        string rootPath,
        AnalysisExecutionOptions? options = null,
        CancellationToken ct = default);

    Task<ProjectAnalysis> AnalyzeProjectAsync(
        string repoName,
        string projectName,
        string projectPath,
        AnalysisExecutionOptions? options = null,
        CancellationToken ct = default);

    Task<AnalysisUpdate?> AnalyzeChangesAsync(
        string repoName,
        string rootPath,
        string diff,
        string commitMessage,
        string existingSummary,
        AnalysisExecutionOptions? options = null,
        CancellationToken ct = default);
}
```

This layer owns:

- graph/source gathering
- project grouping
- prompt intent selection
- choosing direct vs batch execution
- repo-level synthesis flow

This layer should be the spiritual successor to `ICodeAnalyzer`.

## Layer 2: Prompt Building

Introduce a provider-neutral prompt builder layer.

Suggested interfaces:

```csharp
public interface IAnalysisPromptBuilder
{
    AnalysisPrompt BuildProjectPrompt(ProjectAnalysisInput input);
    AnalysisPrompt BuildRepoSynthesisPrompt(RepoSynthesisInput input);
    AnalysisPrompt BuildChangeAnalysisPrompt(ChangeAnalysisInput input);
}

public sealed record AnalysisPrompt(
    string SystemPrompt,
    string UserPrompt,
    string OutputSchemaName,
    string OutputSchemaJson);
```

This layer owns:

- domain-agnostic system prompt
- evidence-based analysis instructions
- schema description
- reusable wording across providers

This layer must not know anything about Anthropic message objects, OpenAI chat payloads, Gemini payloads, or local HTTP clients.

## Layer 3: Provider Capability Model

Introduce a capability contract so orchestration can choose execution mode cleanly.

Suggested interfaces:

```csharp
public interface IAnalysisModelProvider
{
    string ProviderName { get; }
    AnalysisProviderCapabilities Capabilities { get; }

    Task<StructuredAnalysisResponse> ExecuteAsync(
        AnalysisPrompt prompt,
        AnalysisModelRequest request,
        CancellationToken ct = default);

    Task<BatchSubmissionResult> SubmitBatchAsync(
        IReadOnlyList<BatchAnalysisItem> items,
        AnalysisModelRequest request,
        CancellationToken ct = default);

    Task<IReadOnlyList<BatchItemResult>> GetBatchResultsAsync(
        string providerBatchId,
        CancellationToken ct = default);
}
```

```csharp
public sealed record AnalysisProviderCapabilities(
    bool SupportsBatch,
    bool SupportsStructuredJson,
    bool SupportsStreaming,
    bool SupportsLargeContext,
    int? MaxContextTokens);
```

Not every provider must implement every mode.

Rules:

- if `SupportsBatch` is false, batch methods can throw a provider capability exception
- orchestration must fall back to direct per-project execution when batch is unavailable

## Layer 4: Provider Implementations

Concrete implementations should be small and specific:

- `AnthropicAnalysisProvider`
- `OpenAiAnalysisProvider`
- `GeminiAnalysisProvider`
- `LocalAnalysisProvider`

These own:

- request formatting
- auth headers
- endpoint URLs
- response parsing
- provider-specific retry/backoff

They should not own:

- project discovery
- graph gathering
- repo synthesis decisions
- doc generation

## Layer 5: Batch Coordination

The current `IBatchAnalysisService` likely remains useful, but its role should narrow.

Recommended end state:

- `IBatchAnalysisService` becomes orchestration around batch-capable providers
- it should call a provider abstraction, not Anthropic APIs directly
- native batch and replayed batch execution should share prompt builders and result parsing shapes

This preserves the current event-driven batch workflow without making Anthropic the permanent center of the design.

## Proposed Refactor Target Map

### Keep conceptually

- `ICodeAnalyzer` as the high-level orchestration slot, but rename if needed
- `IBatchAnalysisService`
- shared analysis result models
- `CodeGraphDocGenerator`

### Extract

- provider-neutral prompt building from `ClaudeCodeAnalyzer`
- provider-neutral analysis request options from `AnalysisOptions`
- provider-neutral synthesis logic from `BatchAnalysisService`

### Replace

- `ClaudeCodeAnalyzer` with `AnthropicAnalysisProvider` plus orchestration service
- `AnthropicCircuitBreaker` with a provider-specific resilience component under the Anthropic provider

### Add

- provider registry / provider resolver
- execution options model
- capability model
- provider-specific options classes

## Configuration Design

Current `AnalysisOptions` is too Anthropic-specific for the target design.

Recommended structure:

```csharp
public class AnalysisOptions
{
    public string DefaultProvider { get; set; } = "anthropic";
    public string DefaultModel { get; set; } = "";
    public int MaxTokensPerAnalysis { get; set; } = 128_000;
    public int MaxTokensPerSynthesis { get; set; } = 128_000;
    public int MaxParallelAnalyses { get; set; } = 5;
    public int MaxSourceChars { get; set; } = 128_000;
    public bool AutoCommitDocs { get; set; }
    public bool AutoPushDocs { get; set; }
    public string AutoCommitMessage { get; set; } = "docs(codegraph): update CODEGRAPH.md";
    public AnthropicProviderOptions Anthropic { get; set; } = new();
    public OpenAiProviderOptions OpenAi { get; set; } = new();
    public GeminiProviderOptions Gemini { get; set; } = new();
    public LocalProviderOptions Local { get; set; } = new();
}
```

Notes:

- keep generic knobs at the top level
- move provider-specific endpoints, API keys, and versions under provider sections
- allow local models to use either OpenAI-compatible endpoints or custom adapters

## Prompt Policy

All providers should share the same analysis intent and prompt policy.

Suggested system-prompt rules:

- describe the repository based only on the evidence provided
- do not assume a business domain unless the code strongly indicates one
- separate observed facts from inference
- prefer concrete architectural language over vague marketing language
- explicitly note uncertainty when intent is unclear
- return structured JSON matching the requested schema

Suggested wording:

```text
You are analyzing source code and repository structure.

Describe what the code appears to do based on the provided graph, source snippets,
configuration, and project structure. Do not assume any specific business domain
unless the evidence supports it. If the repository could belong to many domains,
describe the technical responsibilities rather than inventing a business story.

When making inferences, stay close to the evidence and indicate uncertainty clearly.
Prefer precise, technical descriptions over generic summaries.
```

## Execution Model

The architecture should use one batch-shaped orchestration model for repository analysis.

Provider execution can happen in two ways behind that single front door:

### 1. Native batch execution

Use when:

- provider supports batch
- asynchronous event-driven flow is acceptable

### 2. Direct replay inside batch orchestration

Use when:

- provider does not support native batch
- the repository should still move through the same batch/event workflow

### 3. Incremental change analysis

Use when:

- repo already has stored analysis
- only a diff/commit needs reinterpretation

The key rule is that repository analysis should share:

- prompt-building inputs
- response schema expectations
- internal result models

## Suggested Interfaces

### Provider resolver

```csharp
public interface IAnalysisProviderRegistry
{
    IAnalysisModelProvider GetProvider(string? providerName = null);
}
```

### Execution options

```csharp
public sealed record AnalysisExecutionOptions(
    string? Provider = null,
    string? Model = null,
    bool IncludeAllSource = false);
```

### Structured response

```csharp
public sealed record StructuredAnalysisResponse(
    string RawText,
    string ModelUsed,
    string ProviderName,
    JsonDocument ParsedJson);
```

## Migration Plan

### Phase 1: Prompt cleanup

- remove the hardcoded domain reseller prompt from `ClaudeCodeAnalyzer`
- introduce a shared domain-agnostic prompt builder
- keep Anthropic as the only provider for now

Outcome:

- immediate risk reduction
- no more incorrect hardcoded business framing

Status:

- Completed on 2026-04-06
- Shared prompt policy now covers direct analysis prompts and batch repo synthesis
- Follow-on work should start at Phase 2

### Phase 2: Provider abstraction

- add `IAnalysisModelProvider`
- implement `AnthropicAnalysisProvider`
- move Anthropic HTTP/message logic out of orchestration
- move `AnthropicCircuitBreaker` under the Anthropic provider

Outcome:

- Anthropic remains functional
- architecture becomes extensible

Status:

- Completed on 2026-04-06 for the repository-analysis path
- Direct analyzer and batch analyzer now depend on the provider abstraction
- Current config remains backward-compatible while exposing a provider-oriented shape
- Anthropic, OpenAI, Gemini, and local OpenAI-compatible providers are all implemented on the shared seam
- Batch result correlation now supports ordered provider outputs by carrying request order through the shared seam
- Provider-specific default models now exist for OpenAI, Gemini, and local backends

### Phase 3: Orchestration unification

- make `IBatchAnalysisService` depend on provider abstractions
- make provider-native batch and direct replay share prompt builders and response contracts

Outcome:

- no duplicate analyzer logic split across legacy and batch paths
- one batch-shaped front door regardless of provider capabilities

Status:

- Completed in principle on 2026-04-06 for repository analysis because `IBatchAnalysisService` now depends on the provider abstraction and non-batch providers can replay stored requests one at a time through the same batch/event workflow
- Remaining work is mostly naming cleanup rather than architecture selection

### Phase 4: Additional providers

- add `OpenAiAnalysisProvider`
- add `GeminiAnalysisProvider`
- add `LocalAnalysisProvider`

Outcome:

- multi-provider support becomes real, not just theoretical

Status:

- Completed on 2026-04-06 with `OpenAiAnalysisProvider`, `GeminiAnalysisProvider`, and `LocalAnalysisProvider`
- Non-batch local providers now run through batch-shaped orchestration via direct request replay

### Phase 5: Provider selection and policy

- add config for default provider/model
- allow per-run overrides for provider/model
- decide whether some providers should be limited to native batch vs replayed batch execution

## Testing Strategy

### Unit tests

- prompt builder emits domain-agnostic system prompt
- prompt builder does not contain hardcoded company/product names
- provider registry resolves the correct provider
- orchestration chooses native batch vs direct replay based on capability
- structured response parsing handles fenced and unfenced JSON

### Contract tests

- each provider implementation maps the shared `AnalysisPrompt` to its own API correctly
- each provider converts responses back into shared structured JSON consistently

### Integration tests

- repository analysis works end-to-end with Anthropic provider
- batch synthesis still produces repo summaries and `CODEGRAPH.md`
- replayed non-batch execution produces equivalent internal result shapes

### Regression guard

Add a test that fails if the shared system prompt contains legacy vertical-specific wording such as:

- reseller
- auctioneer
- HugeDomains
- DropCatch
- NameBright

That makes the domain-neutral rule enforceable instead of aspirational.

## Recommended File Layout

Suggested end-state layout:

```text
src/CodeGraph.Services/Analyzers/
  IRepositoryAnalysisOrchestrator.cs
  RepositoryAnalysisOrchestrator.cs
  IBatchAnalysisService.cs
  BatchAnalysisService.cs
  Prompts/
    IAnalysisPromptBuilder.cs
    AnalysisPromptBuilder.cs
    AnalysisPromptInputs.cs
  Providers/
    IAnalysisModelProvider.cs
    IAnalysisProviderRegistry.cs
    AnalysisProviderCapabilities.cs
    Anthropic/
      AnthropicAnalysisProvider.cs
      AnthropicProviderOptions.cs
      AnthropicResiliencePolicy.cs
    OpenAi/
      OpenAiAnalysisProvider.cs
      OpenAiProviderOptions.cs
    Gemini/
      GeminiAnalysisProvider.cs
      GeminiProviderOptions.cs
    Local/
      LocalAnalysisProvider.cs
      LocalProviderOptions.cs
```

## Recommended Execution Order

1. Remove the hardcoded domain reseller prompt
2. Introduce provider-neutral prompt builders
3. Add provider abstraction with Anthropic as the first concrete implementation
4. Rewire batch analysis to use the provider abstraction
5. Add OpenAI, Gemini, and local providers

## Success Criteria

This plan is complete when:

- no analysis prompt contains hardcoded business-domain assumptions
- Anthropic-specific logic no longer defines the architecture
- native batch and replayed batch execution share the same conceptual model
- provider selection is configuration-driven
- at least one second provider can be added without rewriting orchestration code
- repo summaries remain evidence-based and technically useful across wildly different project types

## Bottom Line

The old `ICodeAnalyzer` / `ClaudeCodeAnalyzer` seam is no longer the front door.

The first move is not "support every model immediately."

The first move is:

- make prompts domain-agnostic
- keep `IBatchAnalysisService` as the repository-analysis front door
- separate orchestration from provider calls
- make Anthropic one provider instead of the architecture itself

Once that is done, supporting Claude, Codex/OpenAI, Gemini, and local models becomes incremental engineering instead of a rewrite.
