# Project context

## Purpose

SolusAgent provides shared agent boundaries for agentic-pr-review, ContractScribe, and future applications. It is intended to remove duplicate runtime mechanics while preserving each application's product semantics and authority.

The repository owns its implementation, shared contracts, documentation, and future planning. Downstream repositories own their integration, product defaults, domain validation, and releases.

## Current position

The current tree is an initialization skeleton: four .NET libraries, their selected dependency graph, and collaboration documentation. It contains no public API types, executable agent loop, model provider, tool implementation, tests, or released distribution. Build success is not evidence that the selected runtime behavior exists.

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

## Deferred planning

Roadmap milestones, detailed migration timing, test infrastructure, CI, NuGet and release policy, additional agent implementations, and source generators will be refined separately. The initial projects reserve the agreed current boundaries only; they do not authorize implementing those later features.
