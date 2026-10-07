# Architecture

This document records the selected architecture. The four production libraries retain the boundaries described in [Project structure](project-structure.md). The outer [execution draft](drafts/agent-execution.md) and [prepared function-tool draft](drafts/function-tools.md) have public contracts and separately compiled synthetic consumers. The production runtime, providers, tool adapters and restoration remain future work. Draft signatures can evolve coherently with their actual producers and consumers.

## Agent and model boundaries

The executable outer [candidate-feedback draft](drafts/candidate-feedback.md) selects an optional `ICandidateAgent` and separate Host payload/feedback channel. Safe completion observations retain independent receipts across stops without importing Host domain validation, effect authority or durable storage.

The outer agent API describes how a product requests execution, observes progress and outcomes, and supplies or receives implementation-scoped context. It must accommodate incremental candidate submission and host acknowledgement where the product needs them.

The self-owned runtime is the initial implementation. Future open-source or hosted agent integrations can implement the same outer contract without adopting its internal loop or model-provider API.

Model-provider replacement is a separate boundary inside the self-owned runtime. That runtime owns its logical execution records, sequencing, tool orchestration, and per-run enforcement. A model adapter owns provider-specific projection, HTTP or SDK transport, error and usage mapping, and required provider continuation materialization.

Keep the outer API independent of self-owned chat message types, model clients, and tool registries. Runtime-specific configuration belongs in `Runtime.Api` or the concrete implementation's startup surface. Backend-specific options for future implementations must not be forced into the self-owned configuration.

## Tool boundary

`Tools.Api` defines a small reusable function-tool contract. Tool implementations expose normalized metadata and invocation behavior. Supporting agent integrations translate that contract into their native declarations and call protocol through a generic adapter.

One adapter normally handles all compatible tools for that integration; it does not require a handwritten mapping for each tool name. Metadata describes names, descriptions, input shapes, and applicable behavior. Input validation or preparation is separate from actual execution so the runtime can admit a call before side effects.

The agent implementation owns call association, scheduling, run budgets, cancellation observation, and recording. Product code owns business semantics and acceptance. Tool metadata is descriptive; a claim such as read-only does not itself enforce authorization.

An integration must validate supported schemas and capabilities rather than silently discard unsupported semantics. A hosted built-in file reader is not equivalent to a product's reviewed-snapshot reader merely because their names match.

The outer agent API must not require every implementation to accept custom tools. Shared tool registration occurs through the selected implementation's configuration surface. A remote callback or MCP bridge can be a later transport adapter when a real integration needs it; it is not required by the initial shared contract.

C# attributes and source generation can later make tool authoring easier. Explicit implementations and generated wrappers must converge on the same metadata and invocation contract. Agent adapters consume that contract, independent of how it was authored. A generator is not required for the initial tool API or adapter.

## Context and restoration

The self-owned runtime must support complete saving and restoration of runtime-owned agent context, including logical conversation and tool records, necessary accounting state, and required provider-scoped continuation.

The product chooses between explicitly starting fresh and restoring supplied context. PR Review can supply saved context; ContractScribe can construct a new initial context from business progress. This lifecycle choice is not a reason to weaken runtime restoration support.

Saved context excludes credentials, live clients, delegates, and tool instances. The host supplies capabilities again. Restoration validates implementation, format, provider, and model compatibility as required by the selected design; invalid or incompatible context fails explicitly rather than silently starting fresh.

The outer envelope identifies the owning implementation and applicable format. It does not make context portable between unrelated agent implementations. The host owns storage, visibility, protection, retention, and deletion. Context is restricted state, not an ordinary log or publication result.

## Budgets and usage

Share run-level admission, enforcement, observation, and accounting mechanics. Products supply policy and defaults and retain campaign-level or cross-attempt business accounting.

Distinguish configured limits, reservations, observed consumption, unknown consumption, and estimated cost. Missing usage is unknown, not zero. Provider-specific cached or reasoning counters retain their documented meaning rather than being fabricated to fit a uniform total.

A retry is a new dispatch with its own admission and accounting. Required replay and provider continuation must survive the model adapter without silent rewriting. These are implementation requirements to validate once the corresponding path exists.

Different whole-agent implementations can offer different budget enforcement, usage visibility, cancellation, tool, and restoration guarantees. Expose those capabilities honestly. A requested limit is not proof that an opaque service enforced it.

## Product and host ownership

Products own authoritative task identities, snapshot selection, business progress, domain evidence checks, candidate acceptance, and final side effects. Product-specific evidence structures and acceptance hooks must not become universal tool results merely because one product uses them.

PR Review's finding validation and GitHub publication remain downstream. ContractScribe's semantic analysis, deterministic patching, campaign state, and GitHub operations remain downstream.

The startup layer constructs the chosen implementation and injects the outer agent interface into business code. A factory or registration entrypoint should make implementation selection straightforward without exposing internal loop and state-management classes.

## Implementation direction

Use ordinary managed .NET as the default. The selected interface boundaries do not require dynamic plugin discovery, a separate worker process, or multiple default build matrices. Add such mechanisms only for an observed extension, isolation, or distribution need.

The first runtime and provider implementations will be derived from the two real consumers under the [roadmap](../90_roadmap/roadmap.md), with synthetic consumer probes protecting the shared contracts. Shared implementation and downstream product migration have separate completion boundaries. Apply [Pre-release engineering](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pre-release-engineering.md) and [Security boundary](security-boundary.md) when refining them.
