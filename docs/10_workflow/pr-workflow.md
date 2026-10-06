# Project pull request requirements

Use the shared [Pull request workflow](../shared/standards/pr-workflow.md) and [Pull request publishing](../shared/skills/pr-publishing/SKILL.md) with the following project requirements.

## Branches and review text

Substantive changes normally use a branch and a draft pull request. Respect a task instruction to keep work local or uncommitted.

Use English and the conventional title format `<type>(<scope>): <summary>`. The body explains the final problem and resulting behavior, actual validation and material limitations. Identify the tracking issue, or `None` with a short no-issue justification under the shared no-issue conditions. Apply [project conventions](../00_project/conventions.md) to all staged and published content.

## Project evidence

Use [Project validation](../00_project/validation.md) together with shared validation rules. For project changes, show the resulting reference graph and classify introduced dependencies. For contract changes, identify actual affected producers, consumers, and persisted or supported compatibility boundaries under [Runtime contract lifecycle](../00_project/contract-lifecycle.md).

Local checks, current-head CI, independent review, and release qualification support different claims. Keep required checks, feedback, and finding history attached to the actual reviewed head under the shared workflow.

## Merge authority

Agents do not merge pull requests. The maintainer owns merge and release decisions. Publication does not authorize release, deployment, tracker closure, or repository settings changes.
