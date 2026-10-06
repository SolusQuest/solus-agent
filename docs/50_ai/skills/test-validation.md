# Validation

Select validation from the failure surface changed by the task. Do not invent test or CI results, reuse stale outputs, or add a validation framework merely to certify an initialization skeleton.

## Current available checks

The solution contains four library skeletons and no test projects, executable host, providers, or tools. The baseline checks are:

```text
dotnet restore SolusAgent.slnx
dotnet build SolusAgent.slnx --configuration Release --no-restore
```

Inspect solution membership and direct project references against [Project structure](../../20_architecture/project-structure.md). For documentation, verify relative links, reading order, line endings, and consistency between current implementation and selected requirements.

Build success demonstrates that the configured libraries compile. It does not demonstrate an agent execution path, tool behavior, context restoration, tests, cross-platform support, CI, package installation, or release readiness. `dotnet test` with no tests is not acceptance evidence.

## Build validity

Use `--no-restore` only after a successful applicable restore. Restore again after SDK, framework, project, reference, package, source, or restore-property changes.

Use `--no-build` only after a successful build of the same inputs, SDK, configuration, and working tree. Rebuild after source, project, solution, build configuration, or generator changes. Outputs from another worktree or Debug configuration do not validate this Release tree.

## Scope as implementation grows

Add meaningful tests with actual behavior during the corresponding implementation work. Use synthetic providers and tools for ordinary checks and include affected producer-consumer paths when a shared API changes. Choose focused checks first, then broaden only for an affected shared boundary, failure, or unresolved concern.

Run native local validation by default. Use another execution environment for a concrete platform-specific question, not as an additional routine matrix. Local evidence does not establish support for an untested platform. CI and required platform qualification will be designed separately.

## Process and evidence

Preserve the process or terminal handle for a long run. When an observation budget expires, inspect the original run before starting another. Do not retry while the original process is active or uncertain, and do not terminate unrelated processes by their global name.

Keep raw build and test output in ignored local locations. Record the actual command, working tree or revision, selected SDK and configuration, result, and relevant limitation. Future test evidence must include execution, not only discovery or empty passing counters.

Once sufficient applicable checks pass, continue toward completing the authorized task. Repeat or broaden them only when changed inputs, a failure, or an unresolved concern justifies it.
