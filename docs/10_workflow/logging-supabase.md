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

The operator reviews and explicitly applies [001_telemetry.sql](../../database/logging/001_telemetry.sql) for a clean installation. It is one transactional initial baseline, intentionally failing on existing objects rather than silently accepting drift. It requires existing Supabase `anon`/`authenticated` roles and `auth.uid()`, and creates no user, key, token or hosted resource. Normal delivery never applies migrations. The final `NOTIFY pgrst, 'reload schema'` refreshes the existing Data API cache.

An initialized database needs a separately reviewed conversion, not a replay of the clean baseline. Inspect its current catalog, dependencies, grants, policies, sequence and row inventory; test conversion with populated legacy rows even when the selected target is empty. Preserve writer/event identity, numeric IDs, receipts, timestamp residuals and complete event content, and verify equality before removing superseded JSON columns. Bound locks, statements and affected rows; reject unexpected drift or unconvertible data. Any migration-bookkeeping change belongs in that exact reviewed operation. No reset, Auth/session/key/settings change or deletion permission follows from this baseline. Historical qualification describes its original schema and revision.

The opt-in historical local Supabase reproduction installs the schema captured from its verified, clean accepted-candidate checkout. Its original readback driver and oracle remain paired with that legacy schema; it does not install the current typed baseline or establish qualification for the current storage model.

The existing `public` table/RPC placement remains the project boundary. Both tables have enabled and forced RLS, explicit own-writer read/insert policies, and minimal column grants. This change does not add an exposed schema or a privileged delivery path.

| Table | Stored facts and identity |
| --- | --- |
| `telemetry_events` | One immutable invocation, keyed by `(writer_id, event_id)`, with a unique database-generated bigint `id`, server receipt, product/invocation/time metadata, typed `repository`, `workflow_run_id`, `run_attempt`, `job`, `invocation_key`, `outcome_kind`, `outcome_code`, `usage_inventory_coverage`, immutable `run_count`, and JSONB `extensions`. |
| `telemetry_runs` | One current run at a 1-based `ordinal`, referencing the event's bigint `event_row_id` and writer. Unique `(event_row_id, ordinal)` and `(event_row_id, execution_id)` preserve order and event-scoped execution identity. Typed fields retain inventory, independent input/output values and coverage, logical/physical counts, tool counts and settlement. `provider_counters`, `accounting` and `estimated_costs` remain JSONB. |

`workflow_run_id` is text even when it looks numeric. Token, logical, physical and tool counts are nullable bigint. Null is never substituted with zero; input and output coverage are independent. Cost/accounting strings and provider relationships remain unchanged inside their JSONB collections. No whole-envelope or whole-run shadow is retained. `execution_fields`, `outcome_code_present`, run `optional_fields` and `tools_present` preserve omitted/null/object/empty-collection distinctions. `execution_id_text` preserves the original nested UUID string for equality; its generated UUID projection supplies typed uniqueness. An execution UUID may recur in another event or under another writer.

Writers can read generated IDs on their own rows. Duplicate/conflicting deliveries preserve the first ID and `received_at`. Identity allocation may leave gaps and does not guarantee commit order. Ordinary roles cannot insert or change event IDs generated by the database, writer ownership or receipt time, manipulate the identity sequence, or update/delete/truncate either table. A run may reference its owned parent's generated ID; it cannot forge that identity. Anonymous table/function access is denied. Correlation labels and JSON content confer no authority.

The run foreign key includes both parent ID and writer, independently preventing cross-writer association. An invoker insert trigger checks parent visibility and the ordinal bound. A deferred event constraint trigger requires exactly `run_count` children at commit. Unique ordinals within `1..run_count` therefore establish complete, dense membership. No ordinal remains available after a complete event commits, so ordinary callers cannot append a later run. Ordinary direct SQL may create a complete owned event in one transaction; incomplete transactions fail. No caller-settable session flag distinguishes trusted RPC writes.

`public.ingest_telemetry(event jsonb)` remains volatile, `SECURITY INVOKER`, with an empty fixed search path and qualified references. It projects the existing v1 object, inserts the parent and all runs in one data-modifying statement, and supports both immediate and deferred constraint timing. A failing child leaves no parent or orphan runs. The writer/event conflict arbiter is nondeferrable. A concurrent loser waits at that unique key and reads the complete committed winner in a separate `READ COMMITTED` statement.

Equal immutable content returns `duplicate`; different content raises fixed SQLSTATE `23505` / `telemetry_identity_conflict`, which PostgREST maps to HTTP 409. Comparison includes all event facts and all runs in ordinal order, every retained JSON value and structural-presence distinction. No row is updated. JSONB object key order and equivalent numeric spelling remain semantic; arrays, absent properties, explicit null and retained strings remain distinct. Server IDs and receipt times are excluded from equality.

The SQL layer checks exact mapped object fields, projection types, required relational constraints and core count/coverage consistency. Unknown members at decomposed boundaries reject rather than disappear. A duplicate execution UUID within a candidate run array is a schema rejection (`22023`), not an existing-event identity conflict. Contracts remains the detailed provider/accounting/extension admission owner; the RPC is not a replacement for `TelemetryJson`. PostgreSQL JSONB cannot represent every admitted JSON value, such as escaped NUL; delivery fails explicitly without dropping data. The transport and its 256 KiB record limit remain unchanged.

## Typed queries and lossless readback

Writer-prefixed indexes support product/time, repository/workflow/attempt, and outcome/time filtering. Run keys support ordered event joins and event-scoped UUID uniqueness; a writer/event index supports child ownership queries. These indexes serve the demonstrated paths rather than indexing every label or JSON field.

```sql
select e.id, e.event_id, e.repository, e.workflow_run_id, e.outcome_kind,
    r.ordinal, r.execution_id, r.input_tokens, r.input_coverage,
    r.output_tokens, r.output_coverage, r.physical_attempts
from public.telemetry_events e
join public.telemetry_runs r on r.event_row_id = e.id
where e.repository = 'synthetic/repository'
    and e.workflow_run_id = '9007199254740993'
    and e.run_attempt = 2 and e.outcome_kind = 'partial'
order by e.id, r.ordinal;
```

An invocation total requires complete membership and complete measurement coverage in that dimension for every run. A known subtotal from partial measurements is not that total. PostgreSQL `sum(bigint)` returns exact numeric, avoiding Int64 overflow across runs. Input/output remain independent; no provider counters enter either sum. Complete empty membership establishes zero, while unavailable or partial membership does not.

```sql
select e.id,
    case when e.usage_inventory_coverage = 'complete'
        and count(r.ordinal) filter (where r.input_coverage = 'complete') = count(r.ordinal)
        then coalesce(sum(r.input_tokens), 0) end as complete_input_total,
    case when e.usage_inventory_coverage = 'complete'
        and count(r.ordinal) filter (where r.output_coverage = 'complete') = count(r.ordinal)
        then coalesce(sum(r.output_tokens), 0) end as complete_output_total
from public.telemetry_events e
left join public.telemetry_runs r on r.event_row_id = e.id
group by e.id, e.usage_inventory_coverage;
```

PostgreSQL timestamps have microsecond precision. `occurred_at` stores six fractional digits after truncation before casting; `occurred_at_residual_tick` stores the seventh digit. Both participate in equality. The invoker `telemetry_event_json` helper reconstructs the logical v1 content from the typed row and owned ordered runs, preserving all values and presence distinctions. It normalizes only the original event UUID/UTC timestamp equivalence; it does not store a second payload. Helper execution grants are explicit, and all database reads remain subject to the caller's RLS.

```sql
select e.id, e.received_at,
    to_char(e.occurred_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US')
        || e.occurred_at_residual_tick || '+00:00' as occurred_at_exact,
    public.telemetry_event_json(e) as event
from public.telemetry_events e;
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

The real SQL tests seed broad Supabase-like default grants, apply the actual baseline, and use non-owner/non-BYPASSRLS identities. They prove both tables' effective privileges, complete multi-run readback, immutable field/order/presence conflicts, direct incomplete-commit and append rejection, both constraint timings, exact counts, coverage-aware aggregates and seventh-digit preservation. Separate sessions observe an actual lock wait before releasing the winner, including duplicate, order/detail conflict and winner-rollback paths. Shared single-run and multi-run fixtures are admitted through the .NET contract and used for SQL readback. SQL role tests model a trusted request identity; they do not authenticate a hosted JWT.

Ordinary Logging CI runs the complete Logging suite and the real disposable verifier on Ubuntu 24.04, plus existing Agent architecture checks. Controlled HTTP tests exercise actual adapter request/response handling, retries, body stalls and cancellation. Native PostgreSQL plus a synthetic `auth.uid()` fixture proves database semantics; it does not authenticate hosted JWT signatures or qualify an operator's deployed Supabase/PostgREST configuration. Published-package installation, live Supabase delivery and service readback remain separately authorized L1 work.

The design follows [Supabase invoker function guidance](https://supabase.com/docs/guides/database/functions), [RLS and grants](https://supabase.com/docs/guides/database/postgres/row-level-security), [PostgREST RPC](https://docs.postgrest.org/en/stable/references/api/functions.html), and PostgreSQL [READ COMMITTED conflict visibility](https://www.postgresql.org/docs/17/transaction-iso.html) and [timestamp precision](https://www.postgresql.org/docs/17/datatype-datetime.html).
