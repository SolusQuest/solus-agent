# Runtime contract lifecycle

Apply the shared [Contract lifecycle](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/contract-lifecycle.md) and [Pre-release engineering](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pre-release-engineering.md) to SolusAgent's outer agent API, shared tool API, runtime extension API, and saved-context boundaries.

## Current draft surfaces

`SolusAgent.Api` contains the outer [execution draft](../20_architecture/drafts/agent-execution.md), with an actual test-only synthetic implementation and calling consumer. `SolusAgent.Tools.Api` contains the executable [function-tool draft](../20_architecture/drafts/function-tools.md), with its synthetic CustomTools producer and consumer. The outer [usage draft](../20_architecture/drafts/usage.md) now exchanges limits, nullable measurements, physical attempts and independent accounting claims through the Api-only synthetic consumer. Runtime.Api adds the [provider exchange draft](../20_architecture/drafts/provider-exchange.md), with an actual Runtime.Api-only synthetic CustomProvider producer/Host consumer. Further member families and runtime execution paths remain future work. The responsibilities and reference graph are selected; revise each draft coherently with its actual producers, validators, consumers and tests without a compatibility freeze. Maintain one current production implementation per selected path; intentional agent/provider alternatives and synthetic test implementations are legitimate alternatives.

This document does not select package versions, persistence codecs, or release policy. The [roadmap](../90_roadmap/roadmap.md) owns the first experimental prerelease and its migration-entry scope. Supported package and saved-context commitments can have different boundaries.

The outer [candidate-feedback draft](../20_architecture/drafts/candidate-feedback.md) is another current member family, exercised by the Api-only producer/Host consumer and Candidates tests. Its signatures and producers/consumers evolve together; no persisted Host progress format, effect transaction or supported API version is selected.

The outer [context envelope draft](../20_architecture/drafts/context-envelope.md) is executable through the existing Api-only producer/Host consumer. Its optional interface, explicit intent/admission and restricted sink evolve together without changing accepted ordinary execution semantics. The Host owns capture retention; there is no agent lookup by execution identity or selected durable saved format.

The [runtime configuration and exposure draft](../20_architecture/drafts/runtime-configuration.md) is another executable Runtime.Api member family. Live configuration, receipt values and the actual independent CustomProvider consumer evolve together. It retains the existing AgentRequest, ProviderAttempt and UsageAttemptObservation authorities and selects no durable exposure format, runtime restoration codec or supported API version.

The [managed runtime execution draft](../20_architecture/drafts/runtime-execution.md) adds the first actual implementation. Its observation channel, request-relative response validator and explicit settlement invocation state revise the existing provider/exposure drafts together with their real producers, consumers and tests. A guarded ProviderRequest is now single-use; independent new turns receive new requests and physical identities. No persisted representation or supported compatibility commitment is added.

## Saved context

The self-owned runtime must support complete restoration of its own runtime context. Context compatibility is scoped to its agent implementation and applicable runtime, provider, model, and format constraints.

An outer context envelope does not make different agent implementations' payloads interchangeable. Reject incompatible or invalid restoration explicitly. Starting fresh is a separate caller decision, not a silent recovery path.

The context design covers runtime-owned records and required provider continuation while excluding credentials, live clients, delegates, and tool instances. The host supplies execution capabilities again on restoration and owns storage policy. Follow [Architecture](../20_architecture/architecture.md) and [Security boundary](../20_architecture/security-boundary.md) for the selected restoration and authority requirements.
