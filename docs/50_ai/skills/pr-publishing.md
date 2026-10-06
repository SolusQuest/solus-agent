# Pull request publishing

Use this procedure when pull request publication is authorized. Follow [Pull request workflow](../../10_workflow/pr-workflow.md) and [Pre-release engineering](../../00_project/pre-release-engineering.md).

## Prepare

Inspect the repository, branch or worktree, working tree, actual diff, tracking issue when useful, and validation evidence. Preserve any task instruction to leave work uncommitted or local; preparation alone does not authorize commit, push, or publication.

Use the no-issue path when its conditions hold. Otherwise keep the issue's primary outcome, accepted scope, dependencies, and review boundary attached to the work.

## Publish

Publish a draft pull request by default unless the task authorizes ready-for-review status. Use a conventional title and describe the final change for a reviewer who has not seen the conversation.

The body identifies the concrete problem and resulting behavior, actual validation, tracking reference or no-issue justification, and material limitations. Explain breaking compatibility only for affected boundaries. Use a structured body argument or body file rather than shell-built multiline text.

Inspect staged and published content for unintended files, private data, credentials, machine-local records, and unsupported implementation or release claims. Do not include build output or task-session artifacts.

## Verify

Read back the pull request target, base, actual head, title, body, and state. Read current checks and review feedback before reporting remote readiness. Local build evidence is separate from required CI and independent review.

Preserve unresolved findings and their current owner. A timeout, cancellation, acknowledgement, or work-in-progress response is not a completed review. After a correction, verify affected findings and behavior without restarting unrelated review or validation.

Publishing does not authorize merge, release, deployment, or tracker closure. Report the reviewable result and remaining decisions to the maintainer.
