# Agent telemetry integration

`SolusAgent.Telemetry` converts public `SolusAgent.Api` usage into the [neutral telemetry v1 contract](telemetry-contract.md). It references only Agent.Api and Contracts. Existing Agent libraries do not reference the mapper, and the mapper has no runtime, Logging, Supabase, network, GitHub environment or product dependency. It implements the mapper outcome of the [L1 plan](../90_roadmap/logging-l1-plan.md); the [candidate consumption track](../10_workflow/logging-package-consumption.md) separately exercises installed packages through the real Action/CLI to controlled storage. Published access and hosted qualification remain separate outcomes.

## Public conversion and current membership

Call `AgentTelemetryMapper.MapRun(AgentRunUsage)` with an immutable public snapshot or `MapRun(AgentOutcome)` with its terminal associated usage. A known execution with a null `Outcome.Usage` maps to unavailable inventory and null token/count facts. An outcome's termination reason does not prove zero usage, complete measurement or product acceptance. Agent.Api validates the outcome/usage execution identity before mapping.

`MapUsage(membershipCoverage, currentOutcomes)` accepts exactly the caller-selected current collection. It copies the collection, rejects null or duplicate execution identities and enforces the neutral 256-run bound. The caller supplies whole-invocation membership coverage independently of each run's attempt inventory and per-axis measurements. Complete empty membership means no current Agent runs; unavailable empty membership is unknown. Partial membership does not establish whole-invocation totals. No invocation aggregate is synthesized.

The product constructs `TelemetryRecord` with that usage, its own terminal `TelemetryOutcome`, safe execution labels and owned extensions. Product success, partial acceptance and publication status remain product decisions even when an Agent completed or failed. Assign `eventId` once to the physical invocation; retain the admitted record/JSON for delivery retries. Do not reexecute the Agent or regenerate identity to retry delivery.

Historical campaigns and previous context rounds never enter `currentOutcomes`. For a restored execution, use `ContextExecutionResult.Outcome`, whose usage is current-round work, and leave `History` outside the usage collection. `ContinuedCalls` contains predecessor seeds: its ordinal can precede the first new attempt, but neither the seed nor the old physical attempt contributes new measurements or counts. A rejected context has no work outcome; an explicitly complete current collection can therefore be empty without inventing an Agent run.

## Measurements and provider facts

Core input/output copy `AgentRunUsage.InputTokens` and `OutputTokens` directly. Their independent complete/partial/unavailable/overflow states retain exact Int64 values or null. Complete empty attempt inventory proves zero. Proven no-dispatch contributes zero under the Agent aggregate's own semantics; dispatched missing observations remain unknown. Partial zero remains a subtotal. Known aggregation overflow stays null/overflow even when other observations are missing.

Logical calls count distinct logical identities represented by current attempts, including continued calls whose first current ordinal exceeds one. Physical attempts count all supplied current entries, including retry/failure work. Partial inventory counts describe only its observed entries; unavailable inventory has null counts. Tool admissions, invocation-interface entries, reserved-unstarted and released-unstarted counts remain independently nullable. Invocation entry does not establish a successful effect, and no general equality between tool counts is imposed.

Provider details group by both kind and declared relationship. Reported-attempt count includes unknown values; measured-attempt count includes known zero. Checked nonnegative sums overflow explicitly. A group is complete only with complete attempt inventory and a measured report from every physical attempt; a known subtotal with omitted/unknown peers is partial. A group with no known values is unavailable. Different relationships stay separate; overlapping kinds are never added to core input/output. No uncached partition is reconstructed by subtraction, and no reasoning subset is invented from its kind alone.

## Separate accounting knowledge

Accounting is independent of measured token coverage. The mapper never calculates product allowances, prices, currency exchange, policy charges or invoices.

| Source | Mapping |
| --- | --- |
| Provider claims | Supplied in-flight reservations and conservative charges sum separately with checked arithmetic. Completely absent amount groups remain absent. A fully reported group over complete inventory is provisional; sparse reports or partial inventory retain an unknown subtotal, including explicit zero. Overflow is null/overflow independently per axis. |
| Provider settlement | No claims yields null. Two or more distinct explicitly reported settlement classes yield mixed. Homogeneous explicit claims with missing claims or partial inventory yield unknown; otherwise their explicit settled/unsettled/unknown class is retained. A missing claim is never invented as an explicit unknown report. |
| Provider estimates | Group by currency without conversion. A fully reported complete-inventory group is provisional; missing same-currency estimates or partial inventory yield an unknown subtotal. Sum decimal coefficients exactly; values that cannot fit the supported decimal representation, including precision loss, become null/overflow rather than rounded amounts. |
| Run ledger | Only the public validated complete finalized snapshot supplies this authority. Copy its exclusive measured, reserved, conservative-charge and unresolved bucket sums separately. Empty buckets and released amounts are known zero within that ledger; a nonempty unresolved bucket remains unknown even with a zero estimate. Estimated buckets are provisional; checked sum overflow remains null/overflow. |
| Allowance/remainder | Copy the ledger's optional allowances and signed remaining allowance. Absent allowance is unavailable. Remaining knowledge follows the public balance coverage; overflow does not become a saturated or zero remainder. |

Provider claims and authoritative run-ledger facts keep different `source` classifications even when they disagree. They are not added together. Ledger measured-bucket zero describes the bucket, not proof of zero whole-run consumption. Neither charges nor estimates upgrade input/output measurement coverage. Attached run accounting is finalized by the Agent contract; a still-reserved nonfinal accounting fixture cannot be attached as valid terminal run usage.

## Callable product-shaped examples

[ProductTelemetryExamples](../../tests/SolusAgent.ContractTests/Telemetry/ProductTelemetryExamples.cs) contains callable `ProduceScribe` and `ProduceApr` functions accepting public Agent values, caller-supplied membership, product outcome and JSON extensions. Both call the mapper, compose the same public record, add caller attributes through `WithCallerAttributes` and use `TelemetryJson.TrySerialize`. They do not read old product outputs, run an Agent, upload or write Action environment files.

[ProductCompositionTests](../../tests/SolusAgent.ContractTests/Telemetry/ProductCompositionTests.cs) execute an independent strict JSON consumer. The Scribe-shaped producer includes two current runs with independent coverage, an exact value above JavaScript's safe integer range, a failed run and separate `extensions.scribe.historicalAccounting`. The APR-shaped producer consumes an actual synthetic `ContextExecutionResult`: historical input 100 remains historical, while its continued current round reports only 7. Agent completion and a partial product outcome remain distinct. Rejected context admission has no fabricated current run.

These are synthetic source-level producer/consumer examples, with no copied product source, model credentials or billing evidence. Run them through the [Telemetry validation command](../00_project/validation.md#optional-agent-telemetry-mapping).

## Product and wrapper responsibilities

The selected new Action-facing interface is output **`telemetry-json`** and input **`telemetry-attributes`**. This guide defines the integration boundary; it does not claim current downstream wrappers already expose those names.

The product's .NET layer maps public Agent observations before older product-specific outputs discard information. It selects current runs, product outcome, safe metrics and historical scope, constructs the bounded record and exposes only an admitted JSON value. It owns `extensions.scribe`, `extensions.apr` or another canonical product namespace. Do not include prompts, candidates, raw responses, exception bodies, credentials or saved context. Structural validation cannot sanitize arbitrary product content.

The wrapper parses `telemetry-attributes` as a JSON object and inserts its members only under `extensions.caller` using the neutral admission rules. It must preserve identity, usage and product namespace, reject collisions and invalid/oversized input, and avoid coercing non-JSON values. It transports the complete admitted record through `telemetry-json` using the platform's safe output mechanism. Delivery policy and logging configuration are separate from the product's business result.

Constructors and caller composition can throw fixed `ArgumentException` diagnostics; strict serialization reports a bounded `TelemetryDiagnostic` with null output on failure. A product handles either as telemetry unavailable, preserving its independently determined business result. It must not emit partial/truncated JSON, replace missing data with known zero, leak source or exception contents, or replay product work. The callable examples demonstrate this boundary for duplicate membership and invalid/over-limit extensions.

Reconcile legacy metrics only when they cover the same physical invocation/current-run collection and have matching measurement and counter semantics. APR-shaped cache-hit/cache-miss fields do not establish arbitrary provider partitions. Scribe-shaped historical campaign accounting is not current invocation usage. Retain existing business outputs for their consumers; do not reconstruct richer telemetry from their lossy projections or silently change their historical meaning. Compare independent axes and coverage first, then reconcile numeric values without adding overlapping provider details or accounting charges.

## Compatibility and qualification

The explicit Agent API baseline is **`SolusAgent.Api` `0.1.0-internal.4`**, recorded as `SolusAgentApiBaseline` in the mapper project. It is an existing private experimental package; earlier `.1` release records are historical evidence. Source builds reference the current Api project. The mapper's required symbols must also compile against the actually installed `.4` package using isolated caches, rather than relying only on source or a version label.

The mapper is an opt-in packable source boundary, with no stable upgrade promise. Release preparation must select and verify the exact compatible Agent.Api and Contracts dependency versions as an independent telemetry package set; it must not apply the old five-Agent-package same-version rule to every new library or republish an immutable package identity. The clean installed new-mapper/Scribe/APR consumers, final package metadata, publication and Action/CLI installation remain later L1 qualification. Follow the existing private-feed access and exact-pin rules in [internal package consumption](../10_workflow/package-consumption.md); no new credentials or permissions are provisioned by the mapper.
