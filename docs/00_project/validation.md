# Project validation

Use shared [Validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/validation.md) and [Test validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/test-validation/SKILL.md) with these SolusAgent commands and acceptance limits.

## Current available checks

The solution contains four core production libraries and the optional DeepSeek adapter, the separate compiled Api-only custom-agent, CustomTools, CustomProvider, ScribeHost and AprHost consumer libraries, and one managed test runner, `tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj`. Api and Tools.Api implement their current execution and function-tool drafts; Runtime.Api contains the provider exchange draft; Runtime implements bounded production provider and tool turns. From the repository root, use the SDK selected by `global.json`:

```text
dotnet restore SolusAgent.slnx
dotnet build SolusAgent.slnx --configuration Release --no-restore
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build
```

The runner uses xUnit through `Microsoft.NET.Test.Sdk`, `xunit` and `xunit.runner.visualstudio` with private runner assets. It references `SolusAgent.Api`, `SolusAgent.Tools.Api`, `SolusAgent.Runtime.Api`, `SolusAgent.Runtime` and the five test-only consumer libraries for actual behavioral tests. The separately compiled Api-only consumer references only Api and has no packages; CustomTools references only Tools.Api and has no packages; CustomProvider references only Runtime.Api directly, without Runtime or packages; ScribeHost references only Api and has no packages; AprHost references only Api and has no packages. Execution and Usage tests invoke the actual consumer/agent through `IAgent`. Architecture tests evaluate the real production and consumer project files through `dotnet msbuild` JSON output, including imported and conditioned items. Future focused tests use this same runner and add only dependencies required by implemented test code. Api, Tools.Api, Runtime.Api and Runtime generate XML documentation with warnings-as-errors during the normal build.

Focused runs select the same runner with a filter, for example:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Architecture"
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Execution"
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Usage"
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Tools"
```

The Architecture tests check the exact four-core-library reference graph plus the optional Runtime.Api-only DeepSeek adapter, package independence of `SolusAgent.Api` and `SolusAgent.Tools.Api`, exact current solution/test dependencies, managed `net10.0` targets and evaluated compile inputs that stay inside the repository. CustomTools must have Tools.Api as its sole production edge and no packages; CustomProvider must have Runtime.Api as its sole direct production edge, no Runtime edge/packages, and repository-contained sources; ScribeHost must have Api as its sole production edge, no packages, and compile inputs confined to its own project directory. Synthetic negative checks mutate an owned temporary Api-only consumer with forbidden `Runtime` and `Runtime.Api` reference edges and an out-of-root linked source, then require the same boundary assertions to fail for the specific offending edge. Confirm nonzero executed test counts and named negative checks; an empty `dotnet test` run is not acceptance evidence.

Execution tests demonstrate the current outer contract through the actual Api-only consumer: bounded normal/partial/resource/cancel/failure outcomes, pre-work invalid/unsupported rejection, correlation, immutable control/data separation and restricted diagnostic canaries. They check the consumer's evaluated exact Api-only graph and compiled assembly references. The [execution draft](../20_architecture/drafts/agent-execution.md) states these guarantees and limits.

Usage tests exercise the [usage draft](../20_architecture/drafts/usage.md) through the actual Api-only scripted agent and Host consumer: nullable core/provider facts, retry association, retained exposure/measurement after validation failure, cancellation and observer failure, count admission and post-response stopping with unknown/overflow neighbors. These are synthetic proof, not production budget enforcement or billing accuracy.

Tools tests run the actual synthetic consumer through public contracts and prove bounded metadata/schema/arguments/results, zero preparation effects, explicit narrow capability admission, call/output association, single-use concurrent invocation and honest cancellation/failure outcomes. The [function-tool draft](../20_architecture/drafts/function-tools.md) states these guarantees and limits. Run the full small suite after shared signature or registration changes.

The synthetic M1 checks alone do not demonstrate production runtime enforcement. The separate Runtime.Execution checks below cover the first production provider turn and its duration/cancellation cut; the optional adapter checks cover actual controlled HTTP transport, and the M3 Runtime.Consumption suites below cover selected production accounting, retry and tool-allowance enforcement under scripted transport. Live provider interoperability, product tool adapters, billing accuracy, durable campaign accounting, context restoration, packaging, installation, production external effects and release readiness remain unproved. A passing local run does not establish CI results or platform support. Inspect solution membership and direct references against [Project structure](../20_architecture/project-structure.md).

Use `--no-restore` only after a successful applicable restore; repeat restore after SDK, framework, project, reference, package, source, or restore-property changes. Use `--no-build` only after a successful build of the same inputs, SDK, configuration, and tree; rebuild after source, project, solution, build configuration, or generator changes.

Candidates tests exercise the actual Api-only producer and Host consumer through `ICandidateAgent`: independent correlation/decisions, rejected corrections and accepted continuation, explicit termination, partial progress, bounded follow-on admissions, missing/failed/unknown feedback, wrong/duplicate association, pending cancellation and late-response non-admission. They also prove pre-work required-capability rejection, observer-failure ordering, immutable safe receipts and payload/correction/error canary confinement. Use the same runner with `--filter "FullyQualifiedName~SolusAgent.ContractTests.Candidates"` for a focused run. The [candidate-feedback draft](../20_architecture/drafts/candidate-feedback.md) states the exact synthetic policies and limits; in-memory Host effects are independently observed and never replayed automatically.

Providers tests call the actual Runtime.Api-only CustomProvider producer/Host consumer and guarded exchange: two model turns, two fully correlated real tools admitted as a whole batch, exact required continuation, invalid shapes/associations, retained usage after payload rejection/failure/cancellation, known/unknown dispatch, capture closure/concurrency, finite count/byte neighbors and diagnostic canaries. Use the same runner with `--filter "FullyQualifiedName~SolusAgent.ContractTests.Providers"`. The [provider exchange draft](../20_architecture/drafts/provider-exchange.md) states current semantics and M2/M4 deferrals; these are synthetic expressibility checks, not actual transport/parser/restoration proof. The optional DeepSeek adapter supplies separate actual transport/parser evidence below.

Context tests exercise the actual Api-only producer/Host consumer for explicit intent, implementation/format/compatibility/grammar/transition rejection before effects, no fresh fallback, Host control/capability reinjection, immutable restricted copies and safe canary confinement. Call-scoped capture and overlapping same-correlation sinks demonstrate Host retention without agent lookup; capture failure stays separate from work outcomes. These checks cover the [context envelope draft](../20_architecture/drafts/context-envelope.md), not production restoration or a durable codec. Use the existing runner:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Context"
```

RuntimeConfiguration tests exercise the actual independent CustomProvider configuration consumer, base-derived and interface-only tools, full acknowledgement/settlement association, held callbacks, explicit retries, cancellation cuts, retained usage after failure, optional Host controls, finite registry/count neighbors and canary confinement. They prove the [runtime configuration and exposure draft](../20_architecture/drafts/runtime-configuration.md) in memory, without production storage, strict work/duration/token enforcement, automatic retry or a restoration codec. Use the existing runner:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.RuntimeConfiguration"
```

The `ConsumerProbes.Scribe` tests exercise the independent Api-only ScribeHost business Host with the runner's `ConsumerProbes/Scribe` startup composition: explicit fresh reconstruction from validated progress, independent accept/reject acknowledgement with bounded repair, missing/unknown/held delivery without replay, actual provider/tool/configuration startup with ordered exposure and same-attempt settlement, retained known/unavailable usage, an independent outer usage producer and restricted diagnostics confinement. These checks prove the M1 synthetic tier of the [Scribe consumption draft](../20_architecture/drafts/scribe-consumption.md); they do not themselves prove a production loop, budget enforcement, real provider transport, restoration, product acceptance, M7 behavior or downstream migration. Separate M2/M3 production consumption evidence is described under Runtime.Consumption below. Use the same runner with the focused filter first:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.ConsumerProbes.Scribe"
```

The `ConsumerProbes.Apr` tests exercise the Api-only APR-shaped business Host and the finite test-only scenario that composes the actual CustomTools counter tool and the actual guarded CustomProvider provider through `RuntimeConfiguration`, `ConfigurationConsumer` and the Host exposure hooks. They cover supplied prior context admission and rejection, correction through `RepairsSubmissionId`, accepted continuation with explicit completion, partial/resource/cancellation stops with retained progress, missing/unknown/failed/wrong feedback, whole-batch admission before effects, honest known/partial/unavailable usage, ordered exposure and same-attempt closure, and restricted payload and credential confinement, plus the AprHost evaluated and compiled boundary. These checks are synthetic M1 consumption evidence under the [APR-shaped draft consumption document](../20_architecture/drafts/apr-consumption.md); they do not themselves prove production runtime or budget enforcement, restoration or downstream migration. Separate M2/M3 production consumption evidence is described under Runtime.Consumption below. Use the existing runner:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.ConsumerProbes.Apr"
```

## DeepSeek adapter

The optional [DeepSeek provider draft](../20_architecture/drafts/deepseek-provider.md) has owning tests in the existing runner. They execute the real writer/invoker/reader/parser/guard with controlled handlers, actual generic tool preparation/results and all historical replay. Negatives cover finish/identity/schema/association, bounded JSON/Unicode, usage retention, cancellation cuts, disposal, concurrent calls and wire/logical ceilings. Actual observation integration checks cover single-use/presealed requests, external sealing during held send/read and frozen earlier measurements without late payload acceptance. Evaluated and compiled assertions require Runtime.Api as the adapter's sole direct production edge, with no packages or Runtime dependency. Run the owning tests first:

```powershell
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.DeepSeek.Adapter"
```

Synthetic TLS tests use the actual production handler factory with a test-only local route and pinned synthetic certificate, count one physical connection/request for success, premature EOF, 500 and 307, and check standard EventSource/DiagnosticListener/ActivityListener confinement. A separate inner-handler throw tests exception sanitization before invoker telemetry. These require no provider secret, installed trust certificate, live provider, sibling source or new runner. Arbitrary Host executable logging and privileged raw internal tracing are outside ordinary diagnostics. Run affected Providers/Usage/Architecture checks and the full small suite after registration changes; live service support, remote stop, billing, streaming and durable restoration remain unverified.

DeepSeek.RuntimeIntegration tests compose the actual adapter with the production runtime through public startup under controlled HTTP transport: one continuous mixed candidate run with real Counter/Transform tool rounds, exact historical replay and correction accounting, composition failure neighbors, exposure and settlement gating, late-completion cut classes observed to actual provider or hook completion, and exact mixed work/attempt ceilings. They run the existing Api-only candidate consumer and scripted Host with synthetic data and no provider secret, live service, billing, remote stop, restoration or product migration. Run the owning suite first:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.DeepSeek.RuntimeIntegration"
```

The M3 actual-adapter conformance additions are `RetryCompositionTests`, `RetryAccountingCompositionTests` and `RetryCutCompositionTests`. They prove real HTTP failure classification driving admitted production retries; unchanged retry bodies and logical identity with distinct physical attempts; retained failed-attempt measurements and per-axis accounting under Stop, ConservativeCharge and ContinueUnknown; typed response loss and malformed-content neighbors; exact dispatch, inventory, token, accounting and whole-tool-batch admission; validated closure and fresh permission; and controlled backoff/deadline/cancellation with immutable late snapshots. Nonzero earlier tool and Host effects make the provider-only replay checks observable. Attempt inventory, actual handler sends, tool invocation-interface counts and guarded business effects are separate assertions. These tests extend selected production enforcement evidence under controlled HTTP; they do not qualify live interoperability, billing, remote stop, restoration or APR/Scribe product migration.

After the owning suite, run the related adapter/runtime/usage/provider regressions and the complete runner, using the successful restore and Release build above:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.DeepSeek.Adapter|FullyQualifiedName~SolusAgent.ContractTests.Runtime|FullyQualifiedName~SolusAgent.ContractTests.Usage|FullyQualifiedName~SolusAgent.ContractTests.Providers"
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build
```

Record actual executed counts and distinguish local Windows evidence from the exact pushed head's Linux CI. The existing adapter and scripted Runtime suites supply broader component matrices; neither alone substitutes for these actual-adapter composition scenarios.

## Production runtime checks

Runtime.Execution tests invoke the production public factory and `IAgent` with the independently compiled finite Scripted Provider. They prove whole-run deadline/cancellation cuts while operations remain held, atomic retained observation, same-attempt permission/settlement, original-request payload revalidation, capacity and UTF-8 neighbors, closure/progress failures, canary confinement and concurrent same-identity isolation. A closed settlement phase matrix covers invocation, obtained result, local stop, exposure and measurement availability. Real internal two-turn operation tests demonstrate work/attempt admission, required continuation replay and non-vacuous Stop/uncertain-closure gating; tool execution now uses the owning Runtime.Tools integration tests; Runtime.Candidates owns separate public candidate execution proof. The [runtime execution draft](../20_architecture/drafts/runtime-execution.md) owns the implemented support and bounded closure allowance. Run focused tests first:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Runtime.Execution"
```

Run the full applicable suite after draft, reference, registration or shared-state changes. Runtime.Tools tests invoke actual CounterTool and interface-only TransformTool through public startup across multiple provider turns. They cover complete all-member argument/capability admission, immediate partial records, unknown held invocation, fixed failures, original-descriptor result validation, required replay, complete/missing/duplicate/foreign/recycled/reordered associations, exact record/retention/result/batch/input/attempt/work neighbors, concurrent same-ID runs and ordinary-data confinement. Actual internal batch-operation tests supplement the public cases by inspecting restricted partial snapshots. The shared-entry integration pair runs tool turns and candidate repair on the same factory instance, with the same Host execution ID in either order, and verifies independent history, continuation, effects and usage. Run `dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Runtime.Tools|FullyQualifiedName~SolusAgent.ContractTests.Tools"`, then the full suite.

The [runtime tools draft](../20_architecture/drafts/runtime-tools.md) owns its conservative reservation policy and limits. This tool-path evidence does not establish candidate execution, complete budgets, persistence, live calls or release qualification; the DeepSeek adapter retains its separate transport evidence.

Runtime.Candidates tests execute the actual public `ICandidateAgent` returned by startup, using the independent Scripted Provider and existing Api-only candidate Host/consumer. They prove feedback-driven repair and continuation, full acknowledgement association, exact finite bounds, retained receipts/usage, whole-run cut barriers and late-result confinement. The [runtime candidate draft](../20_architecture/drafts/runtime-candidates.md) owns its production meaning and terminal categories. Run the focused path and then the full suite after shared contract/state edits:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Runtime.Candidates"
```

Runtime.Consumption tests compose the production runtime startup with the independently compiled ScriptedProvider, the narrow CustomTools CounterTool and TransformTool bindings and the Api-only AprHost and ScribeHost business Hosts. They prove the mixed five-turn tool/candidate production with tool- and correction-derived candidate values and correction metamorphism, once-per-episode correction and repair/continuation accounting across intermediate tool turns, literal `WorkUnitLimit` before every model admission, submission ceilings that ignore tool turns and End at exact ceilings, retained earlier Host acceptance and usage after later provider failure, resource bound, cancellation, unknown feedback and Host End, whole-batch rejection with zero new effects, closure gating of tools and candidate submission, held tool/provider/Host completion observed after the cut without rewriting returned snapshots, Scribe fresh re-identification without transcript or restoration, positively overlapped equal-execution-ID runs with isolated histories and receipts, and restricted/credential canary confinement. Run the focused path first:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Runtime.Consumption"
```

The scenarios described above are the M2 scripted-transport consumption tier: they prove the production runtime, tool batches and both business Host consumption patterns under the independent ScriptedProvider. Those M2 scenarios alone do not establish the separate M3 budget obligations.

The [M3ScribeConformanceTests](../../tests/SolusAgent.ContractTests/Runtime/Consumption/M3ScribeConformanceTests.cs) add Scribe-specific production proof of Host-configured accounting and tool allowance, complete-batch admission/denial, provider-only retry without tool/Host replay, independent model/tool/candidate counters, provider count/token/reservation stops, all three missing-usage policies, retained acknowledged business progress, fresh-run ledger/allowance isolation, and cancellation/duration cuts with immutable late-completion snapshots. The [Scribe M3 evidence mapping](../20_architecture/drafts/scribe-consumption.md#fresh-run-accounting-and-tool-allowance-m3) owns the detailed scenarios. Run its focused filter first:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Runtime.Consumption.M3ScribeConformanceTests"
```

Independent APR M3 accounting and retry consumption is delivered by [PR #63](https://github.com/SolusQuest/solus-agent/pull/63) in `M3AprConformanceTests` and documented in the [APR consumption draft](../20_architecture/drafts/apr-consumption.md). It covers provider counts, observed token and accounting limits, missing-usage policies with bounded continuation, provider retry versus candidate repair, retained acceptance/effects and late cut snapshots. Its evidence is separate from the M1 `ConsumerProbes.Apr` checks and the Scribe-specific tool-allowance proof.

Both M2 and M3 consumption tiers use scripted transport. They do not prove a real model adapter or live transport for these business Hosts, billing accuracy, durable campaign accounting, product acceptance, restoration or downstream migration. The controlled DeepSeek adapter transport evidence is recorded separately in the DeepSeek adapter section above for the generic Api-only candidate-host composition; the literal APR and Scribe business-Host transport obligations in the consumption drafts remain untested.

## Continuous integration

`.github/workflows/ci.yml` runs the same restore, build, and test commands on `push` and `pull_request` with read-only `contents` permission, checking out the repository and installing the SDK selected by `global.json` on `ubuntu-latest`. It requires no provider secrets, live model access, sibling checkouts, or machine-local paths, and it must keep commands identical to those documented above. Ordinary CI executes the same checks as a local run on a different platform; it is not release qualification and does not authorize live or paid execution under [Security boundary](../20_architecture/security-boundary.md).

## Documentation and handbook checks

Verify relative links and anchors, reading order, UTF-8/LF/final-newline hygiene, and consistency between current implementation and selected requirements. Include actual reading paths and executable documentation consumers where affected. For shared-guidance changes, verify the pinned submodule, applicable skill metadata, and resources resolved from the handbook source location.

Check shared links in SolusAgent-owned Markdown against the same adopted upstream commit as the Gitlink. Verify their published GitHub reading route and the corresponding initialized-checkout paths, including linked anchors and the local skill paths in task routing. Parent repository URLs cannot traverse the submodule's child files.

Follow [Shared handbook adoption](shared-handbook.md) for source acquisition, update checks, and the selected entrypoint loading arrangement. A link check alone does not demonstrate a harness loading instructions. Report directly observed reading and resource access separately from untested automatic skill discovery.

## As behavior grows

Add meaningful tests with implemented behavior using synthetic providers, tools, and transports. Include actual affected producer-consumer paths for shared API changes. Native local validation is the default; additional environments require a concrete platform question or declared qualification scope. Ordinary PR and push CI must not require model credentials under [Security boundary](../20_architecture/security-boundary.md).

Keep raw build and test output in ignored local locations and summarize applicable inputs, results, and limits. Select further checks from the affected behavior under the shared procedure; documentation-only adoption does not establish new runtime or build acceptance.

## Ordinary context process and admission checks (C1)

Run `dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Runtime.Context"`, then the related Context, Runtime.Execution, Usage and Providers checks and the complete runner. `Runtime.Context` composes the actual public factory, Api-only context consumer and independently compiled Runtime.Api-only `PersistentProvider`. The existing M1 Context checks retain their synthetic evidence identity.

The C1 suite executes real separate child processes using the existing test runner's test-only executable entrypoint. Producer A captures after a dispatched retryable failure with nonzero usage and acknowledged closure; consumer B reads only retained bytes and separately injected Host controls, completes with full current allowance and a fresh duration, and preserves exact operation/history associations. Separate child invocations prove replayed-grant, competing-source, inconsistent-binding and trusted-provenance rejection with zero new effects. Three-process A/B/C cases also stop B during resumed backoff without an attempt, then complete C using the inherited cursor/state. Request fingerprints and exact physical predecessor IDs are compared across processes. Exclusive flushed fixture files supply the Host claim boundary; they do not qualify arbitrary production storage or power-loss durability.

Owning cases also exercise completed-Final new tasks, multiple renewal rounds, unknown historical measurements, failed/unknown closure, strict malformed/duplicate/missing/contradictory state, provider import/export rejection, zero import before Host admission, failed-import grant consumption, independently trusted terminal reason/cut/attempt/closure contradictions, lawful post-accept cuts and observer failures, frozen terminal state across delayed export, per-Final original effective bounds, live-definition byte accounting at exact/one-under capacity, exact/one-over cumulative capacities and restricted/credential confinement. Actual encoding-capacity neighbors verify that failed preparation publishes neither envelope nor checkpoint metadata, while a sink failure may retain successfully prepared bytes and metadata. Public Context observation tests exercise alternate producers through the Api-only consumer, rejecting malformed checkpoint/history fields and preserving valid immutable observations without requiring globally unique Host correlations. Capture failure preserves execution and usage. Tool/candidate context, DeepSeek persistence, live services, product migrations and guaranteed capture on process death remain unproved. Child scratch state and raw output stay ignored. Record actual executed counts and local versus exact-head CI evidence.

## DeepSeek provider persistence checks (C2)

Run the owning new checks with `dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.DeepSeek.Adapter.Persistence"`, then DeepSeek.Adapter, DeepSeek.RuntimeIntegration, Runtime.Context, Providers and Architecture regressions and the complete runner. Use a successful applicable restore and Release build before `--no-build`.

`PersistenceTests` covers the actual provider-owned 36-byte format, immutable copies, exact/null/empty/maximum replay, malformed frames and UTF-8, every binding field, corrupt/truncated/trailing/unsupported state, current option compatibility, credential rotation, concurrency and disposed adapters without effects. `PersistenceProcessTests` launches the existing runner through a forwarding DeepSeek dispatcher, preserving every other argument and the original C1 entrypoint. Actual public runtime capture/import and controlled HTTP span distinct PIDs and new adapter/handler instances: tools-enabled ordinary Final with no executed tool, empty/maximum reasoning, 503 retry lineage and a three-process earlier-Final/later-failure/retry path. Independent expected JSON is compared to each full actual request, with separate response/attempt association and historical/current usage checks. Malformed provider state selected by a controlled trusted Host reaches provider admission after claim; rejection produces zero sends and consumes the claim. Outer-format/lineage/provenance negatives exercise earlier gates. Child scratch files stay under ignored `.local/issue-69/process-fixtures`; restricted checkpoints and synthetic wire bodies are distinct from ordinary results, stdout and errors.

`PersistenceWireTests` additionally exports after real tool calls/results and imports into a fresh adapter/handler with the original immutable accepted carriers in the same process. It checks complete wire equality, exact raw arguments, null assistant content, all per-turn reasoning, reordered legal results, response association and unchanged tool-effect counts. It does not reconstruct executed tools across processes: the merged C1 public ToolResult has no effect-free historical factory. Production completed-tool restoration and mixed tool/candidate process composition retain separate ownership. These checks require no real credential, live service, new production reference, runner project or migration framework. Record actual nonzero local counts separately from exact-head CI and full composition, storage, billing or live interoperability claims.
