# Security boundary

These rules govern the outer execution draft and future implementation. Current synthetic tests exercise Host control/data separation and ordinary diagnostic confinement; the production runtime, authentication, context protection, provider transport and tool admission remain unimplemented.

## Host authority

The application host owns trusted task configuration, execution identity, available capabilities, and external mutation authority. The model or agent output cannot select an endpoint, expand credentials, authorize new tools, or redefine its acceptance rules.

The generic library can support deliberately supplied capabilities; product rules determine which tools and effects are admissible. PR Review's read-only review boundary must remain in its downstream policy rather than becoming an accidental universal restriction on every agent application.

## Untrusted input

Repository files, pull request text, search results, tool results, model output, and restored conversation content are untrusted data. Their association and control/data classification must survive recording, restoration, and provider projection.

Do not promote data into policy, system or developer instructions, tool definitions, endpoint selection, or provider configuration. Trusted policy must come from the host's authorized source rather than model-controlled or reviewed content.

Tool execution must validate actual arguments and capabilities. Descriptor attributes are useful for discovery and adaptation, but are not proof of safety or permission. Product validation remains responsible for domain evidence and accepted effects.

## Credentials and capabilities

Keep provider credentials encapsulated by the provider transport. Keep platform credentials and storage keys within their host-owned components. Do not place credential bytes in tool definitions, model-visible input, saved context, ordinary events, logs, traces, or publication results.

Supply narrow execution capabilities. The core loop must not obtain arbitrary services, host environment access, platform clients, or generic process and network authority merely because it shares a process with the host.

## Restricted context

Provider-scoped continuation may require exact replay of bounded opaque or structured fields. Preserve documented replay material without requesting, reconstructing, or inferring hidden reasoning.

Separate restricted restoration state from public outcomes and diagnostic events. The host decides the state transport and protection appropriate to its visibility, access, retention, and threat boundary. Integrity and compatibility must be checked before restored state can influence execution.

Never use a public log, ordinary GitHub comment, or repository fixture as a storage path for live continuation state. Use synthetic canaries and captured fake transports when the implementation needs evidence that secret data cannot cross those boundaries.

## Validation and external effects

Ordinary validation uses synthetic fixtures, fake transports, and test-only capabilities. A live-provider or paid run needs authorization for that execution and a bounded scope.

Publishing, release, deployment, tracker mutation, and destructive operations remain under the applicable task's authority. A successful build, passing test, generated candidate, or restored context does not grant that authority.

Ordinary `pull_request` and `push` checks must not require provider secrets. Any `pull_request_target` design requires an explicit security review before adoption.
