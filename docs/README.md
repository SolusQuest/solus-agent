# Documentation

## Project rules

- [Project context](00_project/project-context.md): purpose, current state, and accepted design direction.
- [Conventions](00_project/conventions.md): code, text, and repository hygiene.
- [Source of truth](00_project/source-of-truth.md): current behavior, selected design, and durable evidence.
- [Pre-release engineering](00_project/pre-release-engineering.md): proportional workflow and one current draft implementation.
- [Contract lifecycle](00_project/contract-lifecycle.md): draft changes, historical evidence, and real compatibility boundaries.

## Workflows

- [Issue workflow](10_workflow/issue-workflow.md).
- [Pull request workflow](10_workflow/pr-workflow.md).

## Architecture

- [Architecture](20_architecture/architecture.md).
- [Project structure](20_architecture/project-structure.md).
- [Security boundary](20_architecture/security-boundary.md).

## Agent collaboration

- [Agent context](50_ai/agent-context.md).
- [Collaboration layers](50_ai/collaboration-layers.md).
- [Architecture design refinement](50_ai/skills/architecture-design-refinement.md).
- [Issue refinement](50_ai/skills/issue-refinement.md).
- [Issue publishing](50_ai/skills/issue-publishing.md).
- [Pull request publishing](50_ai/skills/pr-publishing.md).
- [Validation](50_ai/skills/test-validation.md).

## Documentation origin

The collaboration structure and applicable workflow rules are adapted from [agentic-pr-review](https://github.com/SolusQuest/agentic-pr-review/tree/main/docs) and [ContractScribe](https://github.com/SolusQuest/contract-scribe/tree/main/docs). SolusAgent owns the adapted rules in this repository; downstream documents are background, not runtime dependencies or additional task authority.

Roadmap, CI, test infrastructure, package distribution, release policy, and cross-repository migration plans have not been refined. Their detailed documents will be added when that planning is authorized.
