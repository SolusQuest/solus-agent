# Pull request workflow

After the maintainer establishes the initial Git baseline, substantive changes normally use a branch and a draft pull request. Respect any task instruction to keep work local or uncommitted. Local preparation, commit, push, and pull request publication are separate actions with their own task authorization.

## Reviewable changes

Use a conventional title and a self-contained body that explains:

- the concrete problem and resulting behavior;
- the tracking issue, or `None` with a short no-issue justification;
- validation actually performed and its scope;
- material limitations, open findings, and breaking behavior when relevant.

Do not claim passing tests, CI, release readiness, or consumer compatibility without the corresponding evidence. Do not copy raw prompts, private source, live provider responses, credentials, or machine-local records into the body.

## No-issue path

A change can proceed without a separate issue when it is bounded, complete in one review cycle, requires no independent scheduling or coordination, and contains no unresolved product decision, external commitment, authority change, dependency-graph change, or multi-PR outcome.

An explicitly authorized initialization or focused maintenance correction can fit that path. If the work expands beyond it, refine the new scope before marking the pull request ready.

## Validation and review

Follow [Validation](../50_ai/skills/test-validation.md). Validate the changed failure surface and related invariants. Reuse evidence for unchanged inputs; after a fix, refresh what it invalidates rather than repeating unrelated checks.

For project changes, show the resulting reference graph and classify introduced dependencies. For contract changes, identify the actually affected producers, consumers, and persisted or supported compatibility boundary. Update current documentation coherently.

Review findings should be resolved across their admitted behavior and neighboring affected cases, not only the cited example. Preserve the finding history and distinguish an accepted fix from a proposed or incomplete one. Read back current remote head, checks, and feedback before reporting remote readiness.

Apply [Pre-release engineering](../00_project/pre-release-engineering.md) when an extra review, validation mechanism, compatibility path, or mutation stage is proposed. Ordinary changes do not need an additional process checklist.

## Authority

Agents do not merge pull requests. The maintainer owns merge and release decisions. Publishing a pull request does not authorize release, deployment, tracker closure, or repository settings changes.

Use [Pull request publishing](../50_ai/skills/pr-publishing.md) when publication is authorized.
