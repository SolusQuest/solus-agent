# Contract lifecycle

SolusAgent's outer agent API, shared tool API, runtime extension API, and future saved-context formats can have different compatibility boundaries. This document defines how to reason about those boundaries; it does not select package versions, codecs, or a release policy during initialization.

## Draft contracts

The initial API projects contain no public types. Their responsibilities and reference graph are selected, while signatures and data formats remain to be designed.

Before a supported external commitment exists, update a draft contract together with its actual producers, consumers, validators, tests, and documentation. A repository revision identifies draft semantics; a package or format version alone does not identify every unreleased revision.

Do not create a version, alias, old-format reader, or migration framework for each internal edit. Introduce coexistence only when a current downstream integration, retained context, or other real commitment needs it.

## Saved context

The self-owned runtime must eventually support complete restoration of its own runtime context. Context compatibility is scoped to its agent implementation and applicable runtime, provider, model, and format constraints.

An outer context envelope does not make different agent implementations' payloads interchangeable. Reject incompatible or invalid restoration explicitly. Starting a fresh session is a separate caller decision, not a silent recovery path.

The eventual context design must cover runtime-owned records and required provider continuation while excluding credentials, live clients, delegates, and tool instances. The host supplies execution capabilities again on restoration and owns storage policy.

## Historical evidence

An accepted milestone or experiment proves only the behavior observed at its recorded revision and conditions. Later draft changes do not rewrite that historical truth or make its old implementation an active authority.

Current validation runs against current code. Retain executable historical machinery only when a current claim or obligation actually depends on it.

## Supported external boundaries

When a package, API, or saved format becomes a supported downstream surface, record its compatibility and support commitments. Breaking changes then follow the policy for that particular boundary, with executable acceptance and migration behavior where needed.

A public repository or an empty assembly does not establish such a commitment. Package and format versioning need not advance together unless an actual compatibility relationship requires it.

Apply [Pre-release engineering](pre-release-engineering.md) when deciding the amount of process needed for a change.
