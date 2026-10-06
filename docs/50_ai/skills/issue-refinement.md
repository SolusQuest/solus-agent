# Issue refinement

Before refining work, read [Issue workflow](../../10_workflow/issue-workflow.md), [Pre-release engineering](../../00_project/pre-release-engineering.md), and the relevant architecture or accepted planning document.

1. Decide whether a separate issue adds useful planning, coordination, scheduling, or acceptance value. A bounded coherent change can use the no-issue path.
2. Identify one primary outcome and choose its native issue type. Keep the title outcome-focused.
3. State scope, exclusions, complete acceptance criteria, validation, dependencies, and the expected pull request boundary.
4. Resolve required design choices, or separate a genuinely independent decision or experiment before implementation depends on it.
5. Keep implementation with the contracts, tests, fixtures, and documentation needed to accept the outcome. Split only independently useful and acceptable work.
6. For project changes, identify the allowed reference graph and introduced dependencies. For contract changes, identify actual producers, consumers, and compatibility commitments.
7. Record a short exception only if an actual extra review, merge, mutation, compatibility path, or validation mechanism exceeds the proportional default.

A refinement draft does not authorize publishing, changing milestone scope, moving relationships, or closing an issue. When authorized publication follows, use [Issue publishing](issue-publishing.md) and preserve the identity of an existing issue.
