-- Initial schema for caller-owned Supabase projects. Apply explicitly as the operator.
-- Existing installations require a separately reviewed, data-preserving conversion.
begin;

-- Mapped JSON primitives: Contracts remains the detailed provider/accounting admission owner.
create function public.telemetry_int64(p_value jsonb) returns bigint
language plpgsql immutable security invoker set search_path = '' as $$
begin
    if p_value is null or p_value = 'null'::jsonb then return null; end if;
    if pg_catalog.jsonb_typeof(p_value) <> 'string' or (p_value #>> '{}') !~ '^(0|[1-9][0-9]*)$' then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
    return (p_value #>> '{}')::bigint;
exception when numeric_value_out_of_range then
    raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
end;
$$;

create function public.telemetry_object(p_value jsonb, p_allowed text[], p_required text[]) returns void
language plpgsql immutable security invoker set search_path = '' as $$
begin
    if pg_catalog.jsonb_typeof(p_value) is distinct from 'object'
        or not (p_value ?& p_required) or p_value - p_allowed <> '{}'::jsonb then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
end;
$$;

create function public.telemetry_keys(p_value jsonb) returns text[]
language sql immutable security invoker set search_path = '' as $$
    select array(select k from pg_catalog.jsonb_object_keys(p_value) as k order by k);
$$;

create function public.telemetry_pick(p_value jsonb, p_keys text[]) returns jsonb
language sql immutable security invoker set search_path = '' as $$
    select coalesce(pg_catalog.jsonb_object_agg(key, value), '{}'::jsonb)
    from pg_catalog.jsonb_each(p_value) where key = any(p_keys);
$$;

-- Event schema. Writer/event identity is deliberately a nondeferrable conflict arbiter.
create table public.telemetry_events (
    id bigint generated always as identity unique,
    writer_id uuid not null default auth.uid(),
    event_id uuid not null check (event_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    schema_version integer not null check (schema_version = 1),
    product text not null check (product ~ '^[a-z][a-z0-9_-]{0,31}$' and product <> 'caller'),
    product_version text not null check (length(btrim(product_version)) between 1 and 128),
    invocation_id text not null check (length(btrim(invocation_id)) between 1 and 256),
    occurred_at timestamptz not null check (isfinite(occurred_at)),
    occurred_at_residual_tick smallint not null default 0 check (occurred_at_residual_tick between 0 and 9),
    received_at timestamptz not null default now(),
    repository text,
    workflow_run_id text,
    run_attempt integer check (run_attempt > 0),
    job text,
    invocation_key text,
    execution_fields text[] not null check (execution_fields <@ array['repository','workflowRunId','runAttempt','job','invocationKey']
        and array_position(execution_fields, null) is null),
    outcome_kind text not null check (outcome_kind in ('succeeded','partial','failed','cancelled','rejected','unknown')),
    outcome_code text,
    outcome_code_present boolean not null,
    usage_inventory_coverage text not null check (usage_inventory_coverage in ('complete','partial','unavailable')),
    run_count smallint not null check (run_count between 0 and 256),
    extensions jsonb not null check (jsonb_typeof(extensions) = 'object'),
    primary key (writer_id, event_id),
    unique (id, writer_id),
    check (usage_inventory_coverage <> 'unavailable' or run_count = 0),
    check (repository is null or 'repository' = any(execution_fields)),
    check (workflow_run_id is null or 'workflowRunId' = any(execution_fields)),
    check (run_attempt is null or 'runAttempt' = any(execution_fields)),
    check (job is null or 'job' = any(execution_fields)),
    check (invocation_key is null or 'invocationKey' = any(execution_fields)),
    check (outcome_code is null or outcome_code_present)
);
create index telemetry_events_product_time on public.telemetry_events (writer_id, product, occurred_at);
create index telemetry_events_execution on public.telemetry_events (writer_id, repository, workflow_run_id, run_attempt);
create index telemetry_events_outcome_time on public.telemetry_events (writer_id, outcome_kind, occurred_at);

create table public.telemetry_runs (
    event_row_id bigint not null,
    writer_id uuid not null default auth.uid(),
    ordinal smallint not null check (ordinal between 1 and 256),
    -- Preserve the old nested JSON string equality while exposing a typed UUID.
    execution_id_text text not null,
    execution_id uuid generated always as (execution_id_text::uuid) stored not null
        check (execution_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    inventory_coverage text not null check (inventory_coverage in ('complete','partial','unavailable')),
    input_tokens bigint check (input_tokens >= 0),
    input_coverage text not null check (input_coverage in ('complete','partial','unavailable','overflow')),
    output_tokens bigint check (output_tokens >= 0),
    output_coverage text not null check (output_coverage in ('complete','partial','unavailable','overflow')),
    logical_calls bigint check (logical_calls >= 0),
    physical_attempts bigint check (physical_attempts >= 0),
    tools_admitted bigint check (tools_admitted >= 0),
    tools_invoked bigint check (tools_invoked >= 0),
    tools_reserved_unstarted bigint check (tools_reserved_unstarted >= 0),
    tools_released_unstarted bigint check (tools_released_unstarted >= 0),
    tools_present boolean not null,
    optional_fields text[] not null check (optional_fields <@ array['logicalCalls','physicalAttempts','tools',
        'providerCounters','accounting','providerSettlement','estimatedCosts'] and array_position(optional_fields, null) is null),
    provider_settlement text check (provider_settlement in ('unknown','unsettled','settled','mixed')),
    provider_counters jsonb,
    accounting jsonb,
    estimated_costs jsonb,
    primary key (event_row_id, ordinal),
    unique (event_row_id, execution_id),
    foreign key (event_row_id, writer_id) references public.telemetry_events (id, writer_id),
    check ((input_coverage in ('complete','partial')) = (input_tokens is not null)),
    check ((output_coverage in ('complete','partial')) = (output_tokens is not null)),
    check (inventory_coverage = 'complete' or (input_coverage <> 'complete' and output_coverage <> 'complete')),
    check (physical_attempts >= logical_calls),
    check (inventory_coverage <> 'unavailable' or (logical_calls is null and physical_attempts is null
        and input_coverage = 'unavailable' and output_coverage = 'unavailable'
        and (provider_counters is null or provider_counters in ('null'::jsonb, '[]'::jsonb)))),
    check (inventory_coverage <> 'complete' or physical_attempts is distinct from 0 or
        (logical_calls is not distinct from 0 and input_tokens is not distinct from 0 and input_coverage = 'complete'
        and output_tokens is not distinct from 0 and output_coverage = 'complete')),
    check (logical_calls is null or 'logicalCalls' = any(optional_fields)),
    check (physical_attempts is null or 'physicalAttempts' = any(optional_fields)),
    check (not tools_present or 'tools' = any(optional_fields)),
    check (tools_present or (tools_admitted is null and tools_invoked is null
        and tools_reserved_unstarted is null and tools_released_unstarted is null)),
    check (provider_settlement is null or 'providerSettlement' = any(optional_fields)),
    check (provider_counters is null or 'providerCounters' = any(optional_fields)),
    check (accounting is null or 'accounting' = any(optional_fields)),
    check (estimated_costs is null or 'estimatedCosts' = any(optional_fields)),
    check (case when provider_counters is null or provider_counters = 'null'::jsonb then true
        when jsonb_typeof(provider_counters) = 'array' then jsonb_array_length(provider_counters) <= 12 else false end),
    check (case when accounting is null or accounting = 'null'::jsonb then true
        when jsonb_typeof(accounting) = 'array' then jsonb_array_length(accounting) <= 14 else false end),
    check (case when estimated_costs is null or estimated_costs = 'null'::jsonb then true
        when jsonb_typeof(estimated_costs) = 'array' then jsonb_array_length(estimated_costs) <= 16 else false end)
);
create index telemetry_runs_writer_event on public.telemetry_runs (writer_id, event_row_id);

-- Every ordinary write path uses these invariants; no spoofable RPC-only session flag.
create function public.telemetry_run_bound() returns trigger
language plpgsql volatile security invoker set search_path = '' as $$
declare v_count smallint;
begin
    select run_count into v_count from public.telemetry_events
        where id = new.event_row_id and writer_id = new.writer_id;
    if not found then
        raise exception using errcode = '42501', message = 'telemetry_parent_rejected';
    end if;
    if new.ordinal < 1 or new.ordinal > v_count then
        raise exception using errcode = '23514', message = 'telemetry_run_ordinal_rejected';
    end if;
    return new;
end;
$$;
create trigger telemetry_run_bound before insert on public.telemetry_runs
    for each row execute function public.telemetry_run_bound();

create function public.telemetry_event_complete() returns trigger
language plpgsql volatile security invoker set search_path = '' as $$
begin
    if (select count(*) from public.telemetry_runs where event_row_id = new.id and writer_id = new.writer_id) <> new.run_count then
        raise exception using errcode = '23514', message = 'telemetry_incomplete_event';
    end if;
    return null;
end;
$$;
create constraint trigger telemetry_event_complete after insert on public.telemetry_events
    deferrable initially deferred for each row execute function public.telemetry_event_complete();

-- Projection helpers reject fixed fields that decomposition would otherwise discard.
create function public.telemetry_project_event(p_event jsonb) returns public.telemetry_events
language plpgsql stable security invoker set search_path = '' as $$
declare
    v_event public.telemetry_events;
    v_time text;
    v_key text;
    v_fields constant text[] := array['schemaVersion','eventId','invocationId','occurredAt','product','productVersion',
        'execution','outcome','usage','extensions'];
begin
    perform public.telemetry_object(p_event, v_fields, v_fields);
    if pg_catalog.jsonb_typeof(p_event->'schemaVersion') is distinct from 'number' or p_event->'schemaVersion' <> '1'::jsonb then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
    foreach v_key in array array['eventId','invocationId','occurredAt','product','productVersion'] loop
        if pg_catalog.jsonb_typeof(p_event->v_key) is distinct from 'string' then
            raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
        end if;
    end loop;
    perform public.telemetry_object(p_event->'execution', array['repository','workflowRunId','runAttempt','job','invocationKey'], array[]::text[]);
    perform public.telemetry_object(p_event->'outcome', array['kind','code'], array['kind']);
    perform public.telemetry_object(p_event->'usage', array['inventoryCoverage','runs'], array['inventoryCoverage','runs']);
    if pg_catalog.jsonb_typeof(p_event->'extensions') is distinct from 'object'
        or pg_catalog.jsonb_typeof(p_event#>'{usage,runs}') is distinct from 'array'
        or pg_catalog.jsonb_typeof(p_event#>'{usage,inventoryCoverage}') is distinct from 'string'
        or pg_catalog.jsonb_typeof(p_event#>'{outcome,kind}') is distinct from 'string' then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
    foreach v_key in array array['repository','workflowRunId','job','invocationKey'] loop
        if coalesce(pg_catalog.jsonb_typeof(p_event->'execution'->v_key), 'null') not in ('string','null') then
            raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
        end if;
    end loop;
    if coalesce(pg_catalog.jsonb_typeof(p_event#>'{execution,runAttempt}'), 'null') not in ('number','null')
        or coalesce(pg_catalog.jsonb_typeof(p_event#>'{outcome,code}'), 'null') not in ('string','null')
        or pg_catalog.jsonb_array_length(p_event#>'{usage,runs}') > 256 then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
    v_time := p_event->>'occurredAt';
    if v_time !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,7})?(Z|\+00:00)$' then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
    v_event.event_id := (p_event->>'eventId')::uuid;
    v_event.schema_version := 1;
    v_event.product := p_event->>'product';
    v_event.product_version := p_event->>'productVersion';
    v_event.invocation_id := p_event->>'invocationId';
    v_event.occurred_at := pg_catalog.regexp_replace(v_time, '(\.[0-9]{6})[0-9](Z|\+00:00)$', '\1\2')::timestamptz;
    v_event.occurred_at_residual_tick := coalesce(pg_catalog.substring(v_time, '\.[0-9]{6}([0-9])(Z|\+00:00)$'), '0')::smallint;
    v_event.repository := p_event#>>'{execution,repository}';
    v_event.workflow_run_id := p_event#>>'{execution,workflowRunId}';
    v_event.run_attempt := (p_event#>>'{execution,runAttempt}')::integer;
    v_event.job := p_event#>>'{execution,job}';
    v_event.invocation_key := p_event#>>'{execution,invocationKey}';
    v_event.execution_fields := public.telemetry_keys(p_event->'execution');
    v_event.outcome_kind := p_event#>>'{outcome,kind}';
    v_event.outcome_code := p_event#>>'{outcome,code}';
    v_event.outcome_code_present := (p_event->'outcome') ? 'code';
    v_event.usage_inventory_coverage := p_event#>>'{usage,inventoryCoverage}';
    v_event.run_count := pg_catalog.jsonb_array_length(p_event#>'{usage,runs}');
    v_event.extensions := p_event->'extensions';
    return v_event;
end;
$$;

create function public.telemetry_project_run(p_run jsonb) returns public.telemetry_runs
language plpgsql immutable security invoker set search_path = '' as $$
declare
    v_run public.telemetry_runs;
    v_key text;
    v_required constant text[] := array['executionId','inventoryCoverage','inputTokens','outputTokens'];
    v_optional constant text[] := array['logicalCalls','physicalAttempts','tools','providerCounters','accounting','providerSettlement','estimatedCosts'];
begin
    perform public.telemetry_object(p_run, v_required || v_optional, v_required);
    foreach v_key in array array['executionId','inventoryCoverage'] loop
        if pg_catalog.jsonb_typeof(p_run->v_key) is distinct from 'string' then
            raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
        end if;
    end loop;
    foreach v_key in array array['inputTokens','outputTokens'] loop
        perform public.telemetry_object(p_run->v_key, array['value','coverage'], array['value','coverage']);
        if pg_catalog.jsonb_typeof(p_run->v_key->'coverage') is distinct from 'string' then
            raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
        end if;
    end loop;
    v_run.execution_id_text := p_run->>'executionId';
    v_run.execution_id := v_run.execution_id_text::uuid;
    v_run.inventory_coverage := p_run->>'inventoryCoverage';
    v_run.input_tokens := public.telemetry_int64(p_run#>'{inputTokens,value}');
    v_run.input_coverage := p_run#>>'{inputTokens,coverage}';
    v_run.output_tokens := public.telemetry_int64(p_run#>'{outputTokens,value}');
    v_run.output_coverage := p_run#>>'{outputTokens,coverage}';
    v_run.logical_calls := public.telemetry_int64(p_run->'logicalCalls');
    v_run.physical_attempts := public.telemetry_int64(p_run->'physicalAttempts');
    v_run.tools_present := coalesce(pg_catalog.jsonb_typeof(p_run->'tools') = 'object', false);
    if v_run.tools_present then
        perform public.telemetry_object(p_run->'tools', array['admitted','invoked','reservedUnstarted','releasedUnstarted'],
            array['admitted','invoked','reservedUnstarted','releasedUnstarted']);
        v_run.tools_admitted := public.telemetry_int64(p_run#>'{tools,admitted}');
        v_run.tools_invoked := public.telemetry_int64(p_run#>'{tools,invoked}');
        v_run.tools_reserved_unstarted := public.telemetry_int64(p_run#>'{tools,reservedUnstarted}');
        v_run.tools_released_unstarted := public.telemetry_int64(p_run#>'{tools,releasedUnstarted}');
    elsif coalesce(pg_catalog.jsonb_typeof(p_run->'tools'), 'null') <> 'null' then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
    if coalesce(pg_catalog.jsonb_typeof(p_run->'providerSettlement'), 'null') not in ('string','null') then
        raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
    end if;
    v_run.optional_fields := public.telemetry_keys(p_run - v_required);
    v_run.provider_settlement := p_run->>'providerSettlement';
    v_run.provider_counters := p_run->'providerCounters';
    v_run.accounting := p_run->'accounting';
    v_run.estimated_costs := p_run->'estimatedCosts';
    return v_run;
end;
$$;

-- Lossless logical readback is also the immutable equality boundary. No payload shadow is stored.
create function public.telemetry_run_json(p_run public.telemetry_runs) returns jsonb
language sql immutable security invoker set search_path = '' as $$
    select pg_catalog.jsonb_build_object('executionId', p_run.execution_id_text, 'inventoryCoverage', p_run.inventory_coverage,
        'inputTokens', pg_catalog.jsonb_build_object('value', p_run.input_tokens::text, 'coverage', p_run.input_coverage),
        'outputTokens', pg_catalog.jsonb_build_object('value', p_run.output_tokens::text, 'coverage', p_run.output_coverage))
    || public.telemetry_pick(pg_catalog.jsonb_build_object('logicalCalls', p_run.logical_calls::text,
        'physicalAttempts', p_run.physical_attempts::text,
        'tools', case when p_run.tools_present then pg_catalog.jsonb_build_object('admitted', p_run.tools_admitted::text,
            'invoked', p_run.tools_invoked::text, 'reservedUnstarted', p_run.tools_reserved_unstarted::text,
            'releasedUnstarted', p_run.tools_released_unstarted::text) else 'null'::jsonb end,
        'providerSettlement', p_run.provider_settlement, 'providerCounters', p_run.provider_counters,
        'accounting', p_run.accounting, 'estimatedCosts', p_run.estimated_costs), p_run.optional_fields);
$$;

create function public.telemetry_event_json(p_event public.telemetry_events) returns jsonb
language sql stable security invoker set search_path = '' as $$
    select pg_catalog.jsonb_build_object('schemaVersion', p_event.schema_version, 'eventId', p_event.event_id,
        'invocationId', p_event.invocation_id, 'product', p_event.product, 'productVersion', p_event.product_version,
        'occurredAt', pg_catalog.to_char(p_event.occurred_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US')
            || p_event.occurred_at_residual_tick || '+00:00',
        'execution', public.telemetry_pick(pg_catalog.jsonb_build_object('repository', p_event.repository,
            'workflowRunId', p_event.workflow_run_id, 'runAttempt', p_event.run_attempt, 'job', p_event.job,
            'invocationKey', p_event.invocation_key), p_event.execution_fields),
        'outcome', pg_catalog.jsonb_build_object('kind', p_event.outcome_kind)
            || case when p_event.outcome_code_present then pg_catalog.jsonb_build_object('code', p_event.outcome_code) else '{}'::jsonb end,
        'usage', pg_catalog.jsonb_build_object('inventoryCoverage', p_event.usage_inventory_coverage,
            'runs', coalesce((select pg_catalog.jsonb_agg(public.telemetry_run_json(r) order by r.ordinal)
                from public.telemetry_runs r where r.event_row_id = p_event.id and r.writer_id = p_event.writer_id), '[]'::jsonb)),
        'extensions', p_event.extensions);
$$;

create function public.ingest_telemetry(event jsonb) returns text
language plpgsql volatile security invoker set search_path = '' as $$
declare
    v_event public.telemetry_events;
    v_existing public.telemetry_events;
    v_run public.telemetry_runs;
    v_runs public.telemetry_runs[] := array[]::public.telemetry_runs[];
    v_execution_ids uuid[] := array[]::uuid[];
    v_item jsonb;
    v_ordinal smallint := 0;
    v_id bigint;
    v_comparison jsonb;
begin
    if auth.uid() is null then
        raise exception using errcode = '42501', message = 'telemetry_authentication_required';
    end if;
    v_event := public.telemetry_project_event(event);
    foreach v_item in array array(select value from pg_catalog.jsonb_array_elements(event#>'{usage,runs}')) loop
        v_run := public.telemetry_project_run(v_item);
        if v_run.execution_id = any(v_execution_ids) then
            raise exception using errcode = '22023', message = 'telemetry_schema_rejected';
        end if;
        v_execution_ids := array_append(v_execution_ids, v_run.execution_id);
        v_ordinal := v_ordinal + 1;
        v_run.ordinal := v_ordinal;
        v_runs := array_append(v_runs, v_run);
    end loop;
    -- One statement also works when the caller selected immediate constraint timing.
    with new_event as (
        insert into public.telemetry_events (event_id, schema_version, product, product_version, invocation_id,
            occurred_at, occurred_at_residual_tick, repository, workflow_run_id, run_attempt, job, invocation_key,
            execution_fields, outcome_kind, outcome_code, outcome_code_present, usage_inventory_coverage, run_count,
            extensions)
        values (v_event.event_id, v_event.schema_version, v_event.product, v_event.product_version,
            v_event.invocation_id, v_event.occurred_at, v_event.occurred_at_residual_tick, v_event.repository,
            v_event.workflow_run_id, v_event.run_attempt, v_event.job, v_event.invocation_key, v_event.execution_fields,
            v_event.outcome_kind, v_event.outcome_code, v_event.outcome_code_present, v_event.usage_inventory_coverage,
            v_event.run_count, v_event.extensions)
        on conflict (writer_id, event_id) do nothing returning id
    ), new_runs as (
        insert into public.telemetry_runs (event_row_id, ordinal, execution_id_text, inventory_coverage, input_tokens,
            input_coverage, output_tokens, output_coverage, logical_calls, physical_attempts, tools_admitted,
            tools_invoked, tools_reserved_unstarted, tools_released_unstarted, tools_present, optional_fields,
            provider_settlement, provider_counters, accounting, estimated_costs)
        select e.id, r.ordinal, r.execution_id_text, r.inventory_coverage, r.input_tokens, r.input_coverage,
            r.output_tokens, r.output_coverage, r.logical_calls, r.physical_attempts, r.tools_admitted, r.tools_invoked,
            r.tools_reserved_unstarted, r.tools_released_unstarted, r.tools_present, r.optional_fields,
            r.provider_settlement, r.provider_counters, r.accounting, r.estimated_costs
        from new_event e cross join unnest(v_runs) r returning event_row_id
    ) select id into v_id from new_event;
    if v_id is not null then return 'stored'; end if;
    -- A separate READ COMMITTED statement sees the complete winner after the unique wait.
    select * into v_existing from public.telemetry_events where writer_id = auth.uid() and event_id = v_event.event_id;
    v_comparison := event || pg_catalog.jsonb_build_object('eventId', v_event.event_id,
        'occurredAt', pg_catalog.to_char(v_event.occurred_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US')
            || v_event.occurred_at_residual_tick || '+00:00');
    if found and public.telemetry_event_json(v_existing) = v_comparison then return 'duplicate'; end if;
    raise exception using errcode = '23505', message = 'telemetry_identity_conflict';
end;
$$;

-- Override broad Supabase-style defaults on every new object; ordinary clients never own them.
revoke all on public.telemetry_events, public.telemetry_runs from public, anon, authenticated;
revoke all on sequence public.telemetry_events_id_seq from public, anon, authenticated;
grant usage on schema public to authenticated;
grant select on public.telemetry_events, public.telemetry_runs to authenticated;
grant insert (event_id, schema_version, product, product_version, invocation_id, occurred_at, occurred_at_residual_tick,
    repository, workflow_run_id, run_attempt, job, invocation_key, execution_fields, outcome_kind, outcome_code,
    outcome_code_present, usage_inventory_coverage, run_count, extensions)
    on public.telemetry_events to authenticated;
grant insert (event_row_id, ordinal, execution_id_text, inventory_coverage, input_tokens, input_coverage, output_tokens,
    output_coverage, logical_calls, physical_attempts, tools_admitted, tools_invoked, tools_reserved_unstarted,
    tools_released_unstarted, tools_present, optional_fields, provider_settlement, provider_counters, accounting,
    estimated_costs)
    on public.telemetry_runs to authenticated;
alter table public.telemetry_events enable row level security;
alter table public.telemetry_events force row level security;
create policy telemetry_events_insert_own on public.telemetry_events for insert to authenticated
    with check ((select auth.uid()) is not null and writer_id = (select auth.uid()));
create policy telemetry_events_read_own on public.telemetry_events for select to authenticated
    using ((select auth.uid()) is not null and writer_id = (select auth.uid()));
alter table public.telemetry_runs enable row level security;
alter table public.telemetry_runs force row level security;
create policy telemetry_runs_insert_own on public.telemetry_runs for insert to authenticated
    with check ((select auth.uid()) is not null and writer_id = (select auth.uid()));
create policy telemetry_runs_read_own on public.telemetry_runs for select to authenticated
    using ((select auth.uid()) is not null and writer_id = (select auth.uid()));
revoke all on function public.telemetry_int64(jsonb) from public, anon, authenticated;
grant execute on function public.telemetry_int64(jsonb) to authenticated;
revoke all on function public.telemetry_object(jsonb, text[], text[]) from public, anon, authenticated;
grant execute on function public.telemetry_object(jsonb, text[], text[]) to authenticated;
revoke all on function public.telemetry_keys(jsonb) from public, anon, authenticated;
grant execute on function public.telemetry_keys(jsonb) to authenticated;
revoke all on function public.telemetry_pick(jsonb, text[]) from public, anon, authenticated;
grant execute on function public.telemetry_pick(jsonb, text[]) to authenticated;
revoke all on function public.telemetry_project_event(jsonb) from public, anon, authenticated;
grant execute on function public.telemetry_project_event(jsonb) to authenticated;
revoke all on function public.telemetry_project_run(jsonb) from public, anon, authenticated;
grant execute on function public.telemetry_project_run(jsonb) to authenticated;
revoke all on function public.telemetry_run_json(public.telemetry_runs) from public, anon, authenticated;
grant execute on function public.telemetry_run_json(public.telemetry_runs) to authenticated;
revoke all on function public.telemetry_event_json(public.telemetry_events) from public, anon, authenticated;
grant execute on function public.telemetry_event_json(public.telemetry_events) to authenticated;
revoke all on function public.ingest_telemetry(jsonb) from public, anon, authenticated;
grant execute on function public.ingest_telemetry(jsonb) to authenticated;
revoke all on function public.telemetry_run_bound() from public, anon, authenticated;
revoke all on function public.telemetry_event_complete() from public, anon, authenticated;

comment on table public.telemetry_events is 'One immutable writer-owned invocation; run_count seals complete ordered membership.';
comment on table public.telemetry_runs is 'Current runs in 1-based wire order; coverage is independent of nullable counts.';
comment on column public.telemetry_events.execution_fields is 'Present optional wire members; absence differs from explicit null.';
comment on column public.telemetry_runs.optional_fields is 'Present optional wire members; retains absence, null and empty collections.';
comment on column public.telemetry_runs.execution_id_text is 'Original nested UUID text retained for immutable JSONB equality.';
notify pgrst, 'reload schema';
commit;
