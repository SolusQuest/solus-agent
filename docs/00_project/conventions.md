# Conventions

## Repository content

- Use English for repository documentation, code, issue titles, and pull request text.
- Keep documentation, issue bodies, pull request bodies, and comments as normal paragraphs. Do not manually wrap prose or add decorative marker strings.
- Keep repository-bound content suitable for publication: no credentials, private downstream source, private or live-run prompts, raw provider responses, full transcripts, raw logs, machine-local paths, or real session identifiers.
- Synthetic and minimized fixtures are appropriate when an executable contract needs them. Preserve the failure being tested while removing private data and hidden reasoning.
- Do not describe the project as open source or invite contributions until a license decision is recorded.
- Keep machine-local task notes and execution artifacts in ignored locations. The repository must remain usable without private notes, another project's working tree, or an agent's memory.

## C# and project layout

- Target the framework and use the SDK selected by the root build configuration.
- Use nullable reference types, implicit usings, and warnings-as-errors consistently.
- Use PascalCase for types and public members, camelCase for parameters and local variables, and an `I` prefix for interfaces.
- Prefer file-scoped namespaces and normal .NET asynchronous naming. Expose cancellation for asynchronous operations that can be interrupted.
- Project names, assembly names, and root namespaces match: `SolusAgent.Api`, `SolusAgent.Tools.Api`, `SolusAgent.Runtime.Api`, and `SolusAgent.Runtime`.
- Keep public contracts small and implementation details internal. Dependency direction follows [Project structure](../20_architecture/project-structure.md).
- Keep provider-specific wire records, continuation materialization, and SDK dependencies out of the outer agent and shared tool APIs.
- Add a project when an actual dependency, extension, or distribution boundary needs it. The four initial library skeletons are explicitly authorized initialization boundaries; do not add speculative implementation, generator, integration, or test projects merely to reserve names.
- XML documentation comments describe public API behavior when those APIs are implemented. Do not add placeholder types or pretend documentation is an implementation.

## Text and build hygiene

- Use UTF-8, LF line endings, and final newlines as configured by `.editorconfig` and `.gitattributes`.
- Keep build output, local validation evidence, and environment files out of repository content.
- Use synthetic providers and tools for ordinary development validation. Paid or live-provider execution follows the specific task's authorization.
- Select validation from the changed behavior and report what actually ran. Follow [Validation](../50_ai/skills/test-validation.md).

## Work and authorization

- Respect the task's instructions for branches, commits, and publication. A request for local work does not authorize pushing, opening a pull request, or publishing a package.
- Once the maintainer establishes the initial Git baseline, substantive repository changes normally use a branch and a reviewable pull request.
- Use conventional pull request titles: `<type>(<scope>): <summary>`.
- The maintainer owns merge and release decisions. Agents do not merge pull requests.
- Repository settings, labels, milestones, Projects, branch protection, secrets, and tracker closure require authorization for the particular operation.
- An existing task authorization remains valid. Do not request repeated approval for ordinary reversible implementation and validation within that scope.

Use [Issue workflow](../10_workflow/issue-workflow.md) and [Pull request workflow](../10_workflow/pr-workflow.md) for their respective operations.
