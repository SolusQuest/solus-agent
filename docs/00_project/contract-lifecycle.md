# Runtime contract lifecycle

Apply the shared [Contract lifecycle](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/contract-lifecycle.md) and [Pre-release engineering](https://github.com/SolusQuest/solus-book/blob/c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67/standards/pre-release-engineering.md) to SolusAgent's outer agent API, shared tool API, runtime extension API, and saved-context boundaries.

## Current draft surfaces

`SolusAgent.Api` contains the outer [execution draft](../20_architecture/drafts/agent-execution.md), with an actual test-only synthetic implementation and calling consumer. `SolusAgent.Tools.Api` contains the executable [function-tool draft](../20_architecture/drafts/function-tools.md), with its synthetic CustomTools producer and consumer. Other member families and the runtime remain future work. The responsibilities and reference graph are selected; revise each draft coherently with its actual producers, validators, consumers and tests without a compatibility freeze. Maintain one current production implementation per selected path; intentional agent/provider alternatives and synthetic test implementations are legitimate alternatives.

This document does not select package versions, persistence codecs, or release policy. The [roadmap](../90_roadmap/roadmap.md) owns the first experimental prerelease and its migration-entry scope. Supported package and saved-context commitments can have different boundaries.

## Saved context

The self-owned runtime must support complete restoration of its own runtime context. Context compatibility is scoped to its agent implementation and applicable runtime, provider, model, and format constraints.

An outer context envelope does not make different agent implementations' payloads interchangeable. Reject incompatible or invalid restoration explicitly. Starting fresh is a separate caller decision, not a silent recovery path.

The context design covers runtime-owned records and required provider continuation while excluding credentials, live clients, delegates, and tool instances. The host supplies execution capabilities again on restoration and owns storage policy. Follow [Architecture](../20_architecture/architecture.md) and [Security boundary](../20_architecture/security-boundary.md) for the selected restoration and authority requirements.
