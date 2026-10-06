# Issue workflow

Issues are focused execution or decision contracts. Use [Pre-release engineering](../00_project/pre-release-engineering.md) to decide whether separate tracking adds planning, scheduling, coordination, or acceptance value. Bounded work can use the no-issue path in [Pull request workflow](pr-workflow.md).

## Native types

Use the repository's enabled GitHub native type for the primary outcome:

- `Feature`: a new application, agent, runtime, provider, or tool capability.
- `Enhancement`: an improvement to an existing capability.
- `Bug`: broken expected behavior.
- `Task`: planning, documentation, research, tooling, or maintenance.

Verify available types before creating or retyping an issue. These categories describe the desired convention, not a claim that this empty repository has already enabled each type. Do not change repository settings to enable them without authorization.

Keep titles outcome-focused. Do not duplicate the native type as a title prefix or parallel `Type:` body field. A title-, body-, or relationship-only update preserves the current type unless retyping is specifically authorized.

## Execution contract

An executable issue states:

- one primary outcome and useful context;
- bounded scope and exclusions;
- complete acceptance criteria;
- validation and evidence needed for acceptance;
- dependencies and independently unblockable states;
- the expected pull request boundary;
- an owning parent or milestone when it adds coordination value;
- relevant authoritative docs and code paths.

Keep issues self-contained and suitable for repository publication. Do not paste raw task prompts, live logs, transcripts, credentials, or private downstream material.

Split an issue when its outcomes are independently useful and acceptable or require independently unblockable work or distinct authority decisions. Keep code with the tests and documentation needed to accept it. A coordination parent owns the dependency or closure view rather than unbounded implementation.

## Readiness and design

An issue is agent-ready when its outcome, acceptance criteria, scope, dependencies, and validation are clear and no required product or architecture decision remains unresolved.

Refine public APIs, saved context, provider boundaries, tool semantics, budgets, and side-effect authority through [Architecture design refinement](../50_ai/skills/architecture-design-refinement.md) when a material choice remains. Experimental issues state the question and the evidence that can resolve it; an experiment does not promise unobserved production behavior.

Project-boundary work follows [Project structure](../20_architecture/project-structure.md). Contract work follows [Contract lifecycle](../00_project/contract-lifecycle.md).

## Tracker publication and planning changes

Creating, updating, moving, closing, or changing relationships requires authorization for that operation. A local issue draft is not authorization to publish it.

Read current remote state before a mutation and read back the fields actually changed afterward. Issue creation or retyping must set and verify the native type. Do not replay an uncertain write until its result has been reconciled.

Use a reviewed synchronization plan for an authorized bulk milestone or dependency-graph migration whose partial application would leave ambiguous ownership. The plan covers each changed object and relationship; verification covers the complete changed graph. Ordinary body corrections require only target and changed-field readback.

Governing long-lived planning decisions belong in repository docs. Publish an accepted bulk graph against the applicable maintained documentation, preserving closed historical evidence. Detailed roadmap and cross-repository migration planning remain outside initialization.

Follow [Issue publishing](../50_ai/skills/issue-publishing.md) for the operational procedure.
