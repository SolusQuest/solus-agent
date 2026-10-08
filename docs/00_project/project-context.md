# Project context

## Purpose

SolusAgent provides shared agent boundaries for agentic-pr-review, ContractScribe, and future applications. It is intended to remove duplicate runtime mechanics while preserving each application's product semantics and authority.

The repository owns its implementation, shared contracts, documentation, and future planning. Downstream repositories own their integration, product defaults, domain validation, and releases.

## Current position

The current tree contains four core .NET production libraries and an optional DeepSeek adapter in their selected dependency graph, an executable outer [execution draft](../20_architecture/drafts/agent-execution.md) and [prepared function-tool draft](../20_architecture/drafts/function-tools.md), separate Api-only custom-agent, CustomTools, CustomProvider, ScribeHost and AprHost consumer libraries, one managed contract test runner, and ordinary push/pull_request CI. Execution tests demonstrate synthetic bounded work, capability admission, terminal outcomes and diagnostic separation. The [usage draft](../20_architecture/drafts/usage.md) links requested limits and immutable observations to execution, with actual synthetic retry/failure/cancellation paths. Tools tests exercise actual preparation/invocation semantics, and architecture tests evaluate the project boundaries. The optional [DeepSeek provider draft](../20_architecture/drafts/deepseek-provider.md) executes actual bounded HTTP projection/parsing and exact replay with controlled synthetic transport evidence. The production Runtime executes bounded provider and generic [tool turns](../20_architecture/drafts/runtime-tools.md); the separately selected [candidate driver](../20_architecture/drafts/runtime-candidates.md) executes bounded Final-response feedback and composes tool batches inside its candidate productions. Product-specific tools and released distribution remain future work. These drafts and synthetic checks do not establish the selected runtime's later guarantees.

The executable [candidate-feedback draft](../20_architecture/drafts/candidate-feedback.md) extends the outer boundary with individually correlated Host exchanges and bounded repairs/continuations. Its Api-only synthetic producer/Host consumer demonstrates independent acknowledgement retention and diagnostic confinement; it supplies no production loop or Host domain policy.

The [provider exchange draft](../20_architecture/drafts/provider-exchange.md) is executable through the Runtime.Api-only CustomProvider fixture and Providers tests. It preserves bounded classified input, complete tool-round association, retained observation after failure and restricted exact continuation. The optional DeepSeek adapter independently implements that seam.

The executable [context envelope draft](../20_architecture/drafts/context-envelope.md) adds implementation-scoped opaque state, explicit fresh/new-run/continuation choices and fail-closed admission. Its actual Api-only producer/Host consumer proves restricted call-scoped capture and current Host control/data separation; it supplies no runtime restoration codec or recovery guarantee.

The executable [runtime configuration and exposure draft](../20_architecture/drafts/runtime-configuration.md) adds live Host provider/interface-tool bindings and correlated pre-dispatch acknowledgement and post-attempt closure in Runtime.Api. Its independent CustomProvider consumer demonstrates cancellation cuts, explicit retries, retained usage and control/data separation. Current AgentRequest controls remain authoritative; Host durable claims are not storage proof, and the bounded production Runtime is described in the [runtime execution draft](../20_architecture/drafts/runtime-execution.md).

The [Scribe consumption draft](../20_architecture/drafts/scribe-consumption.md) is executable through the independent Api-only ScribeHost business Host and the runner's `ConsumerProbes/Scribe` startup. It reconstructs fresh input from validated business progress and demonstrates independent candidate acknowledgement, bounded repair and restricted diagnostics through the accepted draft surfaces. Business checkpoint/campaign accounting, storage and product acceptance remain downstream; the Runtime/Consumption startup now also composes this Host with the production runtime on scripted transport, while budget, real provider transport, restoration and migration behavior remain unproved.

## Accepted design direction

The [managed runtime execution draft](../20_architecture/drafts/runtime-execution.md) implements public construction and the first production provider turn. Its actual runtime tests use the independent Scripted Provider for non-cooperative cut, observation/closure, original-request bounds and concurrent isolation proof. Existing M1 fixture acceptance remains distinct from this new production evidence.

- Use the name `SolusAgent` and the four core projects and optional adapter defined in [Project structure](../20_architecture/project-structure.md).
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
