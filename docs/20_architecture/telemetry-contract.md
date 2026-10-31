# Neutral telemetry v1 contract

`Solus.Telemetry.Contracts` constructs, validates, serializes and reads an immutable terminal observation of one physical product invocation. It has no Agent, database, transport or third-party package dependency. The independent `Solus.Logging.slnx` currently contains this library and `Solus.Logging.Tests`; the same Contracts project is also registered in `SolusAgent.slnx`. The Agent libraries retain their existing references. This implements the neutral foundation of the [L1 plan](../90_roadmap/logging-l1-plan.md), without implementing its Agent mapper, sink, CLI, Action or publication.

## Producer and consumer

The product supplies its invocation identity, UTC time, terminal outcome and exact current-run membership. Assign `eventId` once for an actual invocation and retain the record for transport retries. Another invocation, including a workflow rerun, supplies a different event identity. Serialization never generates an identity or reexecutes work.

Use `TelemetryJson.TrySerialize` and `TelemetryJson.TryDeserialize` for this wire contract. A generic serializer does not apply its canonical number converters or admission policy. Both methods return a bounded `TelemetryDiagnostic`; failure returns a null output rather than partial JSON or an invalid record. Neither method changes a product result or performs network access. Constructors and `WithCallerAttributes` reject programmer-supplied invalid data with fixed `ArgumentException` diagnostics. Products catch that construction boundary and report telemetry unavailable independently of business success; they must not substitute zero usage.

```csharp
using System.Text.Json;
using Solus.Telemetry.Contracts;

// This identity is supplied once for the invocation, not regenerated for an upload retry.
static bool CreateObservation(Guid eventId, out string? json, out TelemetryDiagnostic diagnostic)
{
    json = null;
    diagnostic = TelemetryDiagnostic.InvalidInput;
    try
    {
        using var extensions = JsonDocument.Parse("{\"scribe\":{\"historicalAccounting\":{\"inputTokens\":\"7\"}}}");
        var measured = new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Complete,
            new(12, TelemetryMeasurementCoverage.Complete), new(0, TelemetryMeasurementCoverage.Complete), 1, 1);
        var incomplete = new TelemetryRunSummary(Guid.NewGuid(), TelemetryInventoryCoverage.Partial,
            new(0, TelemetryMeasurementCoverage.Partial), new(null, TelemetryMeasurementCoverage.Unavailable), 1, 1);
        var record = new TelemetryRecord(eventId, "synthetic-invocation", DateTimeOffset.UtcNow, "scribe", "1.0-test",
            new TelemetryExecution(invocationKey: "matrix-a"), new TelemetryOutcome(TelemetryOutcomeKind.Partial),
            new TelemetryUsage(TelemetryInventoryCoverage.Complete, [measured, incomplete]), extensions.RootElement);
        return TelemetryJson.TrySerialize(record, out json, out diagnostic);
    }
    catch (ArgumentException)
    {
        return false; // Telemetry unavailable; retain the separate business result.
    }
}
```

A consumer calls `TelemetryJson.TryDeserialize(json, out var record, out var diagnostic)` and uses only the returned admitted record. The executable [synthetic producer and consumer](../../tests/Solus.Logging.Tests/Contracts/SyntheticTelemetry.cs) and [contract tests](../../tests/Solus.Logging.Tests/Contracts/TelemetryContractTests.cs) cover multiple current runs, exact numbers, immutable ownership and read/write agreement for Scribe-shaped, APR-shaped and additional product records. These are source-level synthetic consumers, not installed-package, product-repository or live-storage qualification.

## Fixed envelope and wire grammar

Property names are case-sensitive camelCase. `schemaVersion` is the JSON integer `1`, independent of package version. Missing or malformed discriminators reject as invalid; another integer version returns `UnsupportedSchema`. Unknown fixed fields, duplicate decoded JSON member names at any path, numeric/undefined enum aliases, trailing JSON and invalid Unicode reject. Optional constructor fields may be omitted; serialization emits their explicit null or empty collection. An empty optional fact collection means no supplied facts, not a measured zero.

| Field | Meaning and validation |
| --- | --- |
| `eventId` | Nonempty UUID in hyphenated D form; producer-owned identity reused across transport retries. |
| `invocationId` | Nonblank physical invocation label, at most 256 UTF-16 code units. |
| `occurredAt` | Nondefault ISO 8601 UTC timestamp with explicit `Z` or `+00:00` and at most seven fractional digits; offset-free text, unknown `-00:00`, nonzero offsets and finer-than-tick precision reject. |
| `product` | Namespace matching `[a-z][a-z0-9_-]{0,31}`; `caller` and fixed envelope field names are reserved. |
| `productVersion` | Nonblank label, at most 128 code units. |
| `execution` | Optional `repository` (256), `workflowRunId` (128), positive integer `runAttempt`, `job` (256) and `invocationKey` (256) correlation labels. Labels confer no authority. |
| `outcome` | `kind`: `succeeded`, `partial`, `failed`, `cancelled`, `rejected` or `unknown`; optional `code` uses at most 64 ASCII letters, digits, underscores, hyphens or dots. This is a product outcome, not delivery success. |
| `usage` | Explicit current membership coverage and bounded distinct run summaries. |
| `extensions` | Owned product/caller JSON object described below. |

Fixed labels reject blank values, control characters and malformed Unicode. All Int64 quantities, including logical/physical/tool/group counts and signed accounting remainders, use canonical invariant decimal JSON strings or null. For example, `"0"`, `"9007199254740993"` and `"9223372036854775807"` are exact; JSON numbers, `"+1"`, `"01"`, `"-0"`, exponent aliases and overflow reject. Negative values are legal only for a classified remaining allowance. Exact decimal cost amounts also use canonical decimal strings without exponent or redundant fractional zeroes. UUIDs, timestamps and closed classifications are JSON strings. Schema version and bounded workflow attempt are JSON integers.

## Current usage and independent knowledge

`TelemetryUsage` has `inventoryCoverage` (`complete`, `partial`, `unavailable`) and `runs`. Complete empty membership proves no current runs. Unavailable membership is empty and unknown. Partial membership does not establish whole-invocation totals. Duplicate execution identities reject. The library never derives invocation totals or imports historical runs; product historical accounting belongs in an explicitly named extension such as `extensions.scribe.historicalAccounting`.

Each `TelemetryRunSummary` has `executionId`, independent attempt `inventoryCoverage`, `inputTokens`, `outputTokens`, optional `logicalCalls`, `physicalAttempts`, `tools`, `providerCounters`, `accounting`, `providerSettlement` and `estimatedCosts`. Counts describe the supplied inventory; incomplete inventory counts are not whole-run totals. Known physical count cannot be below known logical count. Tool counts independently preserve `admitted`, `invoked`, `reservedUnstarted` and `releasedUnstarted`, with null meaning unreported. Invocation entry is not proof of a successful effect.

Each token observation has `value` and `coverage`:

| Coverage | Value | Claim |
| --- | --- | --- |
| `complete` | Nonnegative Int64, including zero | Whole supplied complete inventory is measured in this dimension. |
| `partial` | Nonnegative Int64, including zero | Known subtotal; measurements or inventory are incomplete. |
| `unavailable` | null | No known sum; never zero consumption. |
| `overflow` | null | Known aggregation exceeded Int64; never wrap or saturate. |

Incomplete attempt inventory cannot assert complete core measurement coverage. Unavailable attempt inventory carries no attempt counts or provider groups and has unavailable core measurements. A complete inventory with known zero physical attempts requires zero logical calls and complete zero core observations. Input and output knowledge remain independent. Accounting cannot upgrade this coverage.

Provider summaries retain `kind` (`cacheRead`, `cacheWrite`, `uncachedInput`, `reasoning`), `relationship` (`includedInInput`, `includedInOutput`, `independent`), a token `observation`, `reportedAttempts` and `measuredAttempts`. Group only matching kind and relationship across distinct current physical attempts; different relationships remain separate. Reported count includes unknown values, measured count includes only known values. Complete coverage requires complete inventory and reported/measured counts equal to the known physical attempt count. Partial coverage retains a known subtotal; unavailable has no measured entries; overflow requires multiple measured entries. Group counts cannot exceed known physical inventory; reported counts across relationships for the same kind must also fit that inventory because a physical attempt supplies at most one fact per kind. Duplicate keys reject. Uncached input cannot be an output subset; every known subset subtotal, including a partial observation, cannot exceed a complete known parent. No missing peer is inferred, and overlapping kinds never contribute to core totals. Actual Agent conversion and aggregation remain the later mapper's responsibility.

Accounting summaries have `source` (`providerClaim` or `runLedger`), `kind` (`reservation`, `released`, `measured`, `conservativeCharge`, `unresolved`, `allowance`, `remainingAllowance`) and independent `input`/`output` amounts. Each amount has `value` and `knowledge` (`known`, `provisional`, `unknown`, `unavailable`, `overflow`). Known/provisional require an amount; unknown may retain an explicit estimate or null; unavailable/overflow require null. Only remaining allowance permits negative values, and released values cannot be positive. Duplicate source/kind keys reject. Provider claims do not become ledger authority. `providerSettlement` separately preserves `unknown`, `unsettled`, `settled` or `mixed`. Cost estimates retain source, a three-uppercase-letter currency label, nullable exact decimal `amount` and knowledge; source/currency keys are unique. The library calculates no policy, exchange rate, bill or implicit measured usage.

## Immutable extensions and composition

`extensions` contains only the record's product namespace and optional `caller`, each an object. `scribe` and `apr` identify their product-owned namespaces; another product chooses its documented canonical namespace. This is structural ownership, not authentication. A namespace cannot override a fixed envelope field. Nested attributes named `usage` or `eventId` are ordinary data and do not override fixed fields.

The record clones extension JSON and copies all collections. It remains unchanged after input arrays are mutated or the source `JsonDocument` is disposed. `WithCallerAttributes` accepts a plain JSON object, adds only its members inside `extensions.caller`, rejects collisions with admitted caller members, and preserves product/fixed values. Ordinary JSON escaping may change; admitted decoded strings and numeric lexemes retain their values. No dictionary or generic object is silently coerced into an extension.

Values support objects, arrays, strings, booleans, null and finite binary64-interoperable JSON numbers. Numeric lexemes are preserved rather than rewritten through `double`. Integral binary64 representations outside ±9007199254740991, nonfinite conversions and nonzero values that underflow to zero reject; use strings for exact larger integers. Ordinary fractional JSON numbers have binary64 consumer precision, not an exact-decimal guarantee. Duplicate names compare decoded strings ordinally, including escaped aliases. Shared validation does not assign product field vocabulary or aggregation semantics.

There are no prompt, candidate, raw-response, exception-body or saved-context fields, and serialization never traverses an Agent outcome or captures ambient data. Extension producers remain responsible for admitting only safe public metrics; structural JSON validation is not a content sanitizer.

## Admission bounds and status

`TelemetryLimits` owns the constants. Both output admission and input parsing apply the same structural invariants. Serialized byte limits use the compact JSON encoder's UTF-8 representation, including escaping, property names and delimiters; input also has a raw UTF-8 record limit before parsing.

| Limit | Inclusive maximum |
| --- | --- |
| Complete record | 262144 bytes |
| Combined extensions | 32768 bytes |
| Members in every extension object | 64 |
| Extension object/array container levels, counting the extensions root as level one | 8 |
| Distinct current runs | 256 |
| Provider kind/relationship groups per run | 12 |
| Accounting source/kind groups per run | 14 |
| Cost source/currency groups per run | 16 |

Oversized record output returns `RecordTooLarge` with null JSON. Invalid input, unsupported version and oversized extensions return their fixed diagnostic without echoing input or exception contents. Construction rejects invalid extensions before output; the product's construction boundary turns that into telemetry unavailable. There is no truncation, partial record or automatic product replay. These source-level boundaries do not establish persistence, durable terminal delivery, billing accuracy or public stable upgrade compatibility.
