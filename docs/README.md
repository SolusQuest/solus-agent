# Documentation

## Shared engineering handbook

SolusAgent consumes a pinned Solus Book submodule at `docs/shared/`. [Shared handbook adoption](00_project/shared-handbook.md) records initialization, updates, loading, and the local rules retained during deduplication.

- [Collaboration](shared/standards/collaboration.md).
- [Source of truth](shared/standards/source-of-truth.md).
- [Pre-release engineering](shared/standards/pre-release-engineering.md).
- [Contract lifecycle](shared/standards/contract-lifecycle.md).
- [Conventions](shared/standards/conventions.md).
- [Issue workflow](shared/standards/issue-workflow.md).
- [Pull request workflow](shared/standards/pr-workflow.md).
- [Validation](shared/standards/validation.md).

## Project requirements

- [Project context](00_project/project-context.md): purpose, current state, and accepted design direction.
- [Project conventions](00_project/conventions.md): language, publication restrictions, C# style, and repository hygiene.
- [Runtime contract lifecycle](00_project/contract-lifecycle.md): local API and saved-context requirements.
- [Issue requirements](10_workflow/issue-workflow.md): native types, architecture inputs, and bulk synchronization.
- [Pull request requirements](10_workflow/pr-workflow.md): titles, review evidence, and merge authority.
- [Project validation](00_project/validation.md): current commands and their actual scope.

## Architecture

- [Architecture](20_architecture/architecture.md).
- [Project structure](20_architecture/project-structure.md).
- [Security boundary](20_architecture/security-boundary.md).

## Agent collaboration

[Agent context](50_ai/agent-context.md) combines project context with the shared [Context model](shared/agents/context-model.md) and [Task routing](shared/agents/task-routing.md). Common procedures have one maintained body in the handbook:

- [Design refinement](shared/skills/design-refinement/SKILL.md).
- [Issue refinement](shared/skills/issue-refinement/SKILL.md).
- [Issue publishing](shared/skills/issue-publishing/SKILL.md).
- [Pull request publishing](shared/skills/pr-publishing/SKILL.md).
- [Test validation](shared/skills/test-validation/SKILL.md).

## Roadmap

[First consumable prerelease](90_roadmap/roadmap.md) owns M0 initialization and M1-M5 delivery outcomes, reuse strategy, prerelease compatibility, and the downstream migration boundary.

## Ownership

Solus Book owns shared standards, context guidance, procedures, and templates. SolusAgent owns its adoption and project requirements. Downstream project documents remain background; they do not silently amend SolusAgent's contracts or authorize work in another repository.

The roadmap selects outcomes rather than implementing them. Detailed CI, tests, distribution, and release designs are refined with their owning work. Actual product migration remains downstream work after a consumable prerelease is available.
