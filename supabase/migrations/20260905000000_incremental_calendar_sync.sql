-- MoiCalendar incremental calendar synchronization protocol v1.
-- Mutations are applied through security-definer RPC functions so ownership,
-- idempotency and revision checks are enforced in one PostgreSQL transaction.

create table public.calendar_event_mutations (
    owner_id uuid not null references public.profiles (id) on delete cascade,
    mutation_id uuid not null,
    device_id uuid not null,
    entity_id uuid not null,
    operation text not null,
    base_revision bigint,
    request_fingerprint text not null,
    result jsonb,
    created_at timestamptz not null default now(),
    primary key (owner_id, mutation_id),
    constraint calendar_event_mutations_device_owner_fk
        foreign key (device_id, owner_id)
        references public.devices (id, owner_id)
        on delete cascade,
    constraint calendar_event_mutations_operation
        check (operation in ('create', 'update', 'delete')),
    constraint calendar_event_mutations_base_revision
        check (base_revision is null or base_revision > 0),
    constraint calendar_event_mutations_fingerprint_length
        check (char_length(request_fingerprint) = 32)
);

create index calendar_event_mutations_owner_created_idx
    on public.calendar_event_mutations (owner_id, created_at desc);
create index calendar_event_mutations_owner_entity_idx
    on public.calendar_event_mutations (owner_id, entity_id, created_at);

alter table public.calendar_event_mutations enable row level security;
revoke all on table public.calendar_event_mutations from public, anon, authenticated;

create function public.moicalendar_default_calendar_id(p_owner_id uuid)
returns uuid
language sql
immutable
strict
set search_path = public, pg_temp
as $$
    select (
        substr(md5(p_owner_id::text || ':moicalendar-default-calendar'), 1, 8) || '-' ||
        substr(md5(p_owner_id::text || ':moicalendar-default-calendar'), 9, 4) || '-' ||
        '4' || substr(md5(p_owner_id::text || ':moicalendar-default-calendar'), 14, 3) || '-' ||
        '8' || substr(md5(p_owner_id::text || ':moicalendar-default-calendar'), 18, 3) || '-' ||
        substr(md5(p_owner_id::text || ':moicalendar-default-calendar'), 21, 12)
    )::uuid;
$$;

create function public.moicalendar_calendar_event_json(p_event public.calendar_events)
returns jsonb
language sql
stable
set search_path = public, pg_temp
as $$
    select jsonb_build_object(
        'id', p_event.id,
        'title', p_event.title,
        'description', p_event.description,
        'location', p_event.location,
        'startUtc', p_event.start_at,
        'endUtc', p_event.end_at,
        'timeZoneId', p_event.time_zone_id,
        'isAllDay', p_event.is_all_day,
        'recurrenceRule', p_event.recurrence_rule,
        'externalUid', p_event.external_uid,
        'createdAtUtc', p_event.created_at,
        'updatedAtUtc', p_event.updated_at,
        'deletedAtUtc', p_event.deleted_at
    );
$$;

create function public.moicalendar_apply_calendar_mutation(p_mutation jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    v_owner_id uuid := auth.uid();
    v_mutation_id uuid;
    v_device_id uuid;
    v_entity_id uuid;
    v_operation text;
    v_base_revision bigint;
    v_payload jsonb;
    v_fingerprint text;
    v_default_calendar_id uuid;
    v_existing_mutation public.calendar_event_mutations%rowtype;
    v_existing_event public.calendar_events%rowtype;
    v_saved_event public.calendar_events%rowtype;
    v_result jsonb;
    v_inserted boolean;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;
    if p_mutation is null or jsonb_typeof(p_mutation) <> 'object' then
        raise exception using errcode = '22023', message = 'Mutation must be a JSON object';
    end if;

    v_mutation_id := (p_mutation ->> 'mutation_id')::uuid;
    v_device_id := (p_mutation ->> 'device_id')::uuid;
    v_entity_id := (p_mutation ->> 'entity_id')::uuid;
    v_operation := lower(p_mutation ->> 'operation');
    v_base_revision := (p_mutation ->> 'base_revision')::bigint;
    v_payload := p_mutation -> 'payload';
    v_fingerprint := md5(p_mutation::text);

    if v_operation not in ('create', 'update', 'delete') or
       v_payload is null or jsonb_typeof(v_payload) <> 'object' then
        raise exception using errcode = '22023', message = 'Mutation fields are invalid';
    end if;

    insert into public.profiles (id)
    values (v_owner_id)
    on conflict (id) do nothing;

    v_default_calendar_id := public.moicalendar_default_calendar_id(v_owner_id);
    insert into public.calendars (
        id, owner_id, name, is_default, server_revision, server_changed_at)
    select v_default_calendar_id, v_owner_id, '我的日历', true, 1, transaction_timestamp()
    where not exists (
        select 1 from public.calendars
        where id = v_default_calendar_id and owner_id = v_owner_id)
    on conflict (id) do nothing;

    insert into public.devices (
        id, owner_id, name, platform, last_seen_at, server_revision, server_changed_at)
    select v_device_id, v_owner_id, 'MoiCalendar 设备', 'PWA', transaction_timestamp(), 1, transaction_timestamp()
    where not exists (
        select 1 from public.devices
        where id = v_device_id and owner_id = v_owner_id)
    on conflict (id) do nothing;

    select * into v_existing_mutation
    from public.calendar_event_mutations
    where owner_id = v_owner_id and mutation_id = v_mutation_id;

    if found then
        if v_existing_mutation.request_fingerprint = v_fingerprint then
            return v_existing_mutation.result;
        end if;
        return jsonb_build_object(
            'status', 'conflict',
            'mutation_id', v_mutation_id,
            'entity_id', v_entity_id,
            'conflict', jsonb_build_object('code', 'mutation_id_reused'));
    end if;

    insert into public.calendar_event_mutations (
        owner_id, mutation_id, device_id, entity_id, operation,
        base_revision, request_fingerprint)
    values (
        v_owner_id, v_mutation_id, v_device_id, v_entity_id, v_operation,
        v_base_revision, v_fingerprint)
    on conflict (owner_id, mutation_id) do nothing
    returning true into v_inserted;

    if coalesce(v_inserted, false) = false then
        select * into v_existing_mutation
        from public.calendar_event_mutations
        where owner_id = v_owner_id and mutation_id = v_mutation_id;
        if v_existing_mutation.request_fingerprint = v_fingerprint then
            return v_existing_mutation.result;
        end if;
        return jsonb_build_object(
            'status', 'conflict',
            'mutation_id', v_mutation_id,
            'entity_id', v_entity_id,
            'conflict', jsonb_build_object('code', 'mutation_id_reused'));
    end if;

    select * into v_existing_event
    from public.calendar_events
    where owner_id = v_owner_id and id = v_entity_id
    for update;

    if v_operation = 'create' then
        if v_base_revision is not null then
            v_result := jsonb_build_object(
                'status', 'conflict', 'mutation_id', v_mutation_id, 'entity_id', v_entity_id,
                'conflict', jsonb_build_object('code', 'invalid_create_base_revision'));
        elsif found then
            v_result := jsonb_build_object(
                'status', 'conflict', 'mutation_id', v_mutation_id, 'entity_id', v_entity_id,
                'conflict', jsonb_build_object(
                    'code', 'entity_exists', 'current_revision', v_existing_event.server_revision));
        else
            insert into public.calendar_events (
                id, owner_id, calendar_id, title, description, location,
                start_at, end_at, time_zone_id, is_all_day, recurrence_rule,
                external_uid, created_at, updated_at, deleted_at,
                server_revision, server_changed_at)
            values (
                v_entity_id,
                v_owner_id,
                v_default_calendar_id,
                v_payload ->> 'title',
                coalesce(v_payload ->> 'description', ''),
                coalesce(v_payload ->> 'location', ''),
                (v_payload ->> 'startUtc')::timestamptz,
                (v_payload ->> 'endUtc')::timestamptz,
                v_payload ->> 'timeZoneId',
                coalesce((v_payload ->> 'isAllDay')::boolean, false),
                v_payload ->> 'recurrenceRule',
                v_payload ->> 'externalUid',
                (v_payload ->> 'createdAtUtc')::timestamptz,
                (v_payload ->> 'updatedAtUtc')::timestamptz,
                null,
                1,
                transaction_timestamp())
            returning * into v_saved_event;
            v_result := jsonb_build_object(
                'status', 'applied',
                'mutation_id', v_mutation_id,
                'entity_id', v_entity_id,
                'server_revision', v_saved_event.server_revision);
        end if;
    elsif not found then
        v_result := jsonb_build_object(
            'status', 'conflict', 'mutation_id', v_mutation_id, 'entity_id', v_entity_id,
            'conflict', jsonb_build_object('code', 'entity_not_found'));
    elsif v_existing_event.deleted_at is not null then
        v_result := jsonb_build_object(
            'status', 'conflict', 'mutation_id', v_mutation_id, 'entity_id', v_entity_id,
            'conflict', jsonb_build_object(
                'code', 'entity_deleted', 'current_revision', v_existing_event.server_revision));
    elsif v_base_revision is null or v_base_revision <> v_existing_event.server_revision then
        v_result := jsonb_build_object(
            'status', 'conflict', 'mutation_id', v_mutation_id, 'entity_id', v_entity_id,
            'conflict', jsonb_build_object(
                'code', 'stale_revision', 'current_revision', v_existing_event.server_revision));
    elsif v_operation = 'update' then
        update public.calendar_events
        set title = v_payload ->> 'title',
            description = coalesce(v_payload ->> 'description', ''),
            location = coalesce(v_payload ->> 'location', ''),
            start_at = (v_payload ->> 'startUtc')::timestamptz,
            end_at = (v_payload ->> 'endUtc')::timestamptz,
            time_zone_id = v_payload ->> 'timeZoneId',
            is_all_day = coalesce((v_payload ->> 'isAllDay')::boolean, false),
            recurrence_rule = v_payload ->> 'recurrenceRule',
            external_uid = v_payload ->> 'externalUid',
            updated_at = (v_payload ->> 'updatedAtUtc')::timestamptz,
            deleted_at = null
        where owner_id = v_owner_id and id = v_entity_id
        returning * into v_saved_event;
        v_result := jsonb_build_object(
            'status', 'applied', 'mutation_id', v_mutation_id, 'entity_id', v_entity_id,
            'server_revision', v_saved_event.server_revision);
    else
        update public.calendar_events
        set deleted_at = transaction_timestamp(),
            updated_at = transaction_timestamp()
        where owner_id = v_owner_id and id = v_entity_id
        returning * into v_saved_event;
        v_result := jsonb_build_object(
            'status', 'applied', 'mutation_id', v_mutation_id, 'entity_id', v_entity_id,
            'server_revision', v_saved_event.server_revision);
    end if;

    update public.calendar_event_mutations
    set result = v_result
    where owner_id = v_owner_id and mutation_id = v_mutation_id;
    return v_result;
end;
$$;

create function public.moicalendar_pull_calendar_changes(
    p_after_revision bigint,
    p_limit integer default 100)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    v_owner_id uuid := auth.uid();
    v_high_water bigint;
    v_last_change_revision bigint;
    v_has_more boolean := false;
    v_cursor bigint;
    v_changes jsonb;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;
    if p_after_revision < 0 or p_limit < 1 or p_limit > 1000 then
        raise exception using errcode = '22023', message = 'Pull cursor or limit is invalid';
    end if;

    select latest_revision into v_high_water
    from public.sync_state
    where owner_id = v_owner_id;

    if v_high_water is null then
        return jsonb_build_object(
            'cursor', p_after_revision,
            'has_more', false,
            'changes', '[]'::jsonb);
    end if;

    with page as (
        select event_row
        from public.calendar_events event_row
        where owner_id = v_owner_id
          and server_revision > p_after_revision
          and server_revision <= v_high_water
        order by server_revision, id
        limit p_limit
    )
    select
        coalesce(jsonb_agg(jsonb_build_object(
            'server_revision', (event_row).server_revision,
            'calendar_event', public.moicalendar_calendar_event_json(event_row))
            order by (event_row).server_revision, (event_row).id), '[]'::jsonb),
        max((event_row).server_revision)
    into v_changes, v_last_change_revision
    from page;

    if v_last_change_revision is not null then
        select exists (
            select 1
            from public.calendar_events
            where owner_id = v_owner_id
              and server_revision > v_last_change_revision
              and server_revision <= v_high_water)
        into v_has_more;
    end if;

    v_cursor := case
        when v_has_more then v_last_change_revision
        else v_high_water
    end;

    return jsonb_build_object(
        'cursor', v_cursor,
        'has_more', v_has_more,
        'changes', v_changes);
end;
$$;

revoke all on function public.moicalendar_default_calendar_id(uuid) from public, anon, authenticated;
revoke all on function public.moicalendar_calendar_event_json(public.calendar_events) from public, anon, authenticated;
revoke all on function public.moicalendar_apply_calendar_mutation(jsonb) from public, anon;
revoke all on function public.moicalendar_pull_calendar_changes(bigint, integer) from public, anon;
grant execute on function public.moicalendar_apply_calendar_mutation(jsonb) to authenticated;
grant execute on function public.moicalendar_pull_calendar_changes(bigint, integer) to authenticated;

comment on table public.calendar_event_mutations is
    'Idempotency ledger for authenticated calendar event mutations. Rows are private to RPC functions.';
comment on function public.moicalendar_apply_calendar_mutation(jsonb) is
    'Applies one idempotent calendar mutation with optimistic base-revision conflict detection.';
comment on function public.moicalendar_pull_calendar_changes(bigint, integer) is
    'Returns ordered calendar changes and a server-controlled monotonic cursor.';
