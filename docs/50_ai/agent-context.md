# Agent context

You are working on SolusAgent, a shared .NET agent API and self-owned runtime project. Read this document after root `AGENTS.md`, then load the shared [Context model](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/agents/context-model.md), applicable rules, and relevant task procedure.

## Current baseline

Before planning or changing implementation, read [Project context](../00_project/project-context.md), [Architecture](../20_architecture/architecture.md), and [Project structure](../20_architecture/project-structure.md). They own the selected project boundaries.

The current tree has four production libraries, an executable [prepared function-tool draft](../20_architecture/drafts/function-tools.md), a real synthetic CustomTools consumer, one managed architecture/contract test runner and ordinary push/pull_request CI. Outer agent and runtime behavior remain unimplemented. The [roadmap](../90_roadmap/roadmap.md) selects M0 plus M1-M5 toward a consumable experimental prerelease. Tests and CI cover compilation, evaluated project boundaries and actual synthetic tool semantics; packaging, runtime implementation and downstream migration have not been delivered. Read the roadmap before delivery planning and keep planned outcomes distinct from current behavior.

`SolusAgent.Api` is independent of the self-owned runtime. `SolusAgent.Tools.Api` is reusable across supporting agent implementations. `SolusAgent.Runtime.Api` owns self-owned extension contracts; `SolusAgent.Runtime` will supply the initial implementation. Products retain domain acceptance and platform side effects. Complete runtime context restoration is selected; the product chooses fresh versus restored execution.

## Shared loading and ownership

[Shared handbook adoption](../00_project/shared-handbook.md) records the pinned source at `docs/shared/`, initialization, updates, and loading results. Shared skill sources are discovered through the [Task routing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/agents/task-routing.md) links and the table below. Read each `SKILL.md` directly; resolve its referenced standards and templates from its directory in the handbook.

Shared links target the adopted upstream commit for GitHub readers. In an initialized checkout, read the local source paths in root `AGENTS.md` and the table below. Map other shared URLs by placing the path after the commit under `docs/shared/`; shared task routing itself is `docs/shared/agents/task-routing.md`. Both routes select the same content.

Shared rules belong to Solus Book. Product requirements and commands remain in `docs/00_project`, `docs/10_workflow`, `docs/20_architecture`, and `docs/90_roadmap`. Project-specific agent context belongs here; an additional local procedure or platform entrypoint needs an actual maintained use. Keep machine-local notes and task records in ignored locations.

## Read by task

Shared [Collaboration](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/collaboration.md), [Source of truth](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/source-of-truth.md), and [Conventions](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/conventions.md) apply with [project conventions](../00_project/conventions.md). Select additional reading from the affected work:

| Task | Shared procedure or rule | Local shared source under `docs/shared/` | Project inputs |
| --- | --- | --- | --- |
| Resolve a material design choice | [Design refinement](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/design-refinement/SKILL.md); [Pre-release engineering](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pre-release-engineering.md). | `skills/design-refinement/SKILL.md`; `standards/pre-release-engineering.md`. | Architecture, project structure, security boundary, and [Runtime contract lifecycle](../00_project/contract-lifecycle.md) where affected. |
| Refine scope or an issue | [Issue refinement](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/issue-refinement/SKILL.md). | `skills/issue-refinement/SKILL.md`. | [Issue requirements](../10_workflow/issue-workflow.md), accepted architecture, and roadmap. |
| Publish or update an issue | [Issue publishing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/issue-publishing/SKILL.md). | `skills/issue-publishing/SKILL.md`. | Issue requirements and the exact authorized target and fields. |
| Implement or change documentation | [Pre-release engineering](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pre-release-engineering.md) and affected shared contracts. | `standards/pre-release-engineering.md` and affected contracts. | Project conventions and affected current specifications; project structure before project changes. |
| Validate | [Test validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/test-validation/SKILL.md); [Validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/validation.md). | `skills/test-validation/SKILL.md`; `standards/validation.md`. | [Project validation](../00_project/validation.md), actual task acceptance, and affected consumers. |
| Prepare or publish a PR | [Pull request publishing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/pr-publishing/SKILL.md). | `skills/pr-publishing/SKILL.md`. | [PR requirements](../10_workflow/pr-workflow.md), project validation, and authorized publication scope. |
| Update handbook adoption | [Downstream adoption](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/docs/downstream-adoption.md). | `docs/downstream-adoption.md`. | Shared handbook adoption, current local requirements, and the selected source revision. |

Read [Security boundary](../20_architecture/security-boundary.md) before changing providers, context, tool admission, diagnostic visibility, or external authority. Read the runtime contract lifecycle before designing public contracts or saved state. Use shared pre-release and lifecycle rules for decomposition, compatibility, review, and closure decisions.

## Architecture refinement inputs

Refine material choices in the outer agent API, shared tool API, runtime extensions, saved context, model exchange, budget semantics, and external authority against both actual downstream needs where they affect a shared contract. Preserve runtime mechanics in SolusAgent and product policies downstream. Product evidence and progress types do not become universal contracts by moving into an API assembly.

Additional agent backends, plugin discovery, remote tool transports, source generators, and release machinery require concrete requirements of their own. Keep refinement bounded to current implementation and preserve accepted naming and dependency decisions.

## Task identity and authority

Apply shared collaboration and its distinction between governing rules and task material. Preserve the requested repository, task scope, branch or worktree, and review identity. Current task instructions and established authorization remain in force; references to other repositories do not grant writes there. Record accepted durable conclusions in their owning project documents rather than copying raw conversations.
