# Managed runtime candidate execution draft

`RuntimeAgentFactory.Create` returns the actual managed runtime as `IAgent`, also implementing the existing optional `ICandidateAgent`. Startup selects the runtime; business Host and calling consumer compile against Api alone. Select `ExecuteCandidatesAsync` explicitly for candidate feedback. Ordinary `ExecuteAsync` retains its independent Final-completion behavior.

```csharp
IAgent agent = RuntimeAgentFactory.Create(configuration, options);
ICandidateAgent candidates = (ICandidateAgent)agent;
CandidateExecutionResult result = await candidates.ExecuteCandidatesAsync(
    candidateRequest, candidateHost, progress, cancellationToken);
```

This path reuses the actual bounded [provider-turn operation](runtime-execution.md), with one physical attempt per logical response and no provider or Host-effect replay. Candidate payload is untrusted data delivered only to `ICandidateHost`. Product acceptance, effects, storage and reconciliation remain Host-owned.

## Production, delivery and continuation

Each invocation owns its classified records, latest provider continuation, attempt inventory, receipts, submission identities and follow-on counters. No shared execution-ID lookup exists. Required unsupported guarantees reject before hooks, progress, provider or Host calls, with complete empty usage. Work units remain accepted logical provider responses; acceptance before a later settlement or delivery failure remains completed work with retained usage.

Candidate handling requires an accepted Final and settlement permitting continuation. Exposure denial or settlement Stop prevents delivery, including for an accepted Final. Missing, failed, unknown or wrong integration also prevents delivery. This leaf does not execute ToolCalls; the ordinary tool branch is separately owned. Safe synchronous progress precedes the Host invocation. Observer failure preserves work/usage and prevents delivery. The final shared admission cut gates the Host call.

Each submission is invoked once and has a fresh ID. A repair backlink identifies only the actual immediately rejected predecessor. Full feedback association is checked before using decisions or instructions: wrong execution or unknown submission is Mismatched, while an earlier submission ID from this invocation is Duplicate. Missing, failed and unknown feedback retain their distinct [draft](candidate-feedback.md) receipts and stops. Earlier accepted receipts survive every later stop. Correlation proves neither authentication, durable storage nor effect rollback.

| Correlated acknowledgement | Behavior |
| --- | --- |
| Accept / End | Complete, including at an exact count ceiling. |
| Reject / End | Stop incomplete with `HostEnded`; no repair. |
| Accept / Continue | Admit another bounded production, counted as a continuation only after admission. |
| Reject / Continue | Admit a bounded repair using actual optional Host correction as input data. |

`ProviderFinish.Final` is candidate data in this entrypoint. Script exhaustion and accepted Continue do not complete it. Null correction permits bounded repair using existing rejected model history. Supplied correction is appended only as `ProviderInput.Data`; it changes neither Host instructions nor provider/model scope, tools, bounds, credentials or required continuation. Strict UTF-8 bytes are charged once to retained records. Record/input/request/retention capacities apply before further dispatch, and provider continuation remains exactly associated with its accepted origin.

## Bounds, cuts and terminal observations

Submission, work, repair and continuation ceilings apply before next production. End does not consume a follow-on allowance or lose its receipt at an exact ceiling. Admitted follow-on counts survive subsequent failure/cancellation; denied admission does not count. Attempt inventory, completed units and receipts remain separate. Finite RuntimeOptions and ProviderExchangeBounds apply throughout the invocation without compaction or truncation.

| Actual stop | Candidate category | Ordinary outcome |
| --- | --- | --- |
| Whole-run duration | `DurationLimit` | ResourceLimit |
| Caller cancellation | `Cancelled` | Cancelled |
| Submission/work ceiling | `SubmissionLimit` / `WorkUnitLimit` | ResourceLimit |
| Follow-on allowance | `RepairLimit` / `ContinuationLimit` | ResourceLimit |
| Provider/record/retained capacity | `RuntimeLimit` | ResourceLimit |
| Exposure denial or settlement Stop | `ProductionStopped` | Partial |
| Provider/integration failure | `ProductionFailed` | Failed |
| Candidate Host Reject / End | `HostEnded` | Partial |

The three appended draft categories distinguish deadline/capacity from caller cancellation/work counts, and provider integration Stop from candidate Host End. Existing acknowledgement-specific stops still require the corresponding last receipt. Generic cut/resource/production stops can retain earlier receipts or Unknown pending delivery without inventing acknowledgements.

The [same whole-run cut](runtime-execution.md#deadline-cancellation-and-completion) bounds Host feedback observation. At feedback selection, an already completed callback is observed and classified even when cancellation or duration is now observable. The cut determines the terminal result and forbids further effects. A callback still pending at that selection yields Unknown delivery. Abandoned success/fault cannot alter returned receipt or usage snapshots. Tests require terminal return before releasing held callbacks. This preserves delivered facts without promising remote stop or granting late feedback authority.

The inherited finite settlement allowance grants only closure; candidate Host waiting gets no additional execution grace. Caller cancellation wins when already observable at the cut; otherwise duration is a resource stop. Trusted synchronously blocking extensions/observers are not preemptible in-process. Durable acknowledgement retention, cross-process recovery, full M3 accounting and M4 restoration remain unproved.

## Actual validation surface

`Runtime/Candidates` tests invoke public startup with the independent Scripted Provider and Api-only `CandidateConsumer`/`ScriptedCandidateHost`. The metamorphic pair changes only Host correction and verifies changed classified provider requests and repair content. Tests cover all decisions, exact/zero/plus-one count and UTF-8 capacity neighbors, independent admissions/usage, settlement gating, progress ordering, feedback uncertainty/association, controlled cut barriers, late immutable results, concurrent equal execution IDs and confinement canaries. The M1 producer retains its narrower synthetic evidence identity.

Use [project validation](../../00_project/validation.md) for focused/full checks. Scripted evidence does not establish actual transport, business effects, storage, live calls, release qualification or downstream migration.
