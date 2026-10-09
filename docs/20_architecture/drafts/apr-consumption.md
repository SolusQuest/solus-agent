# APR-shaped draft consumption

The test-only `AprHost` library is a non-packable Api-only synthetic business Host: it references `SolusAgent.Api` as its only production assembly and receives the outer `IAgent` seam with its optional `ICandidateAgent` and `IContextAgent` seams, the current `AgentRequest` controls, a deliberately supplied `ICandidateHost` feedback channel and a separately supplied call-scoped `IRestrictedContextSink`. The sole test runner owns the finite APR scenario and startup composition under `tests/SolusAgent.ContractTests/ConsumerProbes/Apr`: one actual `CustomTools` `CounterTool` with its `CounterCapability`, one actual guarded `CustomProvider` provider, the existing `RuntimeConfiguration`, `ConfigurationConsumer` and Host exposure hooks, wired into the business Host over outer interfaces only. This is an executable M1 consumption example with no runtime implementation and no provider transport; the M2 composition below adds actual production runtime execution on scripted transport, and M3 adds APR consumption of its accounting and provider retry policy. These tiers carry no product source, PR identity, findings or evidence policy, GitHub authority, packaging or downstream migration.

## Business Host and separate seams

`AprBusinessHost` orchestrates one business execution while the two API seams keep their own request and result types. `RunAsync` runs the context seam first and never starts candidate production or submission when admission was rejected or not attempted, so a rejected run has no work, capture, effect or fresh fallback. A missing optional seam is rejected before work instead of silently falling back to another seam: a supplied intent without a context seam is `Rejected` with `UnsupportedContext`, and a run requiring an unsupported guarantee reports `UnsupportedCapability`. There is no universal combined public request type and no durable restoration claim; the coordination is business orchestration over the existing separate contracts.

Acceptance is product-owned and recorded by the Host itself. The `RecordingCandidateHost` decorator passes every submission to the supplied channel unchanged and records `AprAcceptanceRecord` entries only for correlated acknowledged Accept feedback whose payload parses the minimized `AprItem` grammar. Those records are independent of the agent's `CandidateReceipt` list, of `AgentOutcome.Reason` and of any external effect. Product effects live on the explicit `AprHostAcceptance.ApplyEffects` operation, which applies each accepted candidate at most once; acceptance and completion never increment `EffectCount`.

## Minimized synthetic business data

`AprItem` carries only a bounded synthetic identifier and nonnegative value with the payload grammar `apr-item:<itemId>:<value>`. `TryParse` admits that grammar strictly and never echoes supplied bytes. No pull-request identity, review finding, evidence schema, platform service or credential exists in this model, and payloads stay untrusted candidate data excluded from ordinary diagnostics.

The test-only prior-context grammar is an implementation-local JSON record with an origin correlation, completed units, goal, finished marker, retained synthetic total and copied untrusted notes. It is a minimized in-memory fixture format, not a runtime saved format, restoration codec or compatibility commitment. Supplied new runs require a distinct toy correlation and keep prior state as data; unfinished continuations require the same origin, an unfinished snapshot and the current goal claim. Grammar, implementation, format, compatibility and transition checks run before any provider, tool, capture or progress effect, and rejected supplied state returns no work outcome.

Saved bytes stay untrusted data. Saved instruction, limit or capability claims never replace the current `AgentRequest` controls, the current runtime configuration or the newly supplied capability objects; `SavedInstructionLimitAndCapabilityClaimsRemainDataUnderCurrentControls` drives the run to the current work bound while the current Host instructions and the single registered counter definition remain authoritative.

## Candidate correction, acceptance and completion

One work unit is one completed synthetic review production: a guarded provider exchange with a completed real tool round and its closing exchange. Each production builds classified inputs only from Host instruction, current `AgentInput` data, admitted prior state, a synthetic step value and any Host correction text, all as untrusted data. The provider script turns that data into real tool call requests; complete batch preparation and concrete narrow capability admission run for every requested member before any invocation, and each accepted call is invoked once. The produced `AprItem` value derives from the actual guarded tool output and admitted prior state, so provider and tool results causally drive candidate production, state, capture and usage rather than merely sitting next to them.

Repair association uses the actual `CandidateSubmission.RepairsSubmissionId` member; no stand-in local association field exists. A rejection with `Continue` admits one bounded repair production that consumes the correction text as data; an acceptance with `Continue` admits one bounded continuation production. The explicit finite goal is the scenario's production count, which is distinct from Host acceptance: the run reports `Completed` only when the goal is reached with an accepted final acknowledgement. `HostEnd`, submission, work-unit, repair and continuation bounds produce their own stop reasons, with `ResourceLimit` outcomes stopping before any further provider or tool effect. Missing, explicit `Unknown`, failed, mismatched and duplicate feedback stop with fixed protocol metadata and never authorize continuation, manufactured decisions or automatic resubmission; earlier receipts and Host acceptance survive every later stop, and cancellation after delivered acceptance retains that acknowledgement.

Host effects stay separate: `AcceptedAndCompletedAloneLeaveEffectsZeroUntilTheExplicitHostOperation` observes zero effects after an accepted, completed run and only the explicit Host operation applies them.

## Usage and ordered exposure

Each composed configuration attempt contributes its actual `UsageAttemptObservation` to cumulative `AgentRunUsage` snapshots carried on `AgentProgress` and `AgentOutcome`. Known, partial and unavailable provider measurements travel unchanged: a partial observation keeps its unknown output count unknown and an unavailable observation fabricates no zero. Dispatch exposure and accounting claims remain independent fields, and measurements are retained after later feedback failure.

Ordered pre-dispatch permission and same-attempt closure stay on the Runtime.Api startup surfaces through the existing `ConfigurationConsumer` and the actual Host hooks. Held permission leaves actual provider effects zero; denied, missing, unknown and mismatched receipts prevent dispatch; a failed or unknown closure retains the original attempt and usage while blocking later work on that consumer. Only an authorizing same-attempt settlement lets a production continue: a missing, failed, unknown, mismatched or deliberate Stop receipt terminates that production chain before any later tool, provider or candidate work while the attempt and usage evidence is retained. `AprHost` itself references neither `Runtime.Api` nor `Runtime`, which `AprHostCompiledAssemblyReferencesApiOnlyWithoutRuntimeToolsOrFixtureEdges` proves on the compiled assembly.

The scenario advertises only the guarantees it honors end to end: work-unit bounds, cancellation and usage reporting. Required guarantees are admitted or rejected before work at the outer seam, and outer-owned guarantees are projected away from the inner provider-dispatch consumer after that admission, so the composed path cannot re-read them as an inner failure. Configured dispatch limits and the fixture's small script capacity remain internal test policy: a request requiring the unadvertised dispatch-limits guarantee rejects before work instead of promising terminal semantics the scenario cannot report truthfully.

## Restricted and credential confinement

Positive synthetic canaries prove the restricted path is nonempty: the call-scoped sink receives the admitted prior notes and tool-derived state in one safe-point capture, and those restricted notes stay on that restricted surface. The restricted canaries, the candidate payload grammar and the correction text are absent from ordinary results, progress, receipts, attempt diagnostics, configuration disclosures and safe strings. The restricted notes remain in the restricted saved context bytes, and the correction text may still reach the model as explicitly untrusted data. The fixture credential canaries are excluded from all of these surfaces, including model-visible inputs, tool arguments and saved context bytes. The test fixtures carry private credential canaries inside the provider and hook implementations; real hooks and provider calls run in every positive path, so their absence from those surfaces is not a vacuous check.

## Runnable example

The following composes the actual fixtures and runs one coordinated business execution; it matches the signatures used by `SuppliedPriorContinuationAdmitsWorkAndCapturesToolDerivedRestrictedState`.

```csharp
var startup = AprStartup.Create(new AprStartupOptions { TargetWorkUnits = 1, TargetProductions = 2 });
var execution = new AgentRequest(Guid.NewGuid(), "review the supplied synthetic items",
    [new AgentInput(AgentInputSource.Repository, "synthetic item batch")],
    new AgentExecutionBounds(8, TimeSpan.FromMinutes(1)), AgentCapability.None);
var supplied = new AprContextState(execution.ExecutionId, Units: 0, Goal: 1, Total: 4, Notes: ["prior note"])
    .ToEnvelope(AprScenarioAgent.ImplementationId);
var sink = new AprFixtures.CollectingSink();
var feedback = new ScriptedAprFeedback((submission, index, _) => ValueTask.FromResult<CandidateFeedback?>(
    index == 0
        ? AprDecisions.RejectWithCorrection(submission.ExecutionId, submission.SubmissionId, 7)
        : AprDecisions.Accept(submission.ExecutionId, submission.SubmissionId)));

var run = await startup.Host.RunAsync(
    new ContextExecutionRequest(execution, ContextExecutionIntent.ContinueRun, supplied), sink,
    new CandidateExecutionRequest(execution, new CandidateExecutionBounds(8, 4, 4)), feedback);

// Host acceptance and product effects stay distinct from the agent's receipts and completion.
var appliedEffects = startup.Host.Acceptance.ApplyEffects();
```

`AprStartup`, `AprContextState`, `ScriptedAprFeedback` and `AprDecisions` are test-only scenario helpers in the managed runner; `AprBusinessHost`, `AprItem`, `AprAcceptanceRecord` and `AprHostAcceptance` are the Api-only library surface with XML documentation. The snippet runs in the managed runner's Apr test context with the `SolusAgent.Api.Capabilities`, `SolusAgent.Api.Candidates`, `SolusAgent.Api.Context`, `SolusAgent.Api.Execution` and `SolusAgent.ContractTests.ConsumerProbes.Apr` namespaces imported alongside the implicit `System` namespaces; it was compiled and executed there against these actual helpers as the documentation proof for this leaf.

## Requirement-to-evidence matrix

Every row below names implemented test methods in `SolusAgent.ContractTests.ConsumerProbes.Apr`; the focused run executes 59 nonempty cases from 56 named methods, with the three boundary theories covering both plain and XML-special fixture paths.

| Obligation | Named executed evidence |
| --- | --- |
| Api-only compiled Host, reusable fixtures, no Runtime or product source | `AprHostEvaluatesApiOnlyNoPackagesManagedNet10AndContainedSources`, `AprHostCompiledAssemblyReferencesApiOnlyWithoutRuntimeToolsOrFixtureEdges`, `SolutionAndTestRunnerRegisterTheAprHostProject`, `ForbiddenRuntimeEdgeFailsTheAprAllowedBoundaryAssertion`, `ForbiddenRuntimeApiEdgeThroughAnImportFailsTheAprAllowedBoundaryAssertion`, `LinkedSourceOutsideTheWorkspaceFailsTheAprCompileContainmentCheck` |
| Supplied prior context admitted and captured | `SuppliedPriorContinuationAdmitsWorkAndCapturesToolDerivedRestrictedState`, `SuppliedNewRunKeepsPriorStateAsDataWithADistinctCorrelation` |
| Rejected or incompatible context stops before work, capture, effects and candidate work | `RejectedInvalidContextStopsBeforeWorkCaptureEffectsAndCandidateWork`, `IncompatibleContextRejectsWithoutWorkOrCapture`, `ImplementationMismatchedContextRejectsWithoutWorkOrCapture`, `UnsupportedContextFormatRejectsWithoutWorkOrCapture`, `InvalidRunTransitionRejectsWithoutWorkOrFreshFallback`, `SuppliedContextWithoutAContextSeamIsRejectedBeforeWork` |
| Current Host controls and capture separation | `SavedInstructionLimitAndCapabilityClaimsRemainDataUnderCurrentControls`, `CaptureFailureIsObservedSeparatelyFromTheWorkOutcome` |
| Required-guarantee and same-execution admission before work | `UnsupportedRequiredGuaranteeIsRejectedBeforeWork`, `RequiredDispatchLimitsIsRejectedBeforeWork`, `MismatchedExecutionAssociationIsRejectedBeforeAnyWork` |
| Rejection, correction and repair association | `RejectedCandidateIsCorrectedThroughActualToolOutputAndRepairsSubmissionId` |
| Acceptance with continuation and explicit completion | `IntermediateAcceptanceWithContinuationAwaitsTheExplicitGoalWithAllReceiptsRetained`, `AcceptedAndCompletedAloneLeaveEffectsZeroUntilTheExplicitHostOperation` |
| Partial, resource and cancellation stops with retained progress | `HostEndAfterAcceptedProgressReturnsPartialAndRetainsHostAcceptance`, `SubmissionBoundStopsResourceLimitBeforeTheNextEffectAndRetainsAcceptance`, `WorkUnitBoundStopsResourceLimitBeforeTheNextEffect`, `RepairLimitDeniesTheFollowOnProductionAndRetainsExistingFacts`, `ContinuationLimitDeniesTheFollowOnProductionAndRetainsAcceptedProgress`, `CancellationAfterAcceptedProgressRetainsReceiptsAndHostAcceptance`, `RequiredWorkUnitLimitIsAdmittedAndEnforcedAtTheOuterBound`, `RequiredWorkUnitLimitIsAdmittedOnTheContextSeam` |
| Missing, unknown, failed and wrong feedback | `MissingAcknowledgementAfterAcceptedProgressStopsWithoutFurtherWorkOrReplay`, `UnknownAcknowledgementAfterAcceptedProgressStopsWithoutAManufacturedDecision`, `FailedFeedbackExchangeRetainsExistingFacts`, `MismatchedFeedbackDoesNotAuthorizeContinuation`, `DuplicateFeedbackDoesNotApplyThePriorDecisionToThePendingCandidate` |
| Whole-batch admission and association before effects | `ALaterInvalidBatchArgumentAdmitsNoCandidateAndNoToolEffect`, `AMismatchedConcreteCapabilityAdmitsNoCandidateAndNoInvocation`, `AWrongAssociatedToolResultAdmitsNoSuccessfulCandidate` |
| Honest known, partial and unavailable usage | `CompleteKnownProviderUsageTravelsThroughProgressAndOutcome`, `PartialProviderUsageKeepsUnknownMeasurementsUnknown`, `UnavailableProviderUsageIsRetainedWithoutFabricatedZero`, `RetainedMeasurementSurvivesLaterFeedbackFailure` |
| Ordered exposure and same-attempt closure | `MatchingPermissionAllowsDispatchAndSameAttemptClosure`, `HeldExposurePermissionLeavesActualProviderEffectsZero`, `DeniedExposurePreventsProviderDispatch`, `MissingExposureReceiptPreventsProviderDispatch`, `UnknownExposureReceiptPreventsProviderDispatch`, `MismatchedExposureReceiptPreventsProviderDispatch`, `FailedClosureRetainsAttemptUsageAndBlocksLaterWork`, `UnknownClosureRetainsAttemptUsageAndBlocksLaterWork`, `NonAuthorizingOpeningSettlementStopsBeforeToolInvocationAndSubmission`, `NonAuthorizingClosingSettlementStopsBeforeCandidateSubmission`, `AcknowledgedStopSettlementStopsTheProductionChain`, `NonAuthorizingSettlementInContextWorkStopsDeliberatelyWithRetainedUsage` |
| Restricted payload and credential confinement | `RestrictedCanariesTravelOnlyOnTheRestrictedPayloadSurface`, `CredentialCanariesStayOutOfModelToolSavedAndOrdinaryData` |

Focused command, from the repository root after a successful restore and build of the same tree:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.ConsumerProbes.Apr"
```

## Production runtime composition (M2)

The same `AprBusinessHost` consumes the actual production runtime without any new seam: the startup layer composes `RuntimeAgentFactory.Create` over a `RuntimeConfiguration` carrying the independently compiled ScriptedProvider and the narrow CustomTools `CounterTool` and `TransformTool` bindings, then hands the returned `IAgent` and its `ICandidateAgent` face to the Host and invokes the existing direct candidate method. The Host stays Api-only and never sees Runtime, provider or tool types. One `ExecuteCandidatesAsync` invocation runs the full mixed production `ToolCalls` → Final Reject → correction-derived Final accepted with `Continue` → `ToolCalls` → Final accepted with `End`, so five accepted model responses and two real tool batches produce three fresh submissions with one repair and one continuation. Tool output causally determines the accepted item values, the Host correction causally determines the repaired value, and a changed correction changes that repair accordingly.

The following matches the tested composition in `AprHostRunsFiveTurnToolCandidateMixedProductionInOneInvocation`; `steps` is the finite independent ScriptedProvider script and `feedback` is any supplied `ICandidateHost` channel. The snippet runs in the managed runner's Runtime/Consumption test context with the `SolusAgent.Api.Candidates`, `SolusAgent.Api.Execution`, `SolusAgent.Runtime.Api.Configuration`, `SolusAgent.Runtime.Startup` and `AprHost` namespaces imported alongside the implicit `System` namespaces.

```csharp
IAgent agent = RuntimeAgentFactory.Create(
    new RuntimeConfiguration(provider,
        [new RuntimeToolRegistration(counter, counterCapability), new RuntimeToolRegistration(transform, transformCapability)],
        hooks),
    new RuntimeOptions(requireContinuation: true));
var host = new AprBusinessHost(agent, (ICandidateAgent)agent);
var result = await host.ExecuteCandidatesAsync(
    new CandidateExecutionRequest(execution, new CandidateExecutionBounds(3, 1, 1)),
    feedback, progress, cancellationToken);
```

Runtime completion is not business completion: `AprHostAcceptance.EffectCount` stays zero after a `Completed` run and only the explicit `ApplyEffects` Host operation applies each accepted item at most once. Supplied prior context remains unsupported without a context seam on this path, exactly as in M1, so the APR restored execution mode waits for M4 instead of being silently faked.

## Accounting and retry consumption (M3)

`M3AprConformanceTests` composes the same Api-only `AprBusinessHost` with public production startup, the independently compiled ScriptedProvider and actual CounterTool bindings. APR acceptance still belongs to the Host; Runtime accounts for admitted work and attempts without deciding whether an item is useful or applying product effects. No new Scribe proof, shared fixture change or Host assembly reference is required.

The mixed run admits five logical calls and seven physical attempts: a tool turn, an accepted candidate with Continue, a transient failure and physical retry producing a rejected candidate, then a repair with a throttled failure and physical retry producing a tool turn, followed by the repaired candidate accepted with End. Each retry retains its logical identity, inputs, tools, continuation and bounds while obtaining a fresh physical identity and next ordinal. Candidate repair instead admits new logical work, retains the correction once and associates its fresh submission with the rejected submission. Two correction values change the repaired value through actual tool output. The failed attempts contribute measured accounting without becoming completed responses. The first Host effect is explicitly applied before the later failure; neither retry nor repair replays it or the earlier tool invocation.

Paired at-limit/room cases stop or admit the next production independently for logical calls, physical dispatches, observed input/output thresholds and accounting input/output allowances. Earlier accepted receipts, Host acceptance and tool effects survive each stop. Thresholds stop subsequent work after an observed response; reservations govern the next accounting admission. Neither is a strict physical-token or billing ceiling. Missing input, output or both remain nullable measurements. The accounting policy constructor's default Stop halts when a configured comparison needs missing usage; explicit ConservativeCharge and ContinueUnknown permit further work only within a finite physical-dispatch bound. Charge and unresolved exposure retain reservation estimates in separate ledger buckets without inventing measured consumption. Independent Host hooks acknowledge copied, numerically matching accounting snapshots, including reordered finalized entries.

Held provider and held APR feedback cases use caller cancellation and controlled deadlines, each with late success and late failure. Runtime returns while the actual guarded provider exchange or complete APR recording-decorator operation remains held. Earlier accepted progress and explicitly applied effects survive. Returned receipts, usage, accounting and progress remain unchanged after releasing and observing that operation. A sealed provider observation rejects late usage writes. Late correlated feedback may independently increase live Host acceptance while the returned pending receipt remains Unknown; this does not restart Runtime or automatically apply an effect. This is in-process observation proof, with no remote-stop or durable acknowledgement claim.

The following six methods execute 35 cases in `SolusAgent.ContractTests.Runtime.Consumption.M3AprConformanceTests`:

| Obligation | Named executed evidence | Cases |
| --- | --- | --- |
| Mixed tool/candidate production, accounting, retry versus repair, causal correction and explicit effects | `MixedAprRunKeepsRetryRepairAndExplicitEffectsDistinct` | 2 |
| Next-production count, observed-threshold and accounting-allowance boundaries with retained acceptance | `NextProductionLimitsRetainAcceptedAprProgress` | 12 |
| Default Stop, conservative charge and finite unresolved continuation for each missing token axis | `UnknownUsagePolicyPreservesMeasurementsAndFiniteAprContinuation` | 9 |
| Retry consumes physical capacity without readmitting logical work | `RetryUsesPhysicalCapacityWithoutReadmittingLogicalWork` | 2 |
| Unknown or stale numeric settlement blocks retry while retaining prior effects and accounting | `NonAuthorizingSettlementRetainsAprProgressAndCannotRetry` | 2 |
| Held provider/APR Host cancellation and deadline cuts, immutable snapshots and independent late acceptance | `HeldProviderOrAprHostCutReturnsBeforeReleaseAndFreezesSnapshots` | 8 |

Focused command, after a successful restore and build of the same tree:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Runtime.Consumption.M3AprConformanceTests"
```

## Evidence tiers

The consumption obligations below are partitioned by the evidence tier that can actually prove them. M1 rows remain synthetic expressibility through the Api-only producers; the M2 scripted row is actual production runtime composition with the independent ScriptedProvider and real tool batches; the M2 adapter row is controlled real transport owned by #35 and is now published as the generic Api-only candidate-host composition over actual controlled DeepSeek transport in pull request #41, while this leaf's business Host stays untested at that tier. The M3 section above adds scripted production accounting/retry consumption; billing accuracy, restoration and migration remain unproved.

| Obligation | M1 synthetic contract | M2 scripted production composition | M2 adapter-controlled transport (#35) | Later milestones |
| --- | --- | --- | --- | --- |
| Api-only Host over real runtime entrypoints | Host seams only | `AprHostRunsFiveTurnToolCandidateMixedProductionInOneInvocation` | untested | M5 downstream switch |
| Mixed tool/candidate production with causal values and correction metamorphism | `RejectedCandidateIsCorrectedThroughActualToolOutputAndRepairsSubmissionId` | `AprHostRunsFiveTurnToolCandidateMixedProductionInOneInvocation`, `ChangedCorrectionMetamorphicallyChangesTheRepairPayload` | untested | — |
| Episode accounting across intermediate tool turns | scenario productions only | `RepairEpisodeWithIntermediateToolTurnsChargesTheCorrectionAndRepairOnce`, `ContinuationEpisodeWithIntermediateToolTurnsChargesTheContinuationOnce` | untested | — |
| Literal work/submission ceilings around tool turns | `WorkUnitBoundStopsResourceLimitBeforeTheNextEffect` | `WorkUnitCeilingIsLiteralBeforeEveryModelAdmissionAfterToolTurns`, `SubmissionCeilingDoesNotCountToolTurnsAndEndSucceedsAtExactCeilings`, `ZeroFollowOnAllowanceBlocksBeforeAnyNewProviderOrToolEffects` | untested | — |
| Retained acceptance/usage under later stops and batch rejection | `MissingAcknowledgementAfterAcceptedProgressStopsWithoutFurtherWorkOrReplay` and neighbors | `EarlierHostAcceptanceAndUsageSurvivePostAcceptanceStops`, `LaterBatchWithInvalidLastMemberYieldsZeroNewEffectsWhileEarlierEffectsAndAcceptanceRemain`, `NonAuthorizingClosureBlocksNewToolEffectsAndCandidateSubmission`, `FailedToolAfterEarlierAcceptedBatchRetainsPriorEffectsAndNeverReplays`, `HeldToolOrProviderReturnsAtCutBeforeReleaseAndLateCompletionChangesNothing` | untested | — |
| Full history, continuation and association | association probes | `ContinuationReplayAndClassifiedHistoryStayFullyAssociatedAcrossToolAndCandidateTurns` | untested | — |
| Confinement and isolation | `RestrictedCanariesTravelOnlyOnTheRestrictedPayloadSurface`, `CredentialCanariesStayOutOfModelToolSavedAndOrdinaryData` | `RestrictedAndCredentialCanariesStayOffEverySafeSurface`, `TrustedInstructionAndInstalledBindingsStayImmutableDespiteInstructionLikeData`, `EqualExecutionIdsAcrossConcurrentHostRunsKeepHistoriesAndReceiptsIsolated` | untested | — |
| Fresh versus restored execution | `SuppliedPriorContinuationAdmitsWorkAndCapturesToolDerivedRestrictedState`, `RejectedInvalidContextStopsBeforeWorkCaptureEffectsAndCandidateWork` | `AprContextRequiredRunRemainsUnsupportedWithoutAContextSeam` | untested | M4 actual restoration, including the selected APR mode |
| Real provider transport and parsing | — | — | generic candidate-host composition over controlled DeepSeek transport proved by #35 (PR #41); literal AprHost transport untested | M5 migration proof |
| Provider count/token admission, accounting policy and physical retry | — | M3: `M3AprConformanceTests`, including retained APR progress and held-operation cuts | untested | No new tool allowance conformance claim; V2/V3 own those consumer obligations |
| Billing accuracy and strict physical-token ceilings | — | unproved | unproved | separate evidence required |
| Packaging and downstream migration | — | — | — | M5 |

Focused command, from the repository root after a successful restore and build of the same tree:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Runtime.Consumption"
```

## Later milestone limits

The M1 fixtures above do not invoke the production [provider-turn runtime](runtime-execution.md) or prove a real model adapter, provider transport or runtime tool adapter; the guarded provider script is not adapter conformance proof. The M2 composition runs the actual production runtime and tool batches, but its ScriptedProvider remains synthetic scripted transport: no real model adapter, provider transport or adapter conformance is proved here, and the controlled DeepSeek transport evidence for the generic candidate-host composition is published with #35 in pull request #41 while literal business-Host transport remains unproved here. M3 proves APR consumption of implemented provider count/token admission, accounting policy and provider-only retry on this scripted production path. It does not establish billing accuracy, strict physical-token ceilings, remote cancellation, new tool allowance conformance, persistence or recovery. M4 restoration remains unproved here: the supplied context grammar is in-memory expressibility only, with no complete new-process restoration, durable codec, integrity protection or crash recovery, and the APR restored execution mode waits for that milestone. M5 and actual downstream migration remain unproved: no product acceptance implementation, publication, release, packaging or downstream consumer switch is demonstrated. M1 proves synthetic Host expressibility, M2 proves mixed production consumption and M3 adds accounting/retry conformance; none proves product or release behavior.
