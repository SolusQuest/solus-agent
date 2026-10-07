# Project issue requirements

Use the shared [Issue workflow](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/issue-workflow.md), [Issue refinement](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/issue-refinement/SKILL.md), and [Issue publishing](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/issue-publishing/SKILL.md). Apply [project conventions](../00_project/conventions.md) to issue language and publication content.

## Native types

Use the repository's enabled GitHub native type for the primary outcome:

- `Feature`: a new application, agent, runtime, provider, or tool capability.
- `Enhancement`: an improvement to an existing capability.
- `Bug`: broken expected behavior.
- `Task`: planning, documentation, research, tooling, or maintenance.

Verify available types before creating or retyping an issue. These categories describe the desired convention, not a claim that each type is enabled. Do not change repository settings to enable them without authorization.

Keep titles outcome-focused. A title-, body-, or relationship-only update preserves the current type unless retyping is authorized. If a client cannot set or verify a required native type, preserve the draft or partial result and report the missing operation; do not substitute a title prefix or body field.

## Project design and acceptance

For public APIs, saved context, provider boundaries, tool semantics, budgets, and side-effect authority, use [Design refinement](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/skills/design-refinement/SKILL.md) with the project inputs selected by [Agent context](../50_ai/agent-context.md#read-by-task).

Project-boundary work identifies the allowed reference graph and introduced dependencies under [Project structure](../20_architecture/project-structure.md). Contract work identifies actual producers, consumers, and retained or supported compatibility boundaries under [Runtime contract lifecycle](../00_project/contract-lifecycle.md). Include these inputs in the issue's acceptance and implementation notes where affected.

The [roadmap](../90_roadmap/roadmap.md) owns shared delivery outcomes. Product acceptance, actual cross-repository migration, and downstream releases retain their downstream owners.

## Bulk planning changes

Use a reviewed synchronization plan for an authorized bulk milestone or dependency-graph migration whose partial application would leave ambiguous ownership. The plan covers each changed object and relationship and its application order; verification covers the complete changed graph. Ordinary text corrections require only target and changed-field readback.

Publish an accepted bulk graph against the applicable maintained documentation, preserving closed historical evidence. Tracker writes and settings changes retain the task's operation-specific authority.
