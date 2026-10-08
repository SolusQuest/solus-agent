# APR-shaped draft consumption

The test-only `AprHost` library is a non-packable Api-only synthetic business Host: it references `SolusAgent.Api` as its only production assembly and receives the outer `IAgent` seam with its optional `ICandidateAgent` and `IContextAgent` seams, the current `AgentRequest` controls, a deliberately supplied `ICandidateHost` feedback channel and a separately supplied call-scoped `IRestrictedContextSink`. The sole test runner owns the finite APR scenario and startup composition under `tests/SolusAgent.ContractTests/ConsumerProbes/Apr`: one actual `CustomTools` `CounterTool` with its `CounterCapability`, one actual guarded `CustomProvider` provider, the existing `RuntimeConfiguration`, `ConfigurationConsumer` and Host exposure hooks, wired into the business Host over outer interfaces only. This is an executable M1 consumption example with no runtime implementation and no provider transport; the M2 composition below adds actual production runtime execution on scripted transport. Neither tier carries product source, PR identity, findings or evidence policy, GitHub authority, packaging or downstream migration.

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

## Evidence tiers

The consumption obligations below are partitioned by the evidence tier that can actually prove them. M1 rows remain synthetic expressibility through the Api-only producers; the M2 scripted row is actual production runtime composition with the independent ScriptedProvider and real tool batches; the M2 adapter row is controlled real transport owned by #35 and stays pending until that work publishes its own proven result. M3, M4 and M5 rows name later obligations that nothing in this leaf proves.

| Obligation | M1 synthetic contract | M2 scripted production composition | M2 adapter-controlled transport (#35) | Later milestones |
| --- | --- | --- | --- | --- |
| Api-only Host over real runtime entrypoints | Host seams only | `AprHostRunsFiveTurnToolCandidateMixedProductionInOneInvocation` | pending | M5 downstream switch |
| Mixed tool/candidate production with causal values and correction metamorphism | `RejectedCandidateIsCorrectedThroughActualToolOutputAndRepairsSubmissionId` | `AprHostRunsFiveTurnToolCandidateMixedProductionInOneInvocation`, `ChangedCorrectionMetamorphicallyChangesTheRepairPayload` | pending | — |
| Episode accounting across intermediate tool turns | scenario productions only | `RepairEpisodeWithIntermediateToolTurnsChargesTheCorrectionAndRepairOnce`, `ContinuationEpisodeWithIntermediateToolTurnsChargesTheContinuationOnce` | pending | — |
| Literal work/submission ceilings around tool turns | `WorkUnitBoundStopsResourceLimitBeforeTheNextEffect` | `WorkUnitCeilingIsLiteralBeforeEveryModelAdmissionAfterToolTurns`, `SubmissionCeilingDoesNotCountToolTurnsAndEndSucceedsAtExactCeilings`, `ZeroFollowOnAllowanceBlocksBeforeAnyNewProviderOrToolEffects` | pending | — |
| Retained acceptance/usage under later stops and batch rejection | `MissingAcknowledgementAfterAcceptedProgressStopsWithoutFurtherWorkOrReplay` and neighbors | `EarlierHostAcceptanceAndUsageSurvivePostAcceptanceStops`, `LaterBatchWithInvalidLastMemberYieldsZeroNewEffectsWhileEarlierEffectsAndAcceptanceRemain`, `NonAuthorizingClosureBlocksNewToolEffectsAndCandidateSubmission`, `FailedToolAfterEarlierAcceptedBatchRetainsPriorEffectsAndNeverReplays`, `HeldToolOrProviderReturnsAtCutBeforeReleaseAndLateCompletionChangesNothing` | pending | — |
| Full history, continuation and association | association probes | `ContinuationReplayAndClassifiedHistoryStayFullyAssociatedAcrossToolAndCandidateTurns` | pending | — |
| Confinement and isolation | `RestrictedCanariesTravelOnlyOnTheRestrictedPayloadSurface`, `CredentialCanariesStayOutOfModelToolSavedAndOrdinaryData` | `RestrictedAndCredentialCanariesStayOffEverySafeSurface`, `TrustedInstructionAndInstalledBindingsStayImmutableDespiteInstructionLikeData`, `EqualExecutionIdsAcrossConcurrentHostRunsKeepHistoriesAndReceiptsIsolated` | pending | — |
| Fresh versus restored execution | `SuppliedPriorContinuationAdmitsWorkAndCapturesToolDerivedRestrictedState`, `RejectedInvalidContextStopsBeforeWorkCaptureEffectsAndCandidateWork` | `AprContextRequiredRunRemainsUnsupportedWithoutAContextSeam` | pending | M4 actual restoration, including the selected APR mode |
| Real provider transport and parsing | — | — | pending, owned by #35 | M5 migration proof |
| Budget engine, retry policy, billing accuracy | — | — | — | M3 |
| Packaging and downstream migration | — | — | — | M5 |

Focused command, from the repository root after a successful restore and build of the same tree:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Runtime.Consumption"
```

## Later milestone limits

The M1 fixtures above do not invoke the production [provider-turn runtime](runtime-execution.md) or prove a real model adapter, provider transport or runtime tool adapter; the guarded provider script is not adapter conformance proof. The M2 composition runs the actual production runtime and tool batches, but its ScriptedProvider remains synthetic scripted transport: no real model adapter, provider transport or adapter conformance is proved here, and the controlled DeepSeek transport evidence is owned by #35 and remains pending. M3 remains unimplemented: no production budget engine, accounting enforcement, billing accuracy or strict token ceiling exists here; the finite scenario bounds and the fixture consumer's small script capacity are documented test policy, not production enforcement, storage or recovery. M4 remains unimplemented: the supplied context grammar is in-memory expressibility only, with no complete new-process restoration, durable codec, integrity protection or crash recovery, and the APR restored execution mode waits for that milestone. M5 and actual downstream migration remain unproved: no product acceptance implementation, publication, release, packaging or downstream consumer switch is demonstrated. M1 proves that one synthetic APR-shaped Host can consume the accepted drafts with product-owned acceptance, honest usage and restricted confinement, and M2 proves the same Host drives the actual runtime through one mixed production; neither proves product or release behavior.
