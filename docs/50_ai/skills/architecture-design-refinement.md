# Architecture design refinement

Use this procedure when a material choice remains in the outer agent API, shared tool API, runtime extension contract, saved context, model exchange, budget semantics, or external authority.

Read [Architecture](../../20_architecture/architecture.md), [Project structure](../../20_architecture/project-structure.md), [Security boundary](../../20_architecture/security-boundary.md), and [Pre-release engineering](../../00_project/pre-release-engineering.md).

## Decision-ready result

Produce a bounded design summary covering:

- the concrete decision and affected consumer;
- the recommended option and meaningful alternatives;
- the rationale and available evidence;
- affected contracts and dependency direction;
- product ownership, security, and persisted-state consequences;
- validation that can distinguish correct behavior;
- unresolved choices and the document that will own the accepted decision.

Start from the already accepted boundary. Do not reopen an agreed naming or dependency decision merely because its implementation has not begun. Ordinary internal implementation choices can be made within the current task's authorization.

## Evidence and scope

For a durable external or irreversible boundary, use the bounded precedent check in the pre-release rule. For an uncertain executable behavior, prefer a small synthetic experiment using the real relevant path. State what the experiment can and cannot prove.

Validate against both actual downstream needs where they affect the shared contract. Preserve runtime mechanics in SolusAgent and product policies in the product. Do not make the first consumer's evidence or progress types universal simply by moving them into an API assembly.

Refine only the boundary needed by current implementation. Additional agent backends, plugin discovery, remote tool transports, source generators, and release machinery need their own concrete requirements; their possible future existence is not a reason to build them during initialization.
