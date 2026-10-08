# Documentation

## Shared engineering handbook

SolusAgent consumes a pinned Solus Book submodule at `docs/shared/`. [Shared handbook adoption](00_project/shared-handbook.md) records initialization, updates, loading, and the local rules retained during deduplication.

Shared links below open the adopted Book commit on GitHub. In an initialized checkout, place the URL's path after the commit under `docs/shared/` to read the same file locally.

- [Collaboration](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/collaboration.md).
- [Source of truth](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/source-of-truth.md).
- [Pre-release engineering](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pre-release-engineering.md).
- [Contract lifecycle](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/contract-lifecycle.md).
- [Conventions](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/conventions.md).
- [Issue workflow](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/issue-workflow.md).
- [Pull request workflow](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pr-workflow.md).
- [Validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/validation.md).

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
- [Candidate feedback draft](20_architecture/drafts/candidate-feedback.md): individually correlated Host submissions and retained acknowledgement observations.
- [Run limits and usage draft](20_architecture/drafts/usage.md).
- [Provider exchange draft](20_architecture/drafts/provider-exchange.md).
- [DeepSeek provider draft](20_architecture/drafts/deepseek-provider.md): bounded thinking/tool HTTP exchange, restricted in-run replay and invocation-owned observations.
- [Runtime configuration and exposure draft](20_architecture/drafts/runtime-configuration.md): live Host bindings and ordered same-attempt acknowledgement/closure.
- [Managed runtime execution draft](20_architecture/drafts/runtime-execution.md): public construction, the first production provider turn, whole-run local cuts and retained observations.

- [Context envelope draft](20_architecture/drafts/context-envelope.md): explicit intent/admission and call-scoped restricted capture with Host-owned retention.

## Agent collaboration

[Agent context](50_ai/agent-context.md) combines project context with the shared [Context model](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/agents/context-model.md) and [Task routing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/agents/task-routing.md). Common procedures have one maintained body in the handbook:

- [Design refinement](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/design-refinement/SKILL.md).
- [Issue refinement](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/issue-refinement/SKILL.md).
- [Issue publishing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/issue-publishing/SKILL.md).
- [Pull request publishing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/pr-publishing/SKILL.md).
- [Test validation](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/test-validation/SKILL.md).

## Roadmap

[First consumable prerelease](90_roadmap/roadmap.md) owns M0 initialization and M1-M5 delivery outcomes, reuse strategy, prerelease compatibility, and the downstream migration boundary.

## Ownership

Solus Book owns shared standards, context guidance, procedures, and templates. SolusAgent owns its adoption and project requirements. Downstream project documents remain background; they do not silently amend SolusAgent's contracts or authorize work in another repository.

The roadmap selects outcomes rather than implementing them. Detailed CI, tests, distribution, and release designs are refined with their owning work. Actual product migration remains downstream work after a consumable prerelease is available.
