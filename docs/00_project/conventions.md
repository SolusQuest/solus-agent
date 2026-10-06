# Project conventions

Apply the shared [Conventions](../shared/standards/conventions.md) and [Collaboration](../shared/standards/collaboration.md) with the following SolusAgent requirements.

## Repository content

- Use English for repository documentation, code, issue titles, and pull request text.
- Keep documentation, issue bodies, pull request bodies, and comments as normal paragraphs without manual wrapping or decorative marker strings.
- Keep repository-bound content suitable for publication: no credentials, private downstream source, private or live-run prompts, raw provider responses, full transcripts, raw logs, machine-local paths, or real session identifiers. This local restriction also applies to private drafts and PRs in this repository.
- Use synthetic and minimized fixtures when an executable contract needs them. Preserve the failure while removing private data and hidden reasoning.
- Do not describe the project as open source or invite contributions until a license decision is recorded.
- Keep machine-local task notes and execution artifacts in ignored locations. The repository must remain usable without private notes, another project's working tree, or an agent's memory.

## C# and project layout

- Target the framework and SDK selected by the root build configuration.
- Use nullable reference types, implicit usings, and warnings-as-errors consistently.
- Use PascalCase for types and public members, camelCase for parameters and local variables, and an `I` prefix for interfaces.
- Prefer file-scoped namespaces and normal .NET asynchronous naming. Expose cancellation for asynchronous operations that can be interrupted.
- Project names, assembly names, and root namespaces match: `SolusAgent.Api`, `SolusAgent.Tools.Api`, `SolusAgent.Runtime.Api`, and `SolusAgent.Runtime`.
- Keep public contracts small and implementation details internal. Dependency direction follows [Project structure](../20_architecture/project-structure.md).
- Keep provider-specific wire records, continuation materialization, and SDK dependencies out of the outer agent and shared tool APIs.
- Add a project for an actual dependency, extension, or distribution boundary. Do not reserve speculative implementation, generator, integration, or test projects.
- XML documentation comments describe implemented public API behavior. Do not add placeholder types or imply that documentation implements an API.

## Text and build hygiene

Use UTF-8, LF line endings, and final newlines as configured by `.editorconfig` and `.gitattributes`. Keep build output, raw validation evidence, and environment files out of maintained repository content.

Ordinary validation uses synthetic providers and tools. [Project validation](validation.md) owns current commands and build prerequisites; [Security boundary](../20_architecture/security-boundary.md) owns live-provider and external-effect requirements.

## Project workflow

[Issue requirements](../10_workflow/issue-workflow.md) and [Pull request requirements](../10_workflow/pr-workflow.md) supply this repository's tracker conventions, title format, and merge restriction. Shared authorization guidance does not change those local requirements.

[Shared handbook adoption](shared-handbook.md) owns the handbook source and update arrangement. Changes to imported rule bodies are maintained in Solus Book and consumed through an authorized pinned update.
