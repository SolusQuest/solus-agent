-- Initial schema for caller-owned Supabase projects. Apply explicitly as the operator;
-- ordinary telemetry delivery never executes this file. No role/token provisioning.
begin;

create table public.telemetry_events (
    writer_id uuid not null default auth.uid(),
    event_id uuid not null check (event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    schema_version integer not null check (schema_version = 1),
    product text not null check (product ~ '^[a-z][a-z0-9_-]{0,31}$' and product <> 'caller'),
    product_version text not null check (length(btrim(product_version)) between 1 and 128),
    invocation_id text not null check (length(btrim(invocation_id)) between 1 and 256),
    occurred_at timestamptz not null check (isfinite(occurred_at)),
    -- PostgreSQL stores microseconds. Preserve the seventh .NET fractional digit separately.
    occurred_at_residual_tick smallint not null default 0 check (occurred_at_residual_tick between 0 and 9),
    received_at timestamptz not null default now(),
    execution jsonb not null check (jsonb_typeof(execution) = 'object'),
    outcome jsonb not null check (jsonb_typeof(outcome) = 'object'),
    usage jsonb not null check (jsonb_typeof(usage) = 'object'),
    extensions jsonb not null check (jsonb_typeof(extensions) = 'object'),
    primary key (writer_id, event_id)
);
create index telemetry_events_product_time on public.telemetry_events (writer_id, product, occurred_at);

-- Override Supabase-style default grants on these new objects explicitly.
revoke all on public.telemetry_events from public, anon, authenticated;
grant usage on schema public to authenticated;
grant select on public.telemetry_events to authenticated;
grant insert (event_id, schema_version, product, product_version, invocation_id,
    occurred_at, occurred_at_residual_tick, execution, outcome, usage, extensions)
    on public.telemetry_events to authenticated;
alter table public.telemetry_events enable row level security;
alter table public.telemetry_events force row level security;
create policy telemetry_insert_own on public.telemetry_events for insert to authenticated
    with check ((select auth.uid()) is not null and writer_id = (select auth.uid()));
create policy telemetry_read_own on public.telemetry_events for select to authenticated
    using ((select auth.uid()) is not null and writer_id = (select auth.uid()));

create function public.ingest_telemetry(event jsonb) returns text
language plpgsql volatile security invoker set search_path = ''
as $$
declare
    candidate public.telemetry_events%rowtype;
    existing public.telemetry_events%rowtype;
    inserted boolean;
    time_text text;
    field text;
    fields constant text[] := array['schemaVersion', 'eventId', 'invocationId', 'occurredAt',
        'product', 'productVersion', 'execution', 'outcome', 'usage', 'extensions'];
begin
    if auth.uid() is null then
        raise exception using errcode = '42501', message = 'telemetry_authentication_required';
    end if;
    -- This is the mapped envelope boundary. Contracts owns detailed run/extension admission.
    -- Never discard unknown envelope fields, substitute {}, or trust a supplied writer identity.
    if pg_catalog.jsonb_typeof(event) is distinct from 'object'
        or not (event ?& fields) or event - fields <> '{}'::jsonb
        or pg_catalog.jsonb_typeof(event->'schemaVersion') is distinct from 'number'
        or event->'schemaVersion' <> '1'::jsonb then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
    foreach field in array array['eventId','invocationId','occurredAt','product','productVersion'] loop
        if pg_catalog.jsonb_typeof(event->field) is distinct from 'string' then
            raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
        end if;
    end loop;
    foreach field in array array['execution','outcome','usage','extensions'] loop
        if pg_catalog.jsonb_typeof(event->field) is distinct from 'object' then
            raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
        end if;
    end loop;
    time_text := event->>'occurredAt';
    if time_text !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$' then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
    candidate.writer_id := auth.uid();
    candidate.event_id := (event->>'eventId')::uuid;
    candidate.schema_version := 1;
    candidate.product := event->>'product';
    candidate.product_version := event->>'productVersion';
    candidate.invocation_id := event->>'invocationId';
    -- Truncate BEFORE casting; casting all seven digits first would round and corrupt identity.
    candidate.occurred_at := pg_catalog.regexp_replace(time_text, '(\.[0-9]{6})[0-9](Z|\+00:00)$', '\1\2')::timestamptz;
    candidate.occurred_at_residual_tick := coalesce(pg_catalog.substring(time_text, '\.[0-9]{6}([0-9])(Z|\+00:00)$'), '0')::smallint;
    candidate.execution := event->'execution';
    candidate.outcome := event->'outcome';
    candidate.usage := event->'usage';
    candidate.extensions := event->'extensions';
    insert into public.telemetry_events (event_id, schema_version, product, product_version,
        invocation_id, occurred_at, occurred_at_residual_tick, execution, outcome, usage, extensions)
    values (candidate.event_id, candidate.schema_version, candidate.product, candidate.product_version,
        candidate.invocation_id, candidate.occurred_at, candidate.occurred_at_residual_tick,
        candidate.execution, candidate.outcome, candidate.usage, candidate.extensions)
    on conflict (writer_id, event_id) do nothing returning true into inserted;
    if inserted then return 'stored'; end if;

    -- Separate statement: READ COMMITTED now sees a concurrent winner after the unique check waited.
    -- Never use an UPDATE/upsert, or include server-assigned received_at in immutable equality.
    select * into existing from public.telemetry_events
        where writer_id = candidate.writer_id and event_id = candidate.event_id;
    if found and row(existing.schema_version, existing.product, existing.product_version,
        existing.invocation_id, existing.occurred_at, existing.occurred_at_residual_tick,
        existing.execution, existing.outcome, existing.usage, existing.extensions)
        is not distinct from row(candidate.schema_version, candidate.product, candidate.product_version,
        candidate.invocation_id, candidate.occurred_at, candidate.occurred_at_residual_tick,
        candidate.execution, candidate.outcome, candidate.usage, candidate.extensions) then
        return 'duplicate';
    end if;
    raise exception using errcode = '23505', message = 'telemetry_identity_conflict';
end;
$$;
revoke all on function public.ingest_telemetry(jsonb) from public, anon, authenticated;
grant execute on function public.ingest_telemetry(jsonb) to authenticated;
-- Refresh the existing Data API cache after operator application, without running a migration at ingest.
notify pgrst, 'reload schema';
commit;
