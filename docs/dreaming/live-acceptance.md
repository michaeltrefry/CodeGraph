# S8 live acceptance

This is the epic's separately dispatched model run. Normal tests skip `DreamingLiveAcceptance`; the live test also refuses normal CI. Do not set its opt-in variable for an ordinary test run. A successful build or skipped test is not live acceptance.

The command uses a persistent **local native MariaDB acceptance environment**, not production. It creates or reuses a real `CODEGRAPH` work project linked to the actual committed CodeGraph checkout. It imports two source-derived facts: the default storage provider from its owning source, and a literal low-confidence claim from the generated root CODEGRAPH.md about CodeGraph.Cg. It records the source commit, file hashes and import provenance. It creates no fictitious sessions, correction events, review comments or graph nodes. The configured model chooses whether and how to curate these facts; the harness does not script its answers or guarantee a write/proposal.

Use a stationary, clean, integrated candidate containing S2–S5, these instructions and the live harness. The local provider must already be configured to serve a tool-capable model. The observed local route for the original S8 preparation was LM Studio at `http://127.0.0.1:1234/v1`, model `google/gemma-4-31b`; the old `oldlocal` provider alias is normalized to `lmstudio`. Recheck availability before running. The unavailable remote worker and unauthenticated production endpoints are not required by this local route.

From that checkout, explicitly dispatch:

```sh
CODEGRAPH_DREAMING_LIVE=1 \
CODEGRAPH_DREAMING_LIVE_CONNECTION='Server=127.0.0.1;Port=3307;User ID=root;Password=;Database=cg_dreaming_live;' \
CODEGRAPH_DREAMING_LIVE_SOURCE_ROOT="$PWD" \
CODEGRAPH_DREAMING_LIVE_PROVIDER=lmstudio \
CODEGRAPH_DREAMING_LIVE_MODEL='google/gemma-4-31b' \
CODEGRAPH_DREAMING_LIVE_BASE_URL='http://127.0.0.1:1234/v1' \
CODEGRAPH_DREAMING_LIVE_ARTIFACTS='/tmp/codegraph-dreaming-live-evidence' \
dotnet test src/CodeGraph.Tests/CodeGraph.Tests.csproj \
  --filter FullyQualifiedName~DreamingLiveAcceptance.CodeGraph_live_provider_report_and_revert \
  --logger 'console;verbosity=normal'
```

The command temporarily enables dreaming with 12 turns and 160,000 total provider-reported tokens, no minimum interval, and zero per-token local provider charge. Hardware/electricity costs are not estimated. Settings are restored in `finally`. This budget is explicit and bounded; it is not a performance claim. A 20-minute run cancellation and HTTP timeout also apply. The database and evidence directory persist for inspection and continuation.

The test calls the actual production `GraphAssistant` batch provider implementation, source reader, policy service, journal and MariaDB stores. Telemetry is captured locally in the evidence directory. It always attempts to revert a returned run before evaluating acceptance, including a failed or budget-exhausted result. A crash before the run returns may require manual reversion of the persisted run from the Dreaming view or existing revert endpoint. Never rerun merely to hide a failed attempt; retain and explain every report.

Each timestamped evidence directory contains:

- `environment.json`: project, source commit, instruction revisions, provider route and provenance.
- `settings.json`, `usage.json`, `run.json`: exact budget, actual token usage, all calls, mutations and proposals.
- `before.json`, `after-run.json`, `after-revert.json`: canonical state snapshots.
- `revert.json`, `restoration.json`: inverse actions, conflicts, semantic comparison and retained proposals.
- `acceptance.json`: written only after all checks pass, including completed status within budget, at least one actual direct write and one proposal, evidence and prevalence on both, zero tool refusals, and semantic restoration.

Semantic restoration includes all preexisting entry fields, source/scope, freshness baselines, lesson events, links, challenges, addressed-by state and operational work state. Entry revision/version/update timestamp and append-only history advance by design. New entries created by a run revert to retracted tombstones, which are retained and do not serve. Non-binding dream proposals are outside direct-write reversion and remain visible; `restoration.json` lists them. Physical row equality is therefore not asserted.

Review the actual action rationale, evidence support and observed prevalence as well as the machine checks. Neither an arbitrary successful provider response nor fabricated recurrence counts satisfy S8. If the provider returns only reads, an error, a refusal or an unsupported change, retain the report and treat S8 as incomplete. Tune the versioned instructions based on the observed failure and coordinate any additional model run with the epic owner.
