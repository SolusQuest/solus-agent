# SolusAgent agent entry

Read these files in order before changing this repository:

1. [Agent context](docs/50_ai/agent-context.md).
2. The shared [Context model](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/agents/context-model.md) (`docs/shared/agents/context-model.md` locally).
3. Shared [Collaboration](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/collaboration.md) (`docs/shared/standards/collaboration.md`), [Source of truth](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/source-of-truth.md) (`docs/shared/standards/source-of-truth.md`), and [Conventions](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/conventions.md) (`docs/shared/standards/conventions.md`), together with the [project conventions](docs/00_project/conventions.md).
4. The relevant shared skill and project documents selected by [task routing](docs/50_ai/agent-context.md#read-by-task).

`AGENTS.md` is the shared root entrypoint. Keep platform-specific entrypoints thin. Shared links open the pinned Book revision on GitHub. In an initialized checkout, read the local files shown above and in task routing; other shared URLs map their path after the commit to `docs/shared/`. Shared procedures live at `docs/shared/skills/<name>/SKILL.md`; their resources resolve from the handbook source tree.

Initialize the pinned handbook with `git submodule update --init --recursive` after checkout. [Shared handbook adoption](docs/00_project/shared-handbook.md) records its source, revision, loading arrangement, and project exceptions. Follow its initialization guidance if shared content is missing.

Follow the current user's task scope and established authorization. Project documents own local requirements; the handbook supplies shared defaults. Use [project validation](docs/00_project/validation.md) for the checks available at the current development stage.
