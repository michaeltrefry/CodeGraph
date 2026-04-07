# Adversarial Review Status

Last updated: 2026-04-06

## Purpose

This document tracks what has been completed and what was discovered during the adversarial review and follow-up remediation passes.

It is meant to replace the growing chat history with one current status snapshot.

## Status Legend

- `Completed` — implemented and verified
- `Open` — discovered, not fixed yet
- `Reframed` — discovered issue, but the planned response changed after review

## Validation Snapshot

- Latest verification run: `dotnet test src/CodeGraph.Tests/CodeGraph.Tests.csproj`
- Result: 189 tests passing
- Current checked test/build path is clean after the warning-noise cleanup pass
- Overall adversarial-review remediation is complete; no `Open` items remain in the checked path
- Follow-up cleanup is also complete, including the last compatibility bridge and lower-signal doc/history cleanup

## Completed

### Security and runtime hardening

- `Completed` Admin surfaces are no longer effectively public.
  - API admin endpoints now require either a valid bearer token for an admin user or the shared admin API key.
  - Jobs endpoints now require the shared admin API key.

- `Completed` The orphaned policy-based admin auth stack was removed.
  - The dead `AdminRequirement` / `AdminAuthorizationHandler` path is gone.
  - API admin access continues to run through `AdminAccessFilter` plus JWT auth and admin-user lookup.

- `Completed` Missing jobs dependency wiring was fixed.
  - The jobs host now registers the file-system dependency needed by batch-analysis processing.

- `Completed` Settings path normalization was added.
  - `~/...` paths are normalized at startup instead of being passed through literally.

### Team-project to single-user conversion fixes

- `Completed` Core graph logic no longer hard-depends on `TC.*` naming.
  - Cross-repo package linking was generalized.
  - Gateway target resolution was generalized.
  - Stub-node creation now uses heuristics instead of `TC.*` assumptions.
  - `CARRIES_FIELD` extraction no longer depends on `TC.*`.

- `Completed` Visible product copy was cleaned up in key places.
  - Admin UI text, impact placeholders, README copy, MCP descriptions, API comments, and instruction docs were updated to reduce stale GitLab/Claude/`TC.*` drift.

- `Completed` Graph UI labels no longer rewrite repository names by stripping `TC.` and `Api`.
  - This was a real behavior bug for non-legacy repositories, not just a copy issue.

### Performance and operational fixes

- `Completed` Discovery no longer does one sync-state lookup per repository.
  - Sync-state loading is now batched.

- `Completed` Community detection no longer reruns fleet-wide after every indexed repository by default.
  - It is now controlled by configuration and defaults to off for indexing completion events.

- `Completed` `CODEGRAPH.md` generation can now optionally commit and push generated docs.
  - Commits are opt-in.
  - Pushes are opt-in.
  - Only generated `CODEGRAPH.md` files are staged by this path.

### Analyzer prompt cleanup

- `Completed` Phase 1 of the analyzer refactor is now done.
  - The stale hardcoded reseller/domain system prompt was removed.
  - A shared domain-agnostic prompt builder now drives direct analysis prompts.
  - Batch repo synthesis now uses the same shared prompt policy and includes cross-repo dependency context.
  - Added tests to lock out the old hardcoded business framing.

### Analyzer provider abstraction

- `Completed` Phase 2 provider abstraction is now in place for repository analysis.
  - Added a provider contract and provider registry.
  - Anthropic transport details now live behind `AnthropicAnalysisProvider` instead of inside the analyzer orchestration classes.
  - Added `OpenAiAnalysisProvider` as the second provider implementation on the shared seam.
  - Added `GeminiAnalysisProvider` as the third provider implementation on the shared seam.
  - Added `LocalAnalysisProvider` as the fourth provider implementation for LM Studio / Ollama-style OpenAI-compatible local backends.
  - `BatchAnalysisService` now calls the provider abstraction for submit/status/results/synthesis flow.
  - Batch request ordering is now stored explicitly so providers with ordered inline responses can map results safely.
  - Provider-specific default model settings now exist for OpenAI, Gemini, and local backends instead of inheriting the legacy top-level Anthropic default.
  - Non-batch providers now stay on the same batch/event workflow by replaying stored project requests one at a time during batch processing.
  - Existing config remains backward-compatible through legacy `AnalysisOptions` shims while adding a provider-oriented shape.

### Repository provider and admin workflow fixes

- `Completed` Folder-provider discovery now supports nested repositories.
  - Nested repositories are discovered recursively.
  - Group/path information is preserved.
  - Name-only resolution can find uniquely named nested repositories.

- `Completed` Admin discover filtering was fixed in the Angular UI.
  - The frontend now sends `namePattern`, which matches the API contract.

### Test coverage added during remediation

- `Completed` Added cross-repo linking coverage for non-`TC` package references.

- `Completed` Added coverage for batched sync-state lookup.

- `Completed` Added coverage for nested folder-provider discovery and resolution.

- `Completed` Added startup regression coverage for API admin auth and Jobs analysis-provider registration.

## Newly Discovered

### Analyzer architecture

- `Completed` The orphaned `ICodeAnalyzer` / `ClaudeCodeAnalyzer` path was removed.
  - The active repository-analysis path now runs through `IBatchAnalysisService` plus provider abstractions.
  - The old direct analyzer seam is no longer registered or shipped as dead code.

### Intentional provider-specific naming

- `Completed` Some implementation names are intentionally provider-specific.
  - `GitLabRepoProvider`
  - provider-specific config sections such as `RepositorySource:GitLab`

- `Completed` These names were kept because GitLab, GitHub, and local-folder repository sources are all first-class supported modes, and provider-specific naming is accurate here.

## Follow-up Tracking

### Completed architecture follow-up

- `Completed` Refactor the analyzer layer into a multi-provider LLM architecture.
  - Detailed design plan: `plans/multi-provider-llm-analysis-plan.md`
  - Phase 1 prompt cleanup is complete.
  - Phase 2 provider abstraction is complete for Anthropic, OpenAI, Gemini, and local backends.
  - Batch-shaped orchestration is the long-term front door for repository analysis.
  - Native-batch providers use provider batch APIs.
  - Non-batch providers stay on the same event flow via direct request replay during batch processing.

- `Completed` Delete placeholder `Scopes.cs` files.
  - The API and Jobs tombstones were removed.

- `Completed` Remove unused logger injections and other small warning/noise sources.
  - Removed the dead logger injections touched by the checked build path.
  - Fixed the nullable warning sources in `SecurityAnalyzerTests`.

### Completed cleanup

- `Completed` The long-term repository-analysis path is batch-shaped orchestration.
  - There is no longer a planned separate public direct-analysis path.
  - Provider `ExecuteAsync` remains an internal execution primitive and fallback for non-batch providers.

- `Completed` Audit remaining TC/GitLab-specific examples in tests and comments.
  - Generic runtime/test wording has been cleaned up where it was just stale naming noise.
  - The checked-path cross-repo linker no longer carries `TC.*` gateway/package compatibility logic.
  - Remaining `TC.*` references are now limited to older historical notes or unrelated legacy-shaped test fixtures outside the linker path.

- `Completed` Default auth/OIDC settings are neutral.
  - The checked-in auth defaults no longer point at TC-specific identity endpoints.
  - Example config now starts blank/provider-neutral instead of encoding an old private deployment.

### Completed final cleanup

- `Completed` Remove compatibility-bridge naming that no longer carried migration value.
  - New `AnalysisBatch` records now persist only `providerBatchId`.
  - Added a Neo4j migration to backfill legacy `anthropicBatchId` values into `providerBatchId` and remove the old property.

- `Completed` Finish doc cleanup in lower-signal areas.
  - Active plan docs with stale paths/provider wording were updated to current naming.
  - Historical plans that still contain old identifiers are now explicitly labeled as pre-restructure snapshots so they do not read as current architecture guidance.

## Decision Notes

### Why the old direct analyzer path was removed

The original adversarial read was correct that the runtime no longer depended on the direct analyzer path.

Once the provider abstraction existed and the live batch path was converted to use it, keeping a separate unused `ICodeAnalyzer` / `ClaudeCodeAnalyzer` stack no longer added architectural value. Removing it reduced drift and made the provider-centered design explicit.

## Summary

The highest-risk issues from the adversarial review have already been addressed:

- auth exposure
- jobs DI/runtime gaps
- hard `TC.*` conversion bugs
- discovery performance
- runaway community detection
- missing optional doc publish path
- nested folder-provider support
- several important UI/API and documentation drift issues

No tracked cleanup items remain in this document.
