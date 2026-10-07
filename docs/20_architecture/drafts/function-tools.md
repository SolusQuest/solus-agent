# Prepared function-tool draft

`SolusAgent.Tools.Api` supplies an executable M1 draft for bounded function-tool metadata, actual argument validation, effect-free preparation and explicit capability-authorized invocation. The compiled synthetic [CustomTools consumer](../../../tests/ConsumerProbes/CustomTools/CounterTool.cs) depends on Tools.Api alone. This draft does not implement registration, an agent adapter, runtime recording, scheduling, retry, transport, production effects or package distribution.

## Supported metadata and schema

`ToolDescriptor` is immutable. Tool names, capability semantic identifiers and schema property names use `[a-z][a-z0-9_]{0,63}` and ordinal exact comparison. Noncanonical values reject rather than being folded into another identity. Descriptive `ReadOnly` and `Mutating` values grant no execution authority; unknown effect values reject. Descriptions admit at most 1024 UTF-8 source bytes before trimming outer whitespace and reject empty normalized text. Opaque call IDs retain exact spelling and whitespace, reject control characters, and admit at most 128 UTF-8 bytes.

`ToolSchema` supports only the `closed_scalar_object` profile: a flat object with explicit `type: object`, `properties`, `required`, and `additionalProperties: false`. Each property specifies only `type: string`, `boolean`, or `integer`. There are at most 32 properties. Integer values are lexical JSON integer tokens fitting Int64; fractions and exponent forms reject even if mathematically integral. Required names must exist, be unique, and belong to the declared properties. Optional properties may be absent. Unknown keywords, arrays, nested objects, references, unions, nullable types and other semantics reject explicitly. This profile does not claim complete JSON Schema compatibility.

Schemas admit at most 8192 source UTF-8 bytes and expose deterministic ordinal property/required ordering without dropping supported constraints. Arguments and success outputs admit at most 16384 UTF-8 bytes; each descriptor can select smaller positive argument/result limits. All JSON is strict, with no comments, trailing commas, duplicate decoded member names or malformed Unicode, and maximum container depth four. Byte factories admit size before strict UTF-8 decoding; string entrypoints check strict UTF-8 byte count and reject malformed UTF-16 before parsing. Payloads use owned immutable strings, so caller byte-array mutation or document disposal cannot change a prepared call. Producers must also bound their own allocations before creating output.

An integration must verify support for the complete profile, property semantics, result boundary, effect description and capability semantics before adapting a descriptor. It must reject unsupported semantics rather than silently omit constraints or treat a similarly named built-in tool as equivalent.

## Preparation and authority

Consumers use `IFunctionTool`; custom implementations derive from `FunctionTool<TCapability>` to use the guarded preparation and invocation seam. `Prepare` checks the actual call tool name and real arguments against every supported input constraint, then runs optional effect-free implementation-local validation. It returns `ToolPreparation` with either a fixed rejection or an owner-bound `PreparedToolInvocation` retaining the exact bounded original call. No capability is supplied or invoked during preparation. Trusted tool code must not perform effects in its validation hook; this library is not a sandbox for hostile in-process implementations.

Preparation is not execution permission. The host separately supplies a narrow capability at invocation. The guard verifies both the concrete required capability type and its exact semantic identifier. A capability must confine the actual effect; the model, descriptor, prepared handle and argument data cannot manufacture trusted authority. The synthetic counter capability exposes only a counter mutation and cancellation token, without generic service lookup, process, network, filesystem or platform clients.

`InvokeAsync` checks the actual tool instance, full expected call association (call ID, tool name and exact raw arguments), explicit capability and cancellation before dispatch. A different owner, ID, name, arguments, or missing/wrong capability rejects before effects. An already-cancelled request returns Cancelled before effects. These refusals leave the prepared handle available for a later explicit authorized attempt. After admission, an atomic claim permits at most one dispatch per handle, including concurrent callers; failure or cancellation after that claim does not enable replay. The host still owns uniqueness across separately prepared handles and runs. This local guard is not a durable or distributed exactly-once effect guarantee.

## Output, cancellation and failures

The tool produces `ToolOutput` carrying the original call association plus bounded raw success JSON or a fixed failure. The guard checks output association before success acceptance, then enforces the selected result size/schema. The final `ToolResult` is associated with the requested call and exposes `InvocationStarted`. Non-success outcomes contain no success JSON; errors use fixed `ToolError` values and never publish arbitrary exception details. `ToString` on payload-bearing contracts returns a type name rather than payload contents.

Preparation and pre-dispatch rejection can be shown to have zero effects. Result rejection, failure and in-flight cancellation can happen after an effect. `InvocationStarted: true` explicitly preserves that uncertainty and does not imply rollback, product acceptance, or retry authority. Matching supplied-token cancellation is Cancelled; unrelated cancellation exceptions are Failed. A validated successful output is preserved when cancellation is signalled after completion. An abrupt process death supplies no promised final result.

| Boundary | Result |
| --- | --- |
| Invalid metadata, encoding, schema or retained payload size | Classified `ToolContractException`; no prepared invocation is retained. |
| Invalid descriptor limit configuration or null API argument | `ArgumentOutOfRangeException` or `ArgumentNullException`; these are caller precondition errors. |
| Invalid actual arguments or effect-free domain rejection | Rejected `ToolPreparation`, with no effects. |
| Owner/call mismatch, denied capability or used handle | Associated Rejected result before dispatch. |
| Pre-dispatch cancellation | Associated Cancelled, `InvocationStarted: false`. |
| Invalid, oversized or mismatched output | Associated Rejected, `InvocationStarted: true`; effects may have occurred. |
| Implementation failure or matching in-flight cancellation | Associated Failed or Cancelled, with honest dispatch observation. |
| Accepted associated bounded output | Succeeded with the validated success JSON. |

## Batch admission obligations

Before a batch's first effect, an integration must impose finite count and aggregate byte limits, validate and prepare every member, preserve each original association, reject duplicate call IDs or ambiguous tool binding, verify every required narrow capability, and complete its host-owned admission policy. A rejected member must not be hidden by dispatching earlier members during preparation. If partial execution is intentionally supported later, its admission and recording semantics must be explicit. Scheduling, concurrency, budgets and retry are separate runtime responsibilities; this draft supplies no production batch executor or batch policy.

The test-only bounded host example prepares all members and checks count, aggregate bytes, distinct IDs and capability before any dispatch. Its canary stays zero for valid preparation and for invalid, ambiguous, oversized or unauthorized batch admission. Invocation is always a subsequent explicit operation; no provider retry authorizes retry of tool effects.

## Current proof and limits

[Tools tests](../../../tests/SolusAgent.ContractTests/Tools/FunctionToolTests.cs) exercise the real synthetic producer through public contracts: metadata bounds, byte admission, supported schema normalization, actual argument/domain failures, preparation canary, capability refusal, complete call/output association, single-use concurrency, cancellation and post-effect failures. Evaluated MSBuild architecture assertions require CustomTools to reference Tools.Api as its sole production dependency and keep all compile inputs in this repository. Tools.Api retains no project references or external packages. [Project validation](../../00_project/validation.md) owns the restore/build/test commands and evidence limits.

The contracts are unreleased drafts with one coherent current producer/consumer shape. There are no legacy aliases or fallback readers, provider compatibility promises, transactional effect guarantees, runtime restoration, downstream migration or release qualification claims. Later implementation can revise this shape together with its actual consumers, validators and tests.
