-- Preserve recurrence exclusions and theme colors through every mutation/return path.
-- Missing fields from old clients retain existing values; explicit malformed/null values fail.
alter table public.calendar_events
    add column color_index integer not null default 1,
    add column excluded_occurrence_starts_utc jsonb not null default '[]'::jsonb,
    add constraint calendar_events_color_index_valid check (color_index between 1 and 8),
    add constraint calendar_events_exclusions_array
        check (jsonb_typeof(excluded_occurrence_starts_utc) = 'array');

create function public.moicalendar_normalize_exclusions(p_value jsonb)
returns jsonb
language plpgsql
stable
set search_path = pg_catalog, pg_temp
as $$
declare v_item jsonb; v_result jsonb;
begin
    if p_value is null or jsonb_typeof(p_value) <> 'array' or jsonb_array_length(p_value) > 100000 then
        raise exception using errcode = '22023', message = 'Invalid recurrence exclusions';
    end if;
    for v_item in select value from jsonb_array_elements(p_value) loop
        if jsonb_typeof(v_item) <> 'string' or
           (v_item #>> '{}') !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}T.*(Z|[+-][0-9]{2}:[0-9]{2})$' then
            raise exception using errcode = '22023', message = 'Exclusions must be offset-aware timestamps';
        end if;
        perform (v_item #>> '{}')::timestamptz;
    end loop;
    select coalesce(jsonb_agg(to_jsonb(instant) order by instant), '[]'::jsonb) into v_result
    from (select distinct (value #>> '{}')::timestamptz as instant
          from jsonb_array_elements(p_value)) normalized;
    return v_result;
end;
$$;
revoke all on function public.moicalendar_normalize_exclusions(jsonb) from public, anon, authenticated;

create or replace function public.moicalendar_calendar_event_json(p_event public.calendar_events)
returns jsonb
language sql
stable
set search_path = pg_catalog, pg_temp
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
        'colorIndex', p_event.color_index,
        'excludedOccurrenceStartsUtc', p_event.excluded_occurrence_starts_utc,
        'externalUid', p_event.external_uid,
        'createdAtUtc', p_event.created_at,
        'updatedAtUtc', p_event.updated_at,
        'deletedAtUtc', p_event.deleted_at
    );
$$;

create or replace function public.moicalendar_apply_calendar_mutation_v1(p_mutation jsonb)
returns jsonb
language plpgsql
security definer
set search_path = pg_catalog, pg_temp
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

    if v_payload ? 'colorIndex' and
       (jsonb_typeof(v_payload -> 'colorIndex') <> 'number' or
        (v_payload ->> 'colorIndex') !~ '^[1-8]$') then
        raise exception using errcode = '22023', message = 'Invalid colorIndex';
    end if;
    if v_payload ? 'excludedOccurrenceStartsUtc' then
        perform public.moicalendar_normalize_exclusions(v_payload -> 'excludedOccurrenceStartsUtc');
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
                external_uid, color_index, excluded_occurrence_starts_utc, created_at, updated_at, deleted_at,
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
                coalesce((v_payload ->> 'colorIndex')::integer, 1),
                public.moicalendar_normalize_exclusions(coalesce(v_payload -> 'excludedOccurrenceStartsUtc', '[]'::jsonb)),
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
            color_index = coalesce((v_payload ->> 'colorIndex')::integer, v_existing_event.color_index),
            excluded_occurrence_starts_utc = public.moicalendar_normalize_exclusions(
                coalesce(v_payload -> 'excludedOccurrenceStartsUtc', v_existing_event.excluded_occurrence_starts_utc)),
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

revoke all on function public.moicalendar_apply_calendar_mutation_v1(jsonb) from public, anon, authenticated;

create or replace function public.moicalendar_apply_calendar_mutation(p_mutation jsonb)
returns jsonb
language plpgsql
security definer
set search_path = pg_catalog, pg_temp
as $$
declare
    v_owner_id uuid := auth.uid();
    v_mutation_id uuid;
    v_entity_id uuid;
    v_operation text;
    v_payload jsonb;
    v_default_calendar_id uuid;
    v_created_at timestamptz;
    v_updated_at timestamptz;
    v_deleted_at timestamptz;
    v_existing_event public.calendar_events%rowtype;
    v_saved_event public.calendar_events%rowtype;
    v_result jsonb;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;

    -- The v2 implementation remains authoritative for validation, ownership,
    -- optimistic concurrency, and mutation-ID fingerprint checks.
    v_result := public.moicalendar_apply_calendar_mutation_v2(p_mutation);
    v_operation := lower(p_mutation ->> 'operation');
    if v_operation <> 'delete' or
       v_result ->> 'status' <> 'conflict' or
       v_result #>> '{conflict,code}' <> 'entity_not_found' then
        return v_result;
    end if;

    v_mutation_id := (p_mutation ->> 'mutation_id')::uuid;
    v_entity_id := (p_mutation ->> 'entity_id')::uuid;
    v_payload := p_mutation -> 'payload';
    v_default_calendar_id := public.moicalendar_default_calendar_id(v_owner_id);
    v_deleted_at := coalesce(
        (v_payload ->> 'deletedAtUtc')::timestamptz,
        transaction_timestamp());
    v_created_at := coalesce(
        (v_payload ->> 'createdAtUtc')::timestamptz,
        v_deleted_at);
    v_updated_at := greatest(
        coalesce((v_payload ->> 'updatedAtUtc')::timestamptz, v_deleted_at),
        v_created_at,
        v_deleted_at);

    insert into public.calendar_events (
        id, owner_id, calendar_id, title, description, location,
        start_at, end_at, time_zone_id, is_all_day, recurrence_rule,
        external_uid, color_index, excluded_occurrence_starts_utc, created_at, updated_at, deleted_at,
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
        coalesce((v_payload ->> 'colorIndex')::integer, 1),
        public.moicalendar_normalize_exclusions(coalesce(v_payload -> 'excludedOccurrenceStartsUtc', '[]'::jsonb)),
        v_created_at,
        v_updated_at,
        v_deleted_at,
        1,
        transaction_timestamp())
    on conflict (id) do nothing
    returning * into v_saved_event;

    if not found then
        select * into v_existing_event
        from public.calendar_events
        where owner_id = v_owner_id and id = v_entity_id
        for update;

        if not found then
            -- A globally colliding UUID owned by another user must reveal no data.
            return v_result;
        end if;

        if v_existing_event.deleted_at is null then
            v_result := jsonb_build_object(
                'status', 'conflict',
                'mutation_id', v_mutation_id,
                'entity_id', v_entity_id,
                'conflict', jsonb_build_object(
                    'code', 'entity_exists',
                    'current_revision', v_existing_event.server_revision,
                    'current_entity', public.moicalendar_calendar_event_json(v_existing_event)));
        else
            v_result := jsonb_build_object(
                'status', 'applied',
                'mutation_id', v_mutation_id,
                'entity_id', v_entity_id,
                'server_revision', v_existing_event.server_revision);
        end if;
    else
        v_result := jsonb_build_object(
            'status', 'applied',
            'mutation_id', v_mutation_id,
            'entity_id', v_entity_id,
            'server_revision', v_saved_event.server_revision);
    end if;

    -- Upgrade both new and previously cached entity_not_found results so the
    -- original client mutation ID remains idempotent across application restarts.
    update public.calendar_event_mutations
    set result = v_result
    where owner_id = v_owner_id
      and mutation_id = v_mutation_id
      and request_fingerprint = md5(p_mutation::text);

    return v_result;
end;
$$;

revoke all on function public.moicalendar_apply_calendar_mutation_v1(jsonb) from public, anon, authenticated;
revoke all on function public.moicalendar_apply_calendar_mutation(jsonb) from public, anon;
grant execute on function public.moicalendar_apply_calendar_mutation(jsonb) to authenticated;

