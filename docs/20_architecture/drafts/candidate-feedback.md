# Candidate delivery and Host feedback draft

`SolusAgent.Api.Candidates` expresses individually correlated candidate delivery and Host feedback through the optional `ICandidateAgent : IAgent` contract. `ExecuteCandidatesAsync` composes the existing `AgentRequest` with candidate bounds and an explicitly supplied `ICandidateHost`. The separate Api-only consumer library contains the actual test-only producer, Host and calling consumer. This is an executable M1 draft, with no production loop, provider, durable transaction, context restoration, domain acceptance policy or supported compatibility commitment.

## Separate observations and channels

A candidate is untrusted domain data delivered only to `ICandidateHost.SubmitAsync`. The Host owns validation, accepted business progress, storage and external effects. A correlated Host acknowledgement reports a decision; it does not establish publication, effect completion or retry authority. `AgentOutcome.Completed` reports implementation-specific task completion, independently of those Host observations.

`CandidateSubmission` has immutable execution and submission identities, payload, and an optional rejected predecessor identity. Every correction has a fresh submission identity. Payload is a string with a 65,536-byte strict UTF-8 bound; empty or otherwise domain-invalid data can reach the Host for rejection. These generic representation checks do not perform product validation. `CandidateFeedback` may carry up to 16,384 UTF-8 bytes of correction data on an acknowledged exchange. Neither text field belongs in ordinary diagnostics.

`AgentProgress` and the existing `AgentOutcome` remain unchanged. `CandidateExecutionResult` adds only a closed stopping reason, safe immutable receipts, admitted repair/continuation counts and a computed accepted count. Receipt fields contain correlation and fixed acknowledgement/decision/instruction values. Results have no candidate, correction, raw exception, arbitrary text or Host reference. Prior accepted receipts survive later rejection, cancellation, failure or resource stops. The result snapshots the supplied receipt list; it is an in-process observation rather than a durable Host acknowledgement store.

Payload-bearing request, submission and feedback `ToString()` methods omit content. This does not authorize logging or serializing their content-bearing properties. Constructors use fixed validation errors without echoing supplied text. Synthetic canaries exercise the actual Host channel and prove confinement in serialized ordinary results/progress and error paths under the [security boundary](../security-boundary.md).

## Decisions, instructions and uncertainty

Acknowledged feedback must independently specify Accept/Reject and Continue/End. An acceptance can request more work or end execution; a rejection can request a bounded correction or end execution. Correction data cannot change Host-selected execution controls or bounds.

| Observation | Decision admitted | Synthetic behavior |
| --- | --- | --- |
| Correlated Acknowledged | Accept or Reject, with Continue or End | Retain the receipt, then consider completion, Host End and further admission. |
| Null feedback | None; Missing | Stop incomplete without resubmission. |
| Failed exchange or callback exception | None; Failed | Stop with fixed failure metadata, retaining earlier receipts. |
| Explicit Unknown exchange | None; Unknown | Stop incomplete, retaining uncertainty. |
| Wrong execution or unknown submission | None; Mismatched | Stop with a fixed protocol failure. |
| Prior submission ID in the same execution | None; Duplicate | Stop with a fixed protocol failure; do not apply the prior decision to the pending candidate. |
| Caller cancellation while feedback is pending | None for the pending submission; Unknown | Return Cancelled, retaining earlier delivered acknowledgements. |

Association is checked before reading a feedback decision or instruction. A rejected or mismatched response cannot erase earlier acknowledged progress or manufacture acceptance for another submission. The receipt identifies the actual submitted candidate rather than copying the wrong feedback identity. Matching IDs are correlation, not authentication; the Host must deliberately supply the channel. A custom implementation remains responsible for truthful observations: constructors enforce structural consistency, not acknowledgement provenance.

Named feedback stops require the corresponding last receipt: Missing, Failed, Unknown, Mismatched or Duplicate for their acknowledgement-specific stopping reasons; acknowledged End for HostEnded; Reject/Continue for ProductionExhausted or RepairLimit; and Accept/Continue for ContinuationLimit. An earlier receipt cannot justify a contradictory terminal observation, and those reasons cannot invent an unobserved submission. Generic completion, cancellation, submission/work-unit limits and production/observer failures retain implementation-specific stopping policies: they may have no new receipt or preserve prior acknowledgements, and cancellation or a resource stop may retain an Unknown pending receipt. This validation does not equate task completion, acceptance, receipt counts, work units or Host effects.

The callback returns one response per submission. Duplicate delivery is expressible as replay of a previous response while a subsequent candidate is pending. This draft does not promise a streaming feedback inbox or durable cross-process deduplication. Each synthetic run has its own receipt set; feedback from a different run is not inherited.

No uncertain, missing or failed exchange is retried automatically. The Host may already have accepted data or performed an effect before throwing, returning uncertainty or losing observation to cancellation. Failure does not establish rejection or rollback. A non-cooperative Host may continue after the agent cancels its await; a later callback result is not admitted into the returned immutable result. The Host owns any reconciliation of its business progress and effects. Abrupt process death cannot promise a terminal observation.

## Synthetic bounds and ordering

The test-only `ScriptedCandidateAgent` defines a work unit as successful production of one structurally deliverable candidate. It advertises WorkUnitLimit and Cancellation, rejects unsupported required capabilities before cancellation, production, progress or Host calls, and does not advertise or enforce duration. Its ordinary `ExecuteAsync` uses the existing synthetic work probe; candidate exchange is explicitly selected through `ExecuteCandidatesAsync`.

`CandidateExecutionBounds` specifies a positive maximum total submissions and nonnegative maximum repairs and accepted-result continuations. A repair is a follow-on production admitted after Reject/Continue; a continuation is a follow-on production admitted after Accept/Continue. Both differ from completed production and submission counts. Total submission and execution work-unit bounds are checked before any further admission. Exhaustion returns ResourceLimit with the specific SubmissionLimit, WorkUnitLimit, RepairLimit or ContinuationLimit stopping reason. An admitted follow-on that subsequently fails or is cancelled remains counted as admitted, not completed.

The result does not equate receipt counts with implementation-defined outer work units. Only this synthetic producer uses one production per work unit. Result admission counts cannot exceed the corresponding acknowledged Continue observations; End provides no follow-on allowance.

Each production is awaited, structurally checked, counted and reported to the ordinary synchronous observer before the Host is called. A throwing observer preserves the completed production count, returns Failed/ProgressObserverFailed and prevents that submission. Cancellation observed before the Host call similarly produces no receipt for an unsent candidate. Once the Host is invoked, pending cancellation creates an Unknown receipt. The synthetic agent cancels observation even when its fake producer or Host ignores the token; this is not remote-work termination or effect rollback.

A delivered correlated acknowledgement is retained even if caller cancellation was signalled before its successful await resumed. Final accepted scripted work completes without consuming an unnecessary continuation allowance, including at an exact bound. Host End with scripted work incomplete returns Partial/HostEnded. A final rejection requiring an unavailable correction returns Partial/ProductionExhausted; it cannot become acceptance or successful completion. Cancellation prevents subsequent admissions after an already delivered acknowledgement. Rejection/End returns an incomplete HostEnded result even on the last scripted item.

These policies demonstrate expressibility through a deterministic producer/consumer. The interface alone does not enforce a runtime loop, resource engine, durable acknowledgement retention or exactly-once Host effects. Production mechanisms belong to their later milestones; implementation-scoped context is a separate member family.

## Validation

The Candidates tests in the existing [contract runner](../../00_project/validation.md) exercise rejection and correction using actual Host feedback, independently acknowledged candidates, all decision/instruction combinations, accepted-first/rejected-second partial progress, exact/zero/exhausted bounds, missing/failed/unknown acknowledgement, wrong and duplicate association, cancellation while feedback remains pending, retained delivered acknowledgement, late-result non-admission, production and observer failures, immutable receipts and synthetic confinement canaries. Terminal-result tests reject acknowledgement/Host-instruction contradictions against the latest receipt and empty histories, while preserving generic stopping policies and earlier acceptance independently of work units. Instrumented in-memory Host effects remain independent and are never automatically replayed. Direct candidate-entrypoint tests prove required-capability admission and progress-before-Host ordering.

The full suite retains accepted Execution and Tools behavior, evaluated independent Api-only/Tools-only project graphs and negative forbidden-reference/source probes. Local and ordinary CI evidence support these actual draft paths, not a production provider, product acceptance implementation, restoration engine, distribution or release.
