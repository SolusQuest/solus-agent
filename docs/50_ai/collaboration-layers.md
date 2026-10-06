# Collaboration layers

The repository uses the same three-layer collaboration structure as its initial downstream consumers. `AGENTS.md` is the shared root entrypoint.

## 1. Project rules

Rules shared by humans and agents live under `docs/00_project`, `docs/10_workflow`, and `docs/20_architecture`. Future roadmap rules will live under `docs/90_roadmap` when that planning is performed.

These documents own project purpose, conventions, workflow, architecture, security, and compatibility principles. They distinguish selected requirements from available implementation.

## 2. Shared agent context and procedures

Tool-neutral context lives in `docs/50_ai/agent-context.md` and this document. Task procedures live in `docs/50_ai/skills`.

Procedures route agents to project rules and describe how to carry out design refinement, issue work, pull request publishing, and applicable validation. They should not duplicate entire project-rule documents or invent additional approval stages.

## 3. Platform-specific entrypoints

Thin platform entrypoints such as `CLAUDE.md` point to `AGENTS.md` and add only platform-specific requirements when needed. No additional Codex entrypoint is required for the current project.

Machine-local notes, real session identities, and task execution artifacts belong in ignored locations. Add a shared platform-specific file only when an actual maintained behavior needs it, rather than committing another tool's private working records.

## Placement rule

- If humans and agents must both follow a rule, put it in the owning project document.
- If it is a tool-neutral task procedure, put it in `docs/50_ai/skills`.
- If it is specific to one agent platform, keep it in a thin platform entrypoint or explicitly maintained platform surface.

SolusAgent owns its copies of these rules. A downstream rule applies here only when it has been deliberately adapted into the appropriate layer or is supplied by the current task.
