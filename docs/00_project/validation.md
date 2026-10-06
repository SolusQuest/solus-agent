# Project validation

Use shared [Validation](../shared/standards/validation.md) and [Test validation](../shared/skills/test-validation/SKILL.md) with these SolusAgent commands and acceptance limits.

## Current available checks

The solution contains four library skeletons and no test projects, executable host, providers, or tools. From the repository root, use the SDK selected by `global.json`:

```text
dotnet restore SolusAgent.slnx
dotnet build SolusAgent.slnx --configuration Release --no-restore
```

Inspect solution membership and direct project references against [Project structure](../20_architecture/project-structure.md). Build success proves compilation only; it does not demonstrate an agent execution path, tools, restoration, tests, platform support, CI, package installation, or release readiness. An empty `dotnet test` run is not acceptance evidence.

Use `--no-restore` only after a successful applicable restore; repeat restore after SDK, framework, project, reference, package, source, or restore-property changes. Use `--no-build` only after a successful build of the same inputs, SDK, configuration, and tree; rebuild after source, project, solution, build configuration, or generator changes.

## Documentation and handbook checks

Verify relative links and anchors, reading order, UTF-8/LF/final-newline hygiene, and consistency between current implementation and selected requirements. Include actual reading paths and executable documentation consumers where affected. For shared-guidance changes, verify the pinned submodule, applicable skill metadata, and resources resolved from the handbook source location.

Follow [Shared handbook adoption](shared-handbook.md) for source acquisition, update checks, and the selected entrypoint loading arrangement. A link check alone does not demonstrate a harness loading instructions. Report directly observed reading and resource access separately from untested automatic skill discovery.

## As behavior grows

Add meaningful tests with implemented behavior using synthetic providers, tools, and transports. Include actual affected producer-consumer paths for shared API changes. Native local validation is the default; additional environments require a concrete platform question or declared qualification scope. Ordinary PR and push CI must not require model credentials under [Security boundary](../20_architecture/security-boundary.md).

Keep raw build and test output in ignored local locations and summarize applicable inputs, results, and limits. Select further checks from the affected behavior under the shared procedure; documentation-only adoption does not establish new runtime or build acceptance.
