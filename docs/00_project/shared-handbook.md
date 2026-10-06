# Shared handbook adoption

SolusAgent consumes Solus Book for common engineering standards, agent context guidance, procedures, and templates. Project architecture, current state, commands, and local workflow requirements remain owned here.

## Selected source

| Field | Selection |
| --- | --- |
| Source repository | [SolusQuest/solus-book](https://github.com/SolusQuest/solus-book). |
| Location and acquisition | Git submodule `solus-book` at `docs/shared/`, configured in [`.gitmodules`](../../.gitmodules). |
| Adopted revision | `c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67`. The Gitlink pins the content; ordinary checkout does not select upstream's latest branch. |
| Entrypoint | Root [AGENTS.md](../../AGENTS.md), followed by [project agent context](../50_ai/agent-context.md) and shared context/rules. |
| Skill loading | Explicit links in project task routing and shared [Task routing](../shared/agents/task-routing.md) load the five canonical `SKILL.md` sources. Resources resolve from their handbook directories. |
| Project checks | [Project validation](validation.md) owns commands and available acceptance claims. |

## Checkout and updates

After an ordinary clone or checkout, run from the repository root:

```text
git submodule update --init --recursive
```

A new clone can use `git clone --recurse-submodules <repository-url>`. The initialized handbook and all skill resources are available without another local checkout. Local uncommitted changes remain working-tree preparation until the maintainer authorizes commit or publication.

For an authorized handbook update, fetch the source, inspect the selected full commit and relevant semantic/path changes, and check out that commit in `docs/shared/`. Update the Gitlink and this adopted revision together. Recheck local exceptions, incoming links, resource loading, and representative affected workflows. Keep imported rule bodies maintained upstream rather than editing an independent local copy. Source selection and updates retain the task's scope and authority.

If initialization or a required resource fails, follow shared [Loading failures](../shared/agents/context-model.md#loading-failures). Obtain the selected source or missing guidance before dependent operations; continue independent work covered by available applicable rules.

## Local requirements and deduplication

| Previous surface | Shared coverage and retained project requirements |
| --- | --- |
| Source of truth and pre-release engineering documents | Shared standards own the common rules. Project context, architecture, and roadmap retain current state and selected decisions; the runtime lifecycle retains the single current production path. |
| Conventions and contract lifecycle | Shared standards own general content/lifecycle rules. Local documents retain English/C# style, publication suitability, license-status wording, API boundaries, and saved-context requirements. |
| Issue and PR workflows | Shared workflows and skills own common execution. Local workflow documents retain native type mapping, conventional titles, `None` tracking text, project evidence, reviewed bulk synchronization, and the agent merge restriction. |
| Collaboration layers and five local procedures | Shared context and five canonical skills replace copied bodies. Project agent context retains task-specific reading, downstream design inputs, and local placement; project validation retains commands and prerequisites. |

SolusAgent requires repository content to be suitable for publication even where shared conventions permit private project material. Agents do not merge PRs under the local workflow. These project requirements remain explicit exceptions to broader shared defaults. Product security, complete runtime restoration, and the M0-M5 roadmap retain their owning local documents.

No product or authority requirement is intentionally changed by deduplication. Shared clarifications about loading failures, task material, documentation consumers, and adjacent defect correction apply within the established project scope.

## Observed adoption checks

Local validation on 2026-10-06 used PowerShell 7.6.5 and Git 2.52.0.windows.1 on the adoption working tree:

- The submodule checkout and parent Gitlink matched the selected full commit, and the handbook working tree was clean.
- UTF-8/LF/final-newline and whitespace checks passed for 45 text files. All 284 local Markdown links outside code examples resolved, including 32 anchor references across 40 Markdown files.
- Project task routing exposed all five canonical skills. Their basic metadata and all 16 referenced resources were readable from the selected source location. This Codex session read the root entrypoint, project context, and five shared skill bodies through file tools.
- A disposable local clone received the proposed parent working-tree files and Gitlink without creating a commit. Running `git submodule update --init --recursive` obtained the selected handbook commit from its published remote. The same link, text, routing, and resource checks passed there without a sibling source checkout.
- Semantic review accounted for the deleted common documents and retained local requirements. Source, solution, build configuration, project references, and product security requirements were unchanged; architecture and roadmap changes only updated shared-document links and documentation layout.

These checks validate the explicit entrypoint/task-routing arrangement and its source acquisition. Native skill-catalog discovery, automatic loading in a fresh conversation, other harnesses, and other platforms were not exercised. No runtime build, tests, CI, remote PR review, or release readiness is claimed for this documentation-only change. Raw local evidence remains in ignored locations.
