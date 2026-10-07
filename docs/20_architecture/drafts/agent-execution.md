# Outer agent execution draft

`SolusAgent.Api` expresses bounded asynchronous execution independently of the self-owned runtime, model-provider types and tool registries. `IAgent` is the outer interface. The test-only `SolusAgent.ApiOnlyConsumer` implements it and calls it from business-style consumer code with Api as its only production reference. This is an executable M1 draft; it establishes contract expressibility and the synthetic implementation's behavior, not M2-M4 runtime guarantees or supported distribution.

## Host control and untrusted data

The Host creates `AgentRequest` with a nonempty execution `Guid`, trusted instructions, requested bounds and required capability flags. Constructors reject invalid identity, blank instructions, null members, undefined classifications/capabilities and nonpositive bounds before work. Validation exceptions use fixed messages and parameter names without echoing supplied content. A null request is rejected at the execution entrypoint.

`AgentInput` always represents data. Its Repository, Tool and Model source labels describe origin without granting instruction, policy or capability authority. The request takes a defensive read-only snapshot of the input list; input text and all control members are immutable. Instruction-like text in data cannot redefine Host-selected limits or required capabilities. The in-process contract does not authenticate callers: the Host must obtain trusted instructions from an authorized source. Future restored content remains data and must not acquire control authority through restoration or provider projection.

Credentials and restricted state have no request fields in this draft. Host transport owns credentials; context-envelope work belongs to its separate M1 leaf and restoration to M4. The [security boundary](../security-boundary.md) governs these requirements throughout implementation.

## Requested bounds and required guarantees

`AgentExecutionBounds` carries positive maximum work units and a positive finite `TimeSpan` duration. Work-unit meaning belongs to the chosen implementation. `SupportedCapabilities` advertises work-unit enforcement, duration enforcement and cooperative cancellation as distinct guarantees. `RequiredCapabilities` must be honored or explicitly rejected before work and progress; the rejection outcome names the unsupported flags and contains zero completed work.

A requested bound is not evidence of enforcement. A Host that requires duration enforcement must require `DurationLimit`, and similarly for work-unit enforcement or cancellation observation. An optional unsupported guarantee remains advisory. The separate [usage draft](usage.md) attaches optional configured limits and snapshots, with independently negotiated reporting, pre-dispatch count limits and post-response thresholds; production cumulative-budget enforcement remains future work.

The synthetic agent defines a work unit as one successfully awaited test operation. It supports `WorkUnitLimit` and `Cancellation`, and always applies the work-unit bound. It does not enforce duration; requiring `DurationLimit` rejects before work, while an advisory duration does not imply enforcement. M2-M3 own production stopping, resource admission, deadline, retry and accounting behavior.

## Progress and terminal outcomes

`AgentProgress` contains the original execution identity, a monotonic completed-work count and an optional same-execution immutable usage snapshot. An implementation calls `IProgress.Report` in execution order. Observer delivery may be scheduled by the observer, including after the terminal result; consumers that need immediate ordered delivery can use a synchronous observer, as the supplied consumer does. No cross-process event delivery or durable acknowledgement is promised.

`AgentOutcome` preserves the same execution identity and completed-work count. Its closed terminal vocabulary is:

| Reason | Meaning |
| --- | --- |
| `Completed` | The implementation's task work completed. This does not imply product acceptance, publication or platform effects. |
| `Partial` | The implementation deliberately ended with incomplete work. |
| `ResourceLimit` | A supported bound stopped execution with work still incomplete; it is never successful task completion. |
| `Cancelled` | Caller cancellation was observed. Observation does not prove remote work stopped. |
| `Failed` | A controllable failure stopped work, accompanied by a fixed safe failure code. |
| `UnsupportedCapability` | Required guarantees were rejected before work or progress. |

`HasPartialProgress` indicates preserved completed work in an incomplete run; resource, cancellation and failure outcomes can carry such progress. Failure codes distinguish execution failure from a synchronously throwing progress observer without including raw exceptions or text. Outcome construction rejects incoherent failure/rejection metadata. Abrupt process death cannot promise a terminal result.

The synthetic implementation checks required capabilities first. In its loop it checks task completion, caller cancellation, work-bound exhaustion, then intentional partial stopping before starting another operation. A successfully awaited operation is counted and reported before the next boundary check. Once the task goal is reached and reporting succeeds, cancellation arriving during the final successful work or its observation cannot undo completion. Cancellation still stops unfinished work and preserves completed counts; a cancelled unfinished operation adds no completed work. An unrelated operation cancellation is an execution failure. A synchronous observer failure retains the completed count and returns `ProgressObserverFailed`, including failure while reporting the final unit; asynchronously scheduled observer failures are outside the agent call's observation boundary.

## Ordinary diagnostics and evidence

Progress and outcomes have no free-form text, exceptions, input references, candidate payloads, credentials, continuation, context or generic object/dictionary fields. Request/data `ToString()` excludes content. Their input-bearing properties remain sensitive: redacted `ToString()` does not authorize logging or serializing the request or data themselves. Usage snapshots contain only the closed ordinary metadata documented by their [owning draft](usage.md); candidate submission/Host feedback and restricted context remain separate owning member families.

The [project validation](../../00_project/validation.md) runner executes the actual Api-only implementation/consumer and tests normal completion, resource exhaustion, intentional partial completion, pre-work rejection, cancellation during execution, preserved work, safe failures, control/data separation, defensive copying and synthetic diagnostic canaries. The consumer's real evaluated project graph and compiled assembly references establish the Api-only boundary while the existing production-graph and negative mutation probes remain active. No downstream private source, live provider calls or product acceptance evidence is inherited.
