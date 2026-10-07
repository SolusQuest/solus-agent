# Context envelope draft

`SolusAgent.Api.Context` expresses implementation-owned restricted state and explicit Host intent through optional `IContextAgent`. An actual producer and Host consumer compile in the existing Api-only library; Context tests run in the existing managed contract runner. This is an executable M1 admission and restricted-access draft. It does not implement complete runtime restoration, a durable codec, serialization round trips or arbitrary crash recovery.

## Envelope and authority

`AgentContextEnvelope` contains a nonempty `ImplementationId`, positive `FormatVersion` and `CompatibilityVersion` discriminators, and defensively copied opaque bytes. These are implementation-scoped admission metadata, not package support versions, authentication or a portable transcript. Empty or malformed bytes remain representable so the receiving implementation can explicitly reject them. The outer API chooses no provider/model records or universal capacity policy.

The payload has no public property. `CopyRestrictedPayload()` deliberately copies restricted material for authorized admission or Host storage; every call returns a new array. Safe string representations and ordinary envelope serialization expose only structural metadata. An implementation's producer must exclude credentials, transports, live clients, delegates and tool instances from its serialized contents. Arbitrary bytes cannot be authenticated or scanned generically by this outer representation.

Current trusted instructions, bounds, required capabilities and separately classified `AgentInput` come from the current `AgentRequest`. Supplied content remains data and cannot grant tools, change policy, select provider configuration or replace those controls. The Host supplies execution capabilities again and establishes the integrity and authorized origin of the storage/reinjection path. Matching metadata alone is insufficient evidence of provenance. Implementations must validate their own grammar, compatibility and applicable authority before supplied state influences work.

## Intent and admission

`ContextExecutionRequest` requires an explicit intent. `Fresh` has no envelope; `NewRunFromContext` and `ContinueRun` require one. A contradictory shape or undefined intent is rejected before invocation with a safe validation error. Execution correlation does not become a retained-state lookup address or acquire a universal uniqueness/continuity rule.

| Observation | Ordinary work outcome | Meaning |
| --- | --- | --- |
| `Fresh` admission | Same-associated `AgentOutcome` | Fresh work was admitted. |
| `Supplied` admission | Same-associated `AgentOutcome` | Supplied state was admitted for the selected new-run or continuation intent. |
| `NotAttempted` | Zero-work cancellation or required-capability rejection | Current guarantees or caller cancellation prevented context admission. Any supplied usage inventory is complete and empty for this pre-work path only. |
| `Rejected` | None | Supplied state was rejected before work; no work failure is fabricated. |

Closed rejection codes distinguish unsupported context, implementation mismatch, unsupported format, incompatible context, invalid implementation payload and invalid selected transition. The actual fixture checks required guarantees before cancellation, then checks supplied-context support, implementation, format, compatibility, grammar and transition before work, progress or capture. It never calls ordinary execution as recovery from rejection. Starting fresh after rejection requires a separate explicit Host request.

`ContextExecutionResult` contains only correlation, intent/admission, a rejection code, nullable truthful work outcome and a separate capture status. Admission does not imply successful work, product acceptance or Host effects. Accepted ordinary Execution and Usage semantics remain unchanged: `ExecutionFailed` describes an actual work-operation failure, not context rejection. Constructors validate coherent structural observations without authenticating their origin.

## Explicit restricted capture

The Host optionally supplies `IRestrictedContextSink` for this invocation, separately from ordinary progress. The implementation documents its capture safe points and transfers an immutable envelope through `Capture`. The sink is a restricted channel; the Host can copy, keep or discard its contents under its own integrity, storage, protection, visibility, retention and deletion policy. This API has no agent-owned per-execution store, extraction by ID, retention duration or post-hoc lookup obligation.

Capture observation is independent of the work outcome. `NotRequested` means no sink was requested; `Unavailable` means no state was transferred; `Delivered` means the callback returned normally; `Failed` means capture failed and may already have delivered state to the Host. None proves durable storage, authenticated integrity, accepted effects or absence of retained bytes. A capture exception is confined to the closed status and cannot rewrite a completed work outcome into an execution failure. Ordinary progress/results and safe strings omit envelopes, payloads and raw exception text. No forced callback liveness or abrupt-process-death result is promised.

## Actual synthetic evidence

`ScriptedContextAgent` implements the optional interface in the independently compiled Api-only consumer. Its ordinary `IAgent` path delegates to the existing synthetic execution implementation. Its context path uses only call-local state, a newly injected narrow fake work capability and independent ordinary observers/restricted sinks. It transfers one terminal safe-point snapshot for an admitted call when requested, including partial/resource/cancellation/controllable failure stops. Rejected and not-attempted calls transfer nothing. Overlapping calls, including reused correlations, deliver to their own supplied sinks without shared lookup or last-writer-wins state. `RestrictedContextHost` is explicit test-only Host retention outside the ordinary consumer result.

The private toy JSON snapshot contains only an originating correlation, completed synthetic count, synthetic goal, finished marker and copied classified untrusted data. It excludes old trusted instructions, bounds, requested guarantees and live capabilities. Strict UTF-8/field/value admission rejects malformed, missing, duplicate, unknown or contradictory records. This minimized fixture grammar is not the runtime's saved format or restoration codec.

The toy implementation starts fresh at zero. Its supplied new-run policy uses a distinct toy correlation and resets the count; its unfinished continuation policy requires the toy origin/goal and retains completed work under current Host limits. A completed toy snapshot may be explicitly used for a new run. These are private fixture policies used to prove the public distinctions, not universal ID/recovery rules. Newly injected capabilities and current Host controls remain authoritative; prior text stays classified data.

Context tests invoke that actual producer and consumer for every rejection class, explicit intents, no fallback, current control/capability reinjection, exact/below-retained-count bounds, cancellation/work/observer failure, defensive copies, restricted canary confinement, positive capture and independent concurrent sinks. They inspect evaluated project/compiled assembly references and ordinary safe graphs including accepted Usage and closed capability metadata. See [Project validation](../../00_project/validation.md) for focused/full commands and actual evidence limits.

## Later implementation obligations

The selected self-owned runtime must eventually support complete saving and restoration of its own records under [Architecture](../architecture.md), [Runtime contract lifecycle](../../00_project/contract-lifecycle.md) and [Security boundary](../security-boundary.md). M2-M4 refine provider/model compatibility, logical conversation/tool/accounting/continuation records, integrity admission, checkpoint/unfinished-operation handling and complete runtime restoration. Host-owned storage protection and retention remain outside this envelope. This draft establishes none of those future guarantees, release support, migration readers or downstream product effects.
