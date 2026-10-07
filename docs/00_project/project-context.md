# Project context

## Purpose

SolusAgent provides shared agent boundaries for agentic-pr-review, ContractScribe, and future applications. It is intended to remove duplicate runtime mechanics while preserving each application's product semantics and authority.

The repository owns its implementation, shared contracts, documentation, and future planning. Downstream repositories own their integration, product defaults, domain validation, and releases.

## Current position

The current tree contains four .NET production libraries in their selected dependency graph, an executable outer [execution draft](../20_architecture/drafts/agent-execution.md) and [prepared function-tool draft](../20_architecture/drafts/function-tools.md), separate Api-only custom-agent and CustomTools consumer libraries, one managed contract test runner, and ordinary push/pull_request CI. Execution tests demonstrate synthetic bounded work, capability admission, terminal outcomes and diagnostic separation. The [usage draft](../20_architecture/drafts/usage.md) links requested limits and immutable observations to execution, with actual synthetic retry/failure/cancellation paths. Tools tests exercise actual preparation/invocation semantics, and architecture tests evaluate the project boundaries. There is no production agent loop, model provider, production tool adapter or released distribution. These drafts and synthetic checks do not establish the selected runtime's later guarantees.

The executable [candidate-feedback draft](../20_architecture/drafts/candidate-feedback.md) extends the outer boundary with individually correlated Host exchanges and bounded repairs/continuations. Its Api-only synthetic producer/Host consumer demonstrates independent acknowledgement retention and diagnostic confinement; it supplies no production loop or Host domain policy.

The executable [context envelope draft](../20_architecture/drafts/context-envelope.md) adds implementation-scoped opaque state, explicit fresh/new-run/continuation choices and fail-closed admission. Its actual Api-only producer/Host consumer proves restricted call-scoped capture and current Host control/data separation; it supplies no runtime restoration codec or recovery guarantee.

## Accepted design direction

- Use the name `SolusAgent` and the four projects defined in [Project structure](../20_architecture/project-structure.md).
- Provide an application-facing agent interface from the start. The self-owned runtime is the first implementation; hosted and open-source agent integrations can implement the same outer contract later.
- Keep model-provider contracts specific to the self-owned runtime. A downstream provider implementation should not need the runtime implementation assembly.
- Keep reusable function-tool contracts in an independent assembly. Each supporting agent integration adapts that contract generically from tool metadata and invocation interfaces.
- Use managed .NET without mandatory Native AOT or trimming. A separate process or dynamic plugin framework requires a concrete need and its own design decision.
- The self-owned runtime must support complete saving and restoration of runtime-owned agent context. The product chooses whether to restore supplied context or start fresh from business progress.
- Share runtime budget enforcement and usage observations while retaining product budget policy, defaults, and cumulative business accounting downstream.
- Preserve product-controlled candidate acceptance and acknowledgement; the outer agent design must not assume that every product is a single prompt followed by one final JSON response.

These decisions select boundaries and required behavior. They do not freeze interface signatures, JSON shapes, persistence codecs, provider dependencies, or distribution mechanisms.

## Product ownership

Products retain trusted task identities, repository snapshot selection, business progress, result acceptance, storage policy, and external mutations. Shared code must not inherit PR-review-only evidence rules or ContractScribe-only document and campaign semantics.

Read [Architecture](../20_architecture/architecture.md) and [Security boundary](../20_architecture/security-boundary.md) before implementation.

## Delivery planning

The [roadmap](../90_roadmap/roadmap.md) selects M0 initialization and M1-M5 delivery toward a first consumable 0.x experimental prerelease. Draft contracts evolve with implementation; ordinary validation uses a Scripted Provider, while one actual provider adapter is included; the first release declares prerelease compatibility policy and verified support scope without a compatibility freeze. It enables the two downstream repositories to begin migration rather than requiring their migrations to close SolusAgent's release milestone.

Execution, usage and tool signatures are current executable drafts; other public signatures, context formats, package layout and release mechanics are refined with their owning work. Additional agent implementations and source generators remain later work. The current tree does not deliver all roadmap outcomes, and a planning task does not authorize implementing or publishing those features.
