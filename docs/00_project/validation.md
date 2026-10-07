# Project validation

Use shared [Validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/validation.md) and [Test validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/test-validation/SKILL.md) with these SolusAgent commands and acceptance limits.

## Current available checks

The solution contains four production libraries, the separate compiled Api-only custom-agent, CustomTools and CustomProvider consumer libraries, and one managed test runner, `tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj`. Api and Tools.Api implement their current execution and function-tool drafts; Runtime.Api contains the provider exchange draft; Runtime remains an implementation skeleton. From the repository root, use the SDK selected by `global.json`:

```text
dotnet restore SolusAgent.slnx
dotnet build SolusAgent.slnx --configuration Release --no-restore
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build
```

The runner uses xUnit through `Microsoft.NET.Test.Sdk`, `xunit` and `xunit.runner.visualstudio` with private runner assets. It references `SolusAgent.Api`, `SolusAgent.Tools.Api`, `SolusAgent.Runtime.Api` and the three test-only consumer libraries for actual behavioral tests. The separately compiled Api-only consumer references only Api and has no packages; CustomTools references only Tools.Api and has no packages; CustomProvider references only Runtime.Api directly, without Runtime or packages. Execution and Usage tests invoke the actual consumer/agent through `IAgent`. Architecture tests evaluate the real production and consumer project files through `dotnet msbuild` JSON output, including imported and conditioned items. Future focused tests use this same runner and add only dependencies required by implemented test code. Api, Tools.Api and Runtime.Api generate XML documentation with warnings-as-errors during the normal build.

Focused runs select the same runner with a filter, for example:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Architecture"
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Execution"
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Usage"
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Tools"
```

The Architecture tests check the exact four-library reference graph, package independence of `SolusAgent.Api` and `SolusAgent.Tools.Api`, exact current solution/test dependencies, managed `net10.0` targets and evaluated compile inputs that stay inside the repository. CustomTools must have Tools.Api as its sole production edge and no packages; CustomProvider must have Runtime.Api as its sole direct production edge, no Runtime edge/packages, and repository-contained sources. Synthetic negative checks mutate an owned temporary Api-only consumer with forbidden `Runtime` and `Runtime.Api` reference edges and an out-of-root linked source, then require the same boundary assertions to fail for the specific offending edge. Confirm nonzero executed test counts and named negative checks; an empty `dotnet test` run is not acceptance evidence.

Execution tests demonstrate the current outer contract through the actual Api-only consumer: bounded normal/partial/resource/cancel/failure outcomes, pre-work invalid/unsupported rejection, correlation, immutable control/data separation and restricted diagnostic canaries. They check the consumer's evaluated exact Api-only graph and compiled assembly references. The [execution draft](../20_architecture/drafts/agent-execution.md) states these guarantees and limits.

Usage tests exercise the [usage draft](../20_architecture/drafts/usage.md) through the actual Api-only scripted agent and Host consumer: nullable core/provider facts, retry association, retained exposure/measurement after validation failure, cancellation and observer failure, count admission and post-response stopping with unknown/overflow neighbors. These are synthetic proof, not production budget enforcement or billing accuracy.

Tools tests run the actual synthetic consumer through public contracts and prove bounded metadata/schema/arguments/results, zero preparation effects, explicit narrow capability admission, call/output association, single-use concurrent invocation and honest cancellation/failure outcomes. The [function-tool draft](../20_architecture/drafts/function-tools.md) states these guarantees and limits. Run the full small suite after shared signature or registration changes.

These checks do not demonstrate a production runtime loop, production providers/tool adapters, duration or budget enforcement, context restoration, packaging, installation, production external effects or release readiness. A passing local run does not establish CI results or platform support. Inspect solution membership and direct references against [Project structure](../20_architecture/project-structure.md).

Use `--no-restore` only after a successful applicable restore; repeat restore after SDK, framework, project, reference, package, source, or restore-property changes. Use `--no-build` only after a successful build of the same inputs, SDK, configuration, and tree; rebuild after source, project, solution, build configuration, or generator changes.

Candidates tests exercise the actual Api-only producer and Host consumer through `ICandidateAgent`: independent correlation/decisions, rejected corrections and accepted continuation, explicit termination, partial progress, bounded follow-on admissions, missing/failed/unknown feedback, wrong/duplicate association, pending cancellation and late-response non-admission. They also prove pre-work required-capability rejection, observer-failure ordering, immutable safe receipts and payload/correction/error canary confinement. Use the same runner with `--filter "FullyQualifiedName~SolusAgent.ContractTests.Candidates"` for a focused run. The [candidate-feedback draft](../20_architecture/drafts/candidate-feedback.md) states the exact synthetic policies and limits; in-memory Host effects are independently observed and never replayed automatically.

Providers tests call the actual Runtime.Api-only CustomProvider producer/Host consumer and guarded exchange: two model turns, two fully correlated real tools admitted as a whole batch, exact required continuation, invalid shapes/associations, retained usage after payload rejection/failure/cancellation, known/unknown dispatch, capture closure/concurrency, finite count/byte neighbors and diagnostic canaries. Use the same runner with `--filter "FullyQualifiedName~SolusAgent.ContractTests.Providers"`. The [provider exchange draft](../20_architecture/drafts/provider-exchange.md) states current semantics and M2/M4 deferrals; these are synthetic expressibility checks, not actual transport/parser/restoration proof.

Context tests exercise the actual Api-only producer/Host consumer for explicit intent, implementation/format/compatibility/grammar/transition rejection before effects, no fresh fallback, Host control/capability reinjection, immutable restricted copies and safe canary confinement. Call-scoped capture and overlapping same-correlation sinks demonstrate Host retention without agent lookup; capture failure stays separate from work outcomes. These checks cover the [context envelope draft](../20_architecture/drafts/context-envelope.md), not production restoration or a durable codec. Use the existing runner:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Context"
```

RuntimeConfiguration tests exercise the actual independent CustomProvider configuration consumer, base-derived and interface-only tools, full acknowledgement/settlement association, held callbacks, explicit retries, cancellation cuts, retained usage after failure, optional Host controls, finite registry/count neighbors and canary confinement. They prove the [runtime configuration and exposure draft](../20_architecture/drafts/runtime-configuration.md) in memory, without production storage, strict work/duration/token enforcement, automatic retry or a restoration codec. Use the existing runner:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.RuntimeConfiguration"
```

## Continuous integration

`.github/workflows/ci.yml` runs the same restore, build, and test commands on `push` and `pull_request` with read-only `contents` permission, checking out the repository and installing the SDK selected by `global.json` on `ubuntu-latest`. It requires no provider secrets, live model access, sibling checkouts, or machine-local paths, and it must keep commands identical to those documented above. Ordinary CI executes the same checks as a local run on a different platform; it is not release qualification and does not authorize live or paid execution under [Security boundary](../20_architecture/security-boundary.md).

## Documentation and handbook checks

Verify relative links and anchors, reading order, UTF-8/LF/final-newline hygiene, and consistency between current implementation and selected requirements. Include actual reading paths and executable documentation consumers where affected. For shared-guidance changes, verify the pinned submodule, applicable skill metadata, and resources resolved from the handbook source location.

Check shared links in SolusAgent-owned Markdown against the same adopted upstream commit as the Gitlink. Verify their published GitHub reading route and the corresponding initialized-checkout paths, including linked anchors and the local skill paths in task routing. Parent repository URLs cannot traverse the submodule's child files.

Follow [Shared handbook adoption](shared-handbook.md) for source acquisition, update checks, and the selected entrypoint loading arrangement. A link check alone does not demonstrate a harness loading instructions. Report directly observed reading and resource access separately from untested automatic skill discovery.

## As behavior grows

Add meaningful tests with implemented behavior using synthetic providers, tools, and transports. Include actual affected producer-consumer paths for shared API changes. Native local validation is the default; additional environments require a concrete platform question or declared qualification scope. Ordinary PR and push CI must not require model credentials under [Security boundary](../20_architecture/security-boundary.md).

Keep raw build and test output in ignored local locations and summarize applicable inputs, results, and limits. Select further checks from the affected behavior under the shared procedure; documentation-only adoption does not establish new runtime or build acceptance.
