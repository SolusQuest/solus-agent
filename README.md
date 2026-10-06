# SolusAgent

SolusAgent is a shared .NET agent project being established for [agentic-pr-review](https://github.com/SolusQuest/agentic-pr-review), [ContractScribe](https://github.com/SolusQuest/contract-scribe), and future agent applications.

The selected design separates the application-facing agent API, reusable tool contracts, self-owned runtime extension contracts, and the runtime implementation. Applications retain their product policies, result acceptance, persistence decisions, and platform side effects.

## Current state

The repository contains collaboration documentation and a buildable four-project skeleton. Public interface signatures and executable agent behavior have not been implemented. The architectural requirements below are the selected design, not claims of available functionality.

CI, test infrastructure, package distribution, version policy, roadmap milestones, downstream migration, additional agent implementations, and tool source generators remain for subsequent planning.

## Projects

| Project | Selected responsibility |
| --- | --- |
| `SolusAgent.Api` | Application-facing agent execution contracts, events, results, usage, capabilities, and context envelopes. |
| `SolusAgent.Tools.Api` | Runtime-independent function-tool contracts and metadata. |
| `SolusAgent.Runtime.Api` | Self-owned runtime extension contracts, including model providers and runtime configuration. |
| `SolusAgent.Runtime` | Self-owned agent execution and integration with the shared tool contracts. |

Project, assembly, and root namespace names match. Package metadata and publication policy will be defined separately when distribution is planned.

## Local build

Use the .NET SDK selected by `global.json`. From the repository root:

```text
dotnet restore SolusAgent.slnx
dotnet build SolusAgent.slnx --configuration Release --no-restore
```

These commands require no model credentials. There is no test suite or CI workflow at this stage; a successful build proves project construction and compilation only.

## Documentation

Start with the [documentation index](docs/README.md), [project context](docs/00_project/project-context.md), [architecture](docs/20_architecture/architecture.md), and [project structure](docs/20_architecture/project-structure.md).

Agents start at [AGENTS.md](AGENTS.md).
