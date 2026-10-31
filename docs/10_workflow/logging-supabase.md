# Bounded Supabase telemetry delivery

`Solus.Logging` delivers one admitted neutral record with a finite attempt/time budget. `Solus.Logging.Supabase` implements the fixed Supabase Data API protocol and owns its explicit transport credentials. Both are callable .NET libraries, independent of Agent assemblies and GitHub Actions. The [neutral contract](../20_architecture/telemetry-contract.md) owns record admission and safe public metrics; this guide owns the implemented storage mapping. CLI, Action, package publication and hosted Supabase qualification remain separate [L1 outcomes](../90_roadmap/logging-l1-plan.md).

## Calling the sink

The product creates one `TelemetryRecord` and retains its producer-assigned event identity. The operator supplies the project HTTPS origin, a `sb_publishable_` API key, and an access JWT for a dedicated authenticated ingest user through the caller's secret mechanism. There is no ambient credential discovery, token refresh, arbitrary SQL/table/RPC input or service-role fallback.

```csharp
using Solus.Logging;
using Solus.Logging.Supabase;
using Solus.Telemetry.Contracts;

static async Task<TelemetryDeliveryResult> UploadAsync(TelemetryRecord record,
    Uri projectOrigin, string publishableKey, string accessJwt, CancellationToken cancellationToken)
{
    var configuration = new SupabaseTelemetryOptions(projectOrigin, publishableKey, accessJwt);
    using var sink = new SupabaseTelemetrySink(configuration);
    var delivery = new TelemetryDelivery(sink,
        new TelemetryDeliveryOptions(maximumAttempts: 3,
            totalTimeout: TimeSpan.FromSeconds(10), retryDelay: TimeSpan.FromMilliseconds(200)));
    return await delivery.DeliverAsync(record, cancellationToken);
}
```

Configuration construction rejects invalid origin/credential syntax with fixed exceptions before sending. The server authenticates JWT identity and expiry; syntax validation does not authenticate a token. Use an authenticated user's access JWT, never an administrative/service-role credential. Token provisioning, rotation and retention are operator responsibilities.

`TelemetryPayload.TryCreate` uses `TelemetryJson` to serialize the complete record once. Every attempt receives the same immutable payload and event identity. Oversized or unavailable input returns `Failed/InvalidRecord` with zero attempts. `ITelemetrySink.WriteAsync` is a single-attempt capability; the caller owns its lifetime. The library never traverses Agent outcomes, captures ambient data or reexecutes product work.

`TelemetryDeliveryResult.Status` is `Stored`, `Duplicate` or `Failed`, independent of the product outcome. Diagnostics are fixed enum values and contain no exception, credentials, telemetry input or raw server response. The policy permits 1–10 attempts, a positive total timeout up to two minutes and 0–10 seconds fixed backoff; defaults are 3 attempts, 10 seconds total and 200 ms backoff. The Supabase attempt also has a 30 second cap covering response reads. Server `Retry-After` does not enlarge the selected budget.

Caller cancellation and deadline expiry stop waiting and prohibit another attempt. A custom noncooperative sink may finish later; failed/cancelled delivery does not establish rollback or absence of a row. A later explicit retry retains the same event identity. There is no durable queue, exactly-once guarantee or product replay. The caller chooses business warning/failure policy outside these libraries.

## Fixed wire protocol

The adapter sends `POST /rest/v1/rpc/ingest_telemetry`, `Content-Type: application/json`, `Accept: application/json`, an `apikey` header and `Authorization: Bearer <access JWT>`. Its body is `{"event": <complete admitted v1 object>}`; the RPC argument name is exactly `event`. The wrapper adds ten bytes beyond the record's 256 KiB admission limit and never truncates it.

The owned production handler disables redirects, cookies, proxies, decompression and ambient trace propagation. Response bodies are limited to 4096 bytes. Success requires HTTP 200 with JSON string `"stored"` or `"duplicate"`; empty, malformed, oversized or unknown success responses fail explicitly. HTTP redirects are permanent rejections and never forward credentials.

| Response or event | Classification |
| --- | --- |
| `200` plus `"stored"` | Stored |
| `200` plus `"duplicate"` | Duplicate |
| `401`/`403` | Permanent authentication/authorization failure |
| `409` | Permanent identity conflict |
| `400`/`404` | Permanent schema/record rejection |
| `429`, other `5xx`, network/response loss | Transient, subject to both finite bounds |
| Recognized bounded auth/schema/conflict error code on `429`/`5xx` | Permanent classified failure |
| Other status or invalid success protocol | Permanent rejection/protocol failure |

Oversized or unparsable transient error bodies cannot establish a permanent machine error and retain the retryable HTTP status; oversized HTTP 200 bodies remain protocol failures. The adapter inspects only fixed machine error codes (`PGRST3*`/authentication SQLSTATEs, `PGRST2*`/missing-schema SQLSTATEs, `23505`); it never returns server message/detail strings. Unexpected sink exceptions become `SinkFailure`. Repeated transient failures end as `AttemptsExhausted`; the deadline and caller cancellation have separate diagnostics. A lost successful response followed by retry returns `Duplicate` when the original immutable event committed.

## Operator-applied schema and identity

The operator reviews and explicitly applies [001_telemetry.sql](../../database/logging/001_telemetry.sql) to the selected existing Supabase database. It is an initial transactional schema, intentionally failing on already-existing objects rather than silently accepting drift. It requires existing Supabase `anon`/`authenticated` roles and `auth.uid()`. It creates no user, key, token or hosted resource. Normal delivery never applies migrations. The final `NOTIFY pgrst, 'reload schema'` refreshes the existing Data API schema cache; the RPC must be visible there before ingest.

The fixed table is `public.telemetry_events`, keyed by `(writer_id, event_id)`. `writer_id` defaults to `auth.uid()` and `received_at` is server assigned. The migration explicitly revokes broad default grants on its new table/function and restores all-column `SELECT` under RLS plus column-scoped `INSERT`. Neither server-owned column is insertable; no update/delete privileges are granted to anonymous or authenticated roles. Anonymous execution/read/write is denied. RLS enables and forces own-writer insert/read policies; correlation labels in the event are never authorization claims. The intended dedicated user is scoped to its own rows, as is every other authenticated identity; there is no custom identity allowlist.

`public.ingest_telemetry(event jsonb)` is `SECURITY INVOKER`, volatile, with an empty fixed search path and qualified references. It validates the essential exact v1 envelope/shapes and relies on Contracts for detailed run/extension admission. It does not duplicate every neutral measurement/accounting invariant in SQL. Unknown envelope fields, supplied writer identity, unsupported schema and missing/null structured fields fail without inserting a partial row.

The function attempts `INSERT ... ON CONFLICT DO NOTHING`, then compares the existing typed scalars and complete JSONB values in a separate statement. Under the selected ordinary `READ COMMITTED` Data API transaction, a concurrent loser waits for the first insert and then observes the committed winner. Equal immutable content returns `duplicate`; differing content raises fixed SQLSTATE `23505`, mapped by PostgREST to HTTP 409. Nothing is updated. JSONB object ordering and numeric spelling are semantic; array ordering, missing members and JSON null remain distinct. `received_at` is excluded from duplicate equality and is never refreshed by retries.

`execution`, `outcome`, `usage` and `extensions` are stored whole as JSONB. Canonical Int64/accounting/decimal strings, nulls, coverage, provider relationships and nested caller/product namespaces remain intact. PostgreSQL JSONB cannot represent every admitted JSON string/number (for example escaped NUL); such delivery fails explicitly, without dropping or replacing data.

PostgreSQL timestamp precision is one microsecond; telemetry supports seven fractional digits. `occurred_at` stores the first six digits after truncation before casting, and `occurred_at_residual_tick` stores the seventh digit (0–9). Both participate in collision equality. Reconstruct the original UTC instant with the microsecond timestamp plus its residual tick; the following readback returns seven fractional digits without rounding across a second/day boundary:

```sql
select event_id,
    to_char(occurred_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US')
        || occurred_at_residual_tick || '+00:00' as occurred_at_exact,
    execution, outcome, usage, extensions, received_at
from public.telemetry_events;
```

## Deterministic validation

Use .NET selected by `global.json` and Node 22.18 or newer. The storage harness pins native PostgreSQL 17.6 through test-only npm dependencies; no Docker daemon, system PostgreSQL installation, host user creation or database credentials are needed. Install the pinned dependencies, then run from the repository root:

```text
npm ci --prefix scripts/logging
dotnet build Solus.Logging.slnx -c Release
dotnet test tests/Solus.Logging.Tests/Solus.Logging.Tests.csproj -c Release --filter "FullyQualifiedName~Delivery|FullyQualifiedName~Supabase"
dotnet test tests/Solus.Logging.Tests/Solus.Logging.Tests.csproj -c Release --no-build
node scripts/logging/verify-storage.ts --local
```

The verifier runs the platform package's standard binary setup even if npm install scripts were disabled. Run as a non-root user; it never creates OS users. It owns a unique ignored cluster directory and synthetic local password, listens only on an ephemeral loopback TCP port, closes its connections and stops/deletes its cluster in `finally`. A missing/incompatible runtime fails validation rather than skipping it.

The real SQL tests seed broad Supabase-like default grants, apply the actual migration, and use non-owner/non-BYPASSRLS identities. They prove effective privileges, first/duplicate/collision behavior, writer isolation, anonymous/forgery/update/delete denial, exact structured readback and seventh-digit preservation. Separate sessions hold the first insert uncommitted and observe the competing session waiting on a database lock before releasing it, proving actual overlap. The same JSON fixture is admitted through the .NET contract and used for the real SQL readback.

Ordinary Logging CI runs the complete Logging suite and the real disposable verifier on Ubuntu 24.04, plus existing Agent architecture checks. Controlled HTTP tests exercise actual adapter request/response handling, retries, body stalls and cancellation. Native PostgreSQL plus a synthetic `auth.uid()` fixture proves database semantics; it does not authenticate hosted JWT signatures or qualify an operator's deployed Supabase/PostgREST configuration. Published-package installation, live Supabase delivery and service readback remain separately authorized L1 work.

The design follows [Supabase invoker function guidance](https://supabase.com/docs/guides/database/functions), [RLS and grants](https://supabase.com/docs/guides/database/postgres/row-level-security), [PostgREST RPC](https://docs.postgrest.org/en/stable/references/api/functions.html), and PostgreSQL [READ COMMITTED conflict visibility](https://www.postgresql.org/docs/17/transaction-iso.html) and [timestamp precision](https://www.postgresql.org/docs/17/datatype-datetime.html).
