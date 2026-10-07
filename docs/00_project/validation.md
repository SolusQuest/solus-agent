# Project validation

Use shared [Validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/validation.md) and [Test validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/test-validation/SKILL.md) with these SolusAgent commands and acceptance limits.

## Current available checks

The solution contains four production libraries, a test-only Api-only custom-agent consumer library, and one managed test runner, `tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj`. From the repository root, use the SDK selected by `global.json`:

```text
dotnet restore SolusAgent.slnx
dotnet build SolusAgent.slnx --configuration Release --no-restore
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build
```

The test project runs xUnit through `Microsoft.NET.Test.Sdk`, `xunit`, and `xunit.runner.visualstudio` with private runner assets. It references `SolusAgent.Api` and the test-only `SolusAgent.ApiOnlyConsumer`; that separately compiled consumer references only Api and has no packages. Its Execution tests invoke the actual consumer/agent through `IAgent`. Architecture tests evaluate real production project files through `dotnet msbuild` JSON output, including imported and conditioned items. Future focused tests use this runner and add only references their implemented code needs. Api generates XML documentation with warnings-as-errors during the normal build.

Focused runs select the same runner with a filter, for example:

```text
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Architecture"
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build --filter "FullyQualifiedName~SolusAgent.ContractTests.Execution"
```

The Architecture tests check evaluated project boundaries only: the exact four-library reference graph, package independence of `SolusAgent.Api` and `SolusAgent.Tools.Api`, managed `net10.0` targets, and evaluated compile inputs that stay inside the repository. Synthetic negative checks mutate an owned temporary Api-only consumer with forbidden `Runtime` and `Runtime.Api` reference edges and an out-of-root linked source, then require the same boundary assertions to fail for the specific offending edge. Confirm nonzero executed test counts and named negative checks; an empty `dotnet test` run is not acceptance evidence.

Execution tests demonstrate the current outer contract through the actual Api-only consumer: bounded normal/partial/resource/cancel/failure outcomes, pre-work invalid/unsupported rejection, correlation, immutable control/data separation and restricted diagnostic canaries. They check the consumer's evaluated exact Api-only graph and compiled assembly references. The [execution draft](../20_architecture/drafts/agent-execution.md) states these guarantees and limits.

These checks do not demonstrate a production loop, tools/providers, duration or budget enforcement, context restoration, packaging, installation or release readiness. A passing local run does not establish CI results or platform support. Inspect solution membership and direct references against [Project structure](../20_architecture/project-structure.md).

Use `--no-restore` only after a successful applicable restore; repeat restore after SDK, framework, project, reference, package, source, or restore-property changes. Use `--no-build` only after a successful build of the same inputs, SDK, configuration, and tree; rebuild after source, project, solution, build configuration, or generator changes.

## Continuous integration

`.github/workflows/ci.yml` runs the same restore, build, and test commands on `push` and `pull_request` with read-only `contents` permission, checking out the repository and installing the SDK selected by `global.json` on `ubuntu-latest`. It requires no provider secrets, live model access, sibling checkouts, or machine-local paths, and it must keep commands identical to those documented above. Ordinary CI executes the same checks as a local run on a different platform; it is not release qualification and does not authorize live or paid execution under [Security boundary](../20_architecture/security-boundary.md).

## Documentation and handbook checks

Verify relative links and anchors, reading order, UTF-8/LF/final-newline hygiene, and consistency between current implementation and selected requirements. Include actual reading paths and executable documentation consumers where affected. For shared-guidance changes, verify the pinned submodule, applicable skill metadata, and resources resolved from the handbook source location.

Check shared links in SolusAgent-owned Markdown against the same adopted upstream commit as the Gitlink. Verify their published GitHub reading route and the corresponding initialized-checkout paths, including linked anchors and the local skill paths in task routing. Parent repository URLs cannot traverse the submodule's child files.

Follow [Shared handbook adoption](shared-handbook.md) for source acquisition, update checks, and the selected entrypoint loading arrangement. A link check alone does not demonstrate a harness loading instructions. Report directly observed reading and resource access separately from untested automatic skill discovery.

## As behavior grows

Add meaningful tests with implemented behavior using synthetic providers, tools, and transports. Include actual affected producer-consumer paths for shared API changes. Native local validation is the default; additional environments require a concrete platform question or declared qualification scope. Ordinary PR and push CI must not require model credentials under [Security boundary](../20_architecture/security-boundary.md).

Keep raw build and test output in ignored local locations and summarize applicable inputs, results, and limits. Select further checks from the affected behavior under the shared procedure; documentation-only adoption does not establish new runtime or build acceptance.
