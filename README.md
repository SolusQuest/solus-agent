# SolusAgent

SolusAgent is a shared .NET agent project being established for [agentic-pr-review](https://github.com/SolusQuest/agentic-pr-review), [ContractScribe](https://github.com/SolusQuest/contract-scribe), and future agent applications.

The selected design separates the application-facing agent API, reusable tool contracts, self-owned runtime extension contracts, and the runtime implementation. Applications retain their product policies, result acceptance, persistence decisions, and platform side effects.

## Current state

The repository contains four production libraries in the selected dependency graph, an executable outer [execution draft](docs/20_architecture/drafts/agent-execution.md) and [prepared function-tool draft](docs/20_architecture/drafts/function-tools.md), separate Api-only custom-agent and CustomTools consumer libraries, one managed contract test runner, and ordinary push/pull_request CI. The execution draft expresses bounded requests, capabilities, correlated progress and truthful terminal outcomes. The tool draft supplies bounded metadata, effect-free preparation and explicit capability-authorized invocation. Production runtime behavior, providers/tool adapters, other selected API member families and distribution remain future work.

The outer [candidate-feedback draft](docs/20_architecture/drafts/candidate-feedback.md) adds individually correlated submissions, independent Host decisions and continuation instructions, bounded follow-on work and retained acknowledgement observations. Its actual Api-only synthetic producer/Host consumer and Candidates tests keep candidate payloads separate from ordinary diagnostics and completion. Host domain acceptance and effects remain downstream.

The [roadmap](docs/90_roadmap/roadmap.md) selects M0 plus five delivery milestones toward the first downstream-consumable 0.x experimental prerelease. M1 contracts remain drafts, M2 separates deterministic Scripted Provider validation from the actual provider adapter, and M5 records prerelease compatibility policy and verified support scope without freezing the API. Current tests check compilation, evaluated project boundaries, synthetic outer execution and actual synthetic function-tool semantics; runtime implementation, packaging and release work remain future work, and actual product migration belongs to the downstream repositories.

## Projects

| Project | Selected responsibility |
| --- | --- |
| `SolusAgent.Api` | Application-facing agent execution contracts, events, results, usage, capabilities, and context envelopes. |
| `SolusAgent.Tools.Api` | Runtime-independent function-tool contracts and metadata. |
| `SolusAgent.Runtime.Api` | Self-owned runtime extension contracts, including model providers and runtime configuration. |
| `SolusAgent.Runtime` | Self-owned agent execution and integration with the shared tool contracts. |

Project, assembly, and root namespace names match. Package metadata and publication policy will be defined separately when distribution is planned.

## Checkout

The shared engineering handbook is a pinned Git submodule. After checkout, initialize it with:

```text
git submodule update --init --recursive
```

A fresh clone can use `git clone --recurse-submodules <repository-url>`. See [shared handbook adoption](docs/00_project/shared-handbook.md) for the selected revision, loading, and updates.

## Local build

Use the .NET SDK selected by `global.json`. From the repository root:

```text
dotnet restore SolusAgent.slnx
dotnet build SolusAgent.slnx --configuration Release --no-restore
dotnet test tests/SolusAgent.ContractTests/SolusAgent.ContractTests.csproj --configuration Release --no-build
```

These commands require no model credentials. A successful build proves compilation, including both independent consumer libraries. Architecture tests check evaluated project boundaries; Execution and Tools tests check their actual synthetic contract paths. `.github/workflows/ci.yml` runs the same commands on push and pull_request. See [project validation](docs/00_project/validation.md) for the exact scope and limits.

## Documentation

Start with the [documentation index](docs/README.md), [project context](docs/00_project/project-context.md), [architecture](docs/20_architecture/architecture.md), [project structure](docs/20_architecture/project-structure.md), and [roadmap](docs/90_roadmap/roadmap.md).

Agents start at [AGENTS.md](AGENTS.md).
