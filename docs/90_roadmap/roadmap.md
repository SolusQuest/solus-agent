# Roadmap to the first consumable prerelease

Status: planning baseline. M0 is the existing initialization; M1-M5 are planned outcomes, not implemented behavior. This roadmap selects delivery scope and sequencing without freezing interface signatures, saved formats, provider dependencies, package layout, or a release version.

## Target and ownership

Deliver a versioned 0.x experimental prerelease that agentic-pr-review (APR) and ContractScribe can pin to begin migration. It includes the application-facing API, reusable tool API, self-owned runtime extension API, the managed runtime, one actual model-provider adapter, budget and usage mechanics, and complete runtime-context saving and restoration.

The release boundary is downstream consumability. Actual migration, product acceptance, product defaults, and downstream releases remain owned by each downstream repository. The first SolusAgent release does not establish that either product has migrated or that the public APIs are stable.

Follow the selected [architecture](../20_architecture/architecture.md), [project structure](../20_architecture/project-structure.md), [security boundary](../20_architecture/security-boundary.md), [contract lifecycle](../00_project/contract-lifecycle.md), and [pre-release engineering](../shared/standards/pre-release-engineering.md).

## Milestone overview

| Milestone | Independently acceptable outcome | State |
| --- | --- | --- |
| M0: Initialization | Project rules and four buildable library skeletons with the selected dependency graph. | Existing baseline |
| M1: Draft contracts and consumption | Both consumer patterns can express their needs through executable draft contracts. | Planned |
| M2: Runtime execution | A complete agent/provider/tool/Host feedback path works through the public startup surface. | Planned |
| M3: Budgets, usage, and failure semantics | Resource admission, accounting, retries, cancellation, and termination are coherent across execution paths. | Planned |
| M4: Complete context restoration | A new process can explicitly restore supported runtime context and continue with capabilities supplied again by the Host. | Planned |
| M5: Consumption validation and prerelease | Versioned release artifacts can be installed independently and support the verified migration-entry scenarios. | Planned |

These milestones group outcomes, not mandatory counts of issues, pull requests, reviews, or implementation phases. Each behavior ships with its necessary tests, fixtures, and documentation. Establish ordinary CI with the first executable contracts and evolve it with the behavior; do not postpone validation infrastructure until M5.

Security is an acceptance condition throughout M1-M5. As each affected path is implemented, use focused synthetic checks for credential/capability confinement, instruction-versus-data classification, actual tool arguments, and separation of candidate delivery and restricted state from ordinary diagnostics. These checks follow the owning behavior rather than adding a security certification phase.

## M0: Initialization

The current source contains `SolusAgent.Api`, `SolusAgent.Tools.Api`, `SolusAgent.Runtime.Api`, and `SolusAgent.Runtime` as `net10.0` library skeletons. The independent APIs have no project references; `Runtime.Api` references those APIs; `Runtime` references all three APIs. Managed execution is the default.

M0 establishes repository structure and selected boundaries only. It supplies no public API types, execution loop, providers, tools, test suite, CI, packages, or released distribution. Build success is not evidence of those behaviors.

## M1: Draft contracts and consumption

**Outcome:** APR and ContractScribe consumption patterns can be expressed naturally without moving their product types or authority into the shared APIs.

Develop the minimum executable draft contracts for agent requests, progress and outcomes, candidate submission and Host acknowledgement, capabilities, usage, and implementation-scoped context envelopes. Define shared tool metadata, input preparation, invocation, and results separately from runtime-specific provider exchange and configuration.

Candidate/Host acknowledgement, budget/usage, context envelopes, and provider exchange remain drafts. Specify enough accounting and restoration semantics to guide M2-M4, then revise contracts with their producers, consumers, tests, and documentation as implementation provides evidence. M1 does not freeze the public API or require speculative support for future backends.

Distinguish correlated candidate submissions and Host acknowledgements, runtime completion, and product effects. Define bounded repair/continuation, preservation of acknowledged progress, partial or incomplete outcomes, and missing, failed, or unknown acknowledgements. Completion does not imply product acceptance or publication, and a resource stop is not ordinary successful completion. The draft also anticipates the Host's pre-dispatch exposure-recording and subsequent settlement needs without assigning storage or a product transaction engine to SolusAgent.

**Exit evidence:** synthetic consumer-shaped probes can express APR-style execution with supplied prior context and ContractScribe-style fresh execution from validated business progress, including candidate feedback. Compilation demonstrates that business orchestration uses `Api`, reusable tools use `Tools.Api`, and custom model adapters use `Runtime.Api` without the runtime implementation. Invalid requests and unsupported required capabilities have explicit behavior.

**Dependency:** M0. M2 implementation can feed back into the same draft contracts; these milestones do not impose a permanent design-before-code barrier.

## M2: Runtime execution

**Outcome:** the self-owned runtime executes a complete task through its public construction and execution surfaces.

Implement the model/tool loop, call association, generic tool adaptation, preparation before execution, result recording, and intermediate candidate submission with Host accept/reject/continue/end feedback. Include finite supported execution bounds, bounded inputs/results, cancellation observation, and truthful failure outcomes from the first executable loop. Preparation performs no tool effects; define batch admission before enabling batch execution. Provider retry does not authorize automatic retry of tools or Host effects. Product validation and final side effects remain in the Host or downstream tools.

Keep two provider paths:

- A Scripted Provider supports ordinary CI and deterministic runtime tests. Loop correctness has no network, credential, or particular-provider prerequisite.
- One Actual Provider supplies a production adapter, including provider projection, transport, response/error and usage mapping, and required continuation. Select one bounded provider/profile scope from the real consumer requirements. Test that actual adapter's real serialization, HTTP transport, and parsing with fake transport and synthetic wire fixtures; any live integration execution is separately authorized and bounded. Scripted, transport-level, and live evidence establish different claims; a familiar wire format does not establish support for every service using it.

The actual adapter belongs to M2, but it is not a prerequisite for developing or validating the generic loop. Decide its project/package placement from its real dependency boundary instead of adding projects merely to reserve names.

**Exit evidence:** scripted end-to-end execution covers multiple model/tool turns, candidate rejection and correction, acceptance with continued work, and explicit termination. The actual adapter has executable request/response, tool association, error, and continuation mapping evidence. A generic adapter handles compatible tool metadata without a handwritten mapping per tool name. Unsupported schemas or capabilities fail explicitly.

**Dependency:** the applicable M1 contracts. M3 and M4 requirements inform execution records from the start; their complete guarantees are established in their own milestones.

## M3: Budgets, usage, and failure semantics

**Outcome:** supported runtime limits and usage observations remain coherent on success, failure, retry, timeout, and cancellation.

Implement run-level admission, reservations, settlement, balance, and exhaustion for supported limits. Distinguish logical model calls from physical dispatches and retries. Every retry has new admission and accounting; a failed or interrupted dispatched request cannot disappear from exposure merely because no result arrived.

Keep configured limits, known actual usage, unknown consumption, in-flight reservations, conservative charges, and estimated cost distinct. Preserve complete, partial, and unavailable observations. Cache and reasoning counters retain their provider-defined meaning and are not fabricated or double-counted. Optional estimated cost does not claim provider billing accuracy. Products still own trusted defaults, rate policy, presentation, and cross-invocation or campaign accounting.

Document which limits can be enforced before dispatch, which are post-response stopping thresholds, and how unknown exposure affects further admission. Cancellation observation does not prove remote work stopped. Preserve validated usage when later response or candidate validation fails. Provide an ordered Host integration seam for recording exposure before dispatch and receiving subsequent settlement; where the Host requires durable pre-dispatch recording, external work waits for that acknowledgement. A final-result-only reporting interface cannot fulfill this requirement. SolusAgent owns the execution/accounting mechanics, while the Host owns durable claims and storage.

**Exit evidence:** synthetic dispatch and failure scenarios distinguish pre-dispatch rejection, successful settlement, failed attempts, retry admission, lost responses, partial or missing usage, cancellation races, and deadline exhaustion. Unsupported enforcement guarantees are reported honestly. Normal stops and controllable failures return the supported observations and stop reason; abrupt process death is not promised a final result envelope.

**Dependency:** M2 execution and provider paths. Applicable regressions are carried from the existing consumers rather than replaced with an unrelated accounting design.

## M4: Complete context saving and restoration

**Outcome:** at documented supported checkpoint boundaries, a new process can restore the complete runtime-owned state necessary to continue execution correctly.

Save logical conversation and tool records, candidate/Host feedback needed for continuation, necessary accounting state, and required provider-scoped continuation. Define checkpoint and unresolved-call behavior, distinguish historical observations from new-run usage, and make the enforcement scope across restoration explicit. Do not silently reset an existing enforced budget or automatically replay an uncertain side effect.

Distinguish resuming an unfinished run from a new Host-authorized run using previous context. Explicitly account for unresolved provider calls, tool operations, and Host acknowledgements. Capacity exhaustion is an explicit stop, not permission for silent truncation, compaction, or fresh-session fallback.

Validate context integrity, record association, implementation identity, format, and applicable provider/model compatibility before restored data affects execution. Incompatible or invalid state fails explicitly. Fresh execution is a separate caller decision. Preserve instruction-versus-data classification through recording, restoration, and provider projection.

Credentials, live clients, delegates, transports, and tool instances are excluded. The Host supplies capabilities again and owns storage, protection, retention, and deletion. Restricted continuation and context do not appear in ordinary diagnostic events or public outcomes. Complete runtime restoration neither promises arbitrary mid-operation recovery nor supplies product campaign checkpoints or cross-backend conversion.

**Exit evidence:** synthetic new-process round trips continue through the real runtime and actual provider adapter under controlled transport, verifying reconstructed requests, conversation/tool/continuation associations, candidate feedback, and accounting continuity; serialization round trips alone are insufficient. Invalid, incompatible, and tampered records are rejected. Restoration does not duplicate completed calls or charge historical usage as new work. Secret and restricted-state canaries remain absent from ordinary public surfaces. APR can choose restoration, while ContractScribe can choose a new conversation reconstructed from business progress.

**Dependency:** M2 execution records and M3 accounting semantics. Context design is refined earlier, but completion follows the behavior it must faithfully restore.

## M5: Consumption validation and experimental prerelease

**Outcome:** APR and ContractScribe can pin released artifacts and begin their own migration work.

Validate both representative consumption patterns against the public interfaces using synthetic fixtures. Build versioned NuGet artifacts and install them into clean consumer projects without sibling source checkouts, linked product files, private machine paths, or unpublished runtime internals. Confirm the application startup surface, custom tools, custom providers, candidate feedback, budget/usage, and restored-versus-fresh execution paths supported by the release.

The APR-shaped scenario includes product-owned tools and acceptance, restored context with required provider continuation, and incomplete/failure outcomes. The Scribe-shaped scenario includes multiple submissions with independent Host acknowledgements, bounded repair and partial progress, and fresh execution from product-owned progress. The same scenario obligations guide M1 probes and subsequent implementation checks; they are consumer conformance, not full downstream product acceptance.

Record usage instructions, package/dependency layout, the prerelease compatibility policy, and the actually verified framework, platform, provider, and feature support scope. Resolve the license, distribution destination, version, and release authority required for the chosen distribution. Qualify the concrete consumer host environments in the declared scope; do not infer additional platform support from a build on one machine.

The first release is a **0.x experimental prerelease**. Consumers pin versions, and released artifact identities remain fixed. API, package, and context-format changes can still be coordinated with the two actual consumers; this milestone does not promise strong backward compatibility or a compatibility freeze. Document breaking changes and context acceptance rules explicitly, and honor any actual retained-state or support commitment under [contract lifecycle](../00_project/contract-lifecycle.md). API/package compatibility and saved-context compatibility need not share one version boundary. Reassess a stable compatibility commitment after both downstream products have completed at least one actual migration round; that observation does not automatically freeze every boundary.

**Exit evidence:** independent clean consumers successfully restore and run the supported package paths; deterministic tests and actual-adapter fake-transport tests pass at the release revision; any required bounded live or target-platform evidence is identified separately with its actual result. The selected prerelease artifacts are published through a maintainer-authorized release and match the validated revision. Ordinary PR and push CI requires no model credentials.

**Dependency:** M1-M4 guarantees and the concrete distribution decisions. Package work can begin earlier, but packaging alone cannot close the milestone.

## Reuse and extraction strategy

Use the existing APR and ContractScribe implementations as starting material for generic mechanics and regressions. Inspect their current maintained revisions during extraction and record the source revision and license basis for adopted code. Existing product acceptance proves behavior only at its original revision and conditions; SolusAgent validates the adapted implementation.

| Source | Candidate reuse | Ownership retained downstream |
| --- | --- | --- |
| APR | Loop mechanics, provider transport and continuation, dispatch/retry accounting, resource limits, deadlines, session records and restoration, applicable synthetic regressions. | PR identity and reviewed snapshots, findings/evidence validation, review-specific terminal semantics, publication eligibility, and GitHub mutations. |
| ContractScribe | Tool exchange and preparation boundaries, structured submissions, validation feedback, cancellation/failure handling, resource observations, applicable synthetic regressions. | Target and proposal semantics, Roslyn evidence, deterministic patches, business checkpoints, campaign state/accounting, and GitHub lifecycle. |
| Both consumers | Concrete constraints on shared execution, provider/tool extensions, usage semantics, and consumer-facing candidate/context contracts. | Trusted configuration, domain acceptance, storage policy, final side effects, and downstream migration/release decisions. |

Do not copy either entire product runtime or `ContractScribe.Core` into a shared API. Carry tests with the generic behavior, minimize fixtures, and exclude private downstream source, live prompts/replies, transcripts, hidden reasoning, and machine-local state. ContractScribe M7 requirements supply design inputs; unimplemented M7 behavior is not existing reusable implementation.

## Sequencing and migration boundary

The acceptance sequence is M0 -> M1 -> M2 -> M3 -> M4 -> M5. Work inside that sequence can overlap where dependencies are understood: draft contracts evolve with execution, accounting and context records are designed early, and consumer probes and packaging checks can inform implementation before M5.

M2 enables local integration experiments. M4 establishes the runtime behaviors needed by the selected consumer patterns. M5 supplies the versioned artifact boundary for formal downstream migration planning and execution. Representative consumer probes protect the shared contract; they do not require completing both product migrations as a SolusAgent release gate.

Budgets, honest usage, candidate acknowledgement, and complete runtime restoration are included in the first prerelease so downstream products do not need a second generic runtime to replace missing shared mechanics. ContractScribe can still choose fresh conversations and preserve its prohibition on full context in consumer coordination history.

Later work may add other whole-agent backends, additional providers, source generators, remote tool transports, dynamic discovery, convenience hosting, broader platform support, or a stable release. These require concrete consumer needs. Native AOT, automatic context compaction, product campaign state, product acceptance, and platform publication logic are not first-prerelease runtime requirements.

Exact public signatures, codecs and compatibility discriminators, budget constants, retry policy, provider package placement, release version/feed, and detailed downstream migration graphs remain decisions of their owning implementation or release work. Refine durable external, persisted-state, security, or distribution choices with the bounded precedent check required by pre-release engineering; this roadmap does not freeze those designs.
