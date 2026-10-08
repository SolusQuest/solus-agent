# Managed runtime execution draft

`SolusAgent.Runtime.Startup.RuntimeAgentFactory.Create(RuntimeConfiguration, RuntimeOptions?)` returns `IAgent`. Application startup selects the implementation and supplies live Host bindings; business orchestration needs only Api. Runtime owns each invocation's driver, classified records, attempt inventory, accepted model data and continuation. The independently compiled [Scripted Provider](../../../tests/ConsumerProbes/CustomProvider/ScriptedProvider.cs) references Runtime.Api alone. The [Runtime.Execution tests](../../../tests/SolusAgent.ContractTests/Runtime/Execution) compose it with the actual production Runtime; the existing M1 producers and Host consumers remain synthetic expressibility evidence.

```csharp
IAgent agent = RuntimeAgentFactory.Create(configuration,
    new RuntimeOptions(timeProvider: TimeProvider.System, requireContinuation: true));
AgentOutcome outcome = await agent.ExecuteAsync(request, progress, cancellationToken);
```

`configuration`, `request` and `progress` are Host-supplied values. This example does not supply transport credentials, tool authority or business acceptance. No dependency-injection framework, internal loop type, service locator or persistence format is required.

## Support and work meaning

The implementation advertises `WorkUnitLimit`, `DurationLimit`, `Cancellation` and `UsageReporting`, plus `OrderedExposure` and `ProviderBounds`. Required outer capabilities are checked before progress, hooks or effects. Unsupported `DispatchLimits` or `UsageThresholds` return the pre-work `UnsupportedCapability` outcome with the unsupported flags and complete empty inventory. Optional usage policies remain advisory; truthful nullable reporting does not promise complete measurements, token ceilings, reservations or billing accuracy.

Required runtime integration is validated at construction. Missing required hooks or required durable acknowledgement without hooks rejects with a safe configuration error. Optional absence of hooks is permitted only when ordered exposure is not required. A supplied hook is always honored. A profile requiring continuation selects `RuntimeOptions.RequireContinuation`; unavailable provider support rejects before effects and each provider request requires that semantics. `Describe` includes only support, numeric ceilings, closure allowance and closed configuration requirements, omitting provider/model labels, the clock and live bindings.

One completed work unit is one logical model response admitted against the actual runtime request. An attempt, dispatch or usage capture is not a completed unit. This leaf makes one physical attempt per logical turn and never retries. An accepted `Final` completes the public execution; `MaximumWorkUnits` is a ceiling, not a request to generate that many final responses. A valid tool-call response counts as one accepted logical response and enters the [runtime tool adapter](runtime-tools.md) only after provider settlement permits further admission. Its complete successful results feed the next bounded model turn; tools do not add model work units. `ICandidateAgent` and `IContextAgent` remain later leaves.

## Per-turn ordering and retained facts

The actual reusable `ProviderAttemptOperation` performs bounded request admission, exposure permission, one provider invocation, observation retention, original-request response admission and same-attempt settlement. `RunState.AdmitTurn` checks the current cut, completed-unit ceiling, inventory capacity, available record slot and payload bounds before new effects. All externally supplied `AgentInput` sources remain data; their source labels never authorize `FromModel`, Host instructions or tools. Only an admitted response becomes an accepted model record, anchored to its exact scope and attempt; required continuation remains with that turn without truncation or reinterpretation.

BeforeDispatch permission must match provider/model scope, execution/logical/physical IDs, ordinal and acknowledgement requirement. Null, failure, unknown, denial, mismatch or insufficient strength grants no dispatch. A final local admission cut follows the receipt. Every entered exposure receives one local settlement attempt, including denied or abandoned permission. An optional-hook-free path performs no invented Host acknowledgement. Host `Durable` remains its assertion, not a library storage certificate.

The [provider exchange draft](provider-exchange.md) now gives each single-use `ProviderRequest` its own `Observation`. The runtime can atomically `Seal` it independently of payload completion. Captured dispatch, usage and accounting before the cut survive; writers after it reject. Snapshots are immutable and sealing is idempotent. A faulting or absent interface result is normalized as an actually observed exchange failure, preserving current-channel facts. A foreign result becomes `Rejected/InvalidAssociation` without relabeling foreign usage. Associated returned observations can supply otherwise undisclosed facts before the local cut; conflicting streamed facts reject and retain the original capture.

`Accepted` on a returned response is insufficient: a forwarding interface could guard it under looser bounds or different continuation requirements. Runtime reuses `ProviderResponse.ValidateFor` against its actual request, retaining observation first. Invalid returned payload becomes a normalized rejected exchange, with no accepted record or completed unit. Missing usage remains unavailable, even when dispatch is known. Accepted work is recorded before settlement and progress, so later failure cannot erase it.

`RuntimeSettlement.ProviderInvoked` separates a seam that was never called from one still pending at a local cut. A pending invoked provider may have no obtained outcome/error while retaining known or unknown observations; only `Cancelled` or `DurationLimit` permits that state. An actual returned exchange or observed fault has a normalized outcome/error. Never-invoked closure has NotDispatched/unavailable measurement and an explicit admission stop. Settlement delivery status never occupies the original stop field.

## Deadline, cancellation and completion

One injected monotonic clock measures the whole-run local deadline, including input admission, exposure, provider waiting and normal closure. Every start and response acceptance shares a run-owned gate with the cut. Caller cancellation wins when it is already observable at that cut; otherwise reaching the deadline closes admission. At an exact deadline, a ready permission or response cannot override the cut. Once a cut is observed its cause is retained. This is a local observation/linearization rule, not a real-time scheduling or remote-stop promise.

The cut seals the active observation immediately, wakes the wait and signals extension cancellation asynchronously. It does not wait for uncooperative cancellation registrations. Held asynchronous exposure/provider operations cannot delay local termination; late receipts, results, observations or faults have no path to new admission or returned snapshots. Faults of abandoned tasks are observed. Trusted in-process extensions must bound their own synchronous invocation/property/observer work and allocations; the runtime cannot preempt synchronously blocking code, reclaim a remote operation or forcibly remove references held by an extension.

Settlement uses the same clock with a finite allowance: normally the smaller of remaining run time and `SettlementGrace`; after a cut, at most one explicit `SettlementGrace` is available for closure. The default is one second and the configurable maximum is thirty seconds. Thus execution admission ends at the requested duration, while an uncooperative cleanup callback can extend terminal return by at most the configured post-cut allowance, subject to local scheduling. This allowance grants only closure, never another provider/tool/candidate effect. Late Continue cannot reopen admission.

Terminal precedence is pre-work unsupported capability; then an observed caller/deadline cut; then exposure/provider/bounds/closure failures; then the response handler and ordinary progress. Denial returns Partial. Invalid association, observed provider failure and uncertain closure delivery return Failed; payload/capacity limits return ResourceLimit. A valid correlated settlement Stop closes further admission while an already accepted Final still completes. For a nonterminal handoff, Stop or missing/failed/unknown/mismatched closure prevents the next turn. A progress exception is separately `ProgressObserverFailed`, preserving accepted units and usage. Cancellation observed during final progress prevents completion without erasing that unit. No terminal result is later rewritten by abandoned work.

## Finite state and next leaves

| Dimension | Policy |
| --- | --- |
| Initial classified inputs and each provider request | `ProviderExchangeBounds.MaximumInputs`; one Host instruction consumes a slot |
| Whole-run input/model/tool-result records | `RuntimeOptions.MaximumRecords`, default and ceiling 64; reserve a response slot before exposure |
| Whole-run attempt inventory | `MaximumAttempts`, default and ceiling 64; independent of completed units and measurement availability |
| Retained variable input/definition/model/tool-result bytes | `MaximumRetainedBytes`, default and ceiling 196608; strict provider payload accounting, no wire-size claim |
| Response and continuation | Configuration ceilings, with response ceiling lowered to remaining retention capacity |
| Time | Current `AgentRequest.Bounds.MaximumDuration`, plus only the explicit bounded closure allowance |

Capacities never scale allocation with an arbitrary Host work allowance. No compaction or silent truncation occurs. Immutable snapshots preserve complete attempt inventory even when individual measurements remain unavailable. Concurrent calls, including equal Host execution IDs, allocate independent records, logical/physical IDs, observation channels and cuts; no run lookup or permission cache exists on the shared agent.

The internal turn operation returns accepted data, normalized provider facts, local stop and settlement status only after closure. Its real two-turn tests demonstrate continuation replay, work/inventory admission, and non-vacuous Stop/unknown gating. The public driver handles Final and admitted ToolCalls through the actual generic batch operation. R3 supplies candidate feedback; these leaves include no unused public handler framework. Full reservation/retry/budget accounting is M3, restoration M4 and packaging/migration M5. The optional DeepSeek adapter owns its separate transport proof. No live/paid run, storage certification, product acceptance or release support is established here.

Use [project validation](../../00_project/validation.md) for focused and full checks. Tests use explicit barriers and a controlled clock, and require local terminal return before releasing held operations. They cover original-request substitution, phase cross-products, boundaries, same-identity isolation and ordinary-data canary confinement through actual Runtime code.
