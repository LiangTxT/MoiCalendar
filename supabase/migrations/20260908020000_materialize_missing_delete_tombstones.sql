-- A delete for a calendar event created before cloud synchronization was enabled
-- is already locally authoritative, even when no cloud row exists yet. Materialize
-- a server-revisioned tombstone so another device cannot later resurrect that event.

alter function public.moicalendar_apply_calendar_mutation(jsonb)
    rename to moicalendar_apply_calendar_mutation_v2;

revoke all on function public.moicalendar_apply_calendar_mutation_v2(jsonb)
    from public, anon, authenticated;

create function public.moicalendar_apply_calendar_mutation(p_mutation jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
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

revoke all on function public.moicalendar_apply_calendar_mutation(jsonb)
    from public, anon;
grant execute on function public.moicalendar_apply_calendar_mutation(jsonb)
    to authenticated;

comment on function public.moicalendar_apply_calendar_mutation_v2(jsonb) is
    'Internal v2 mutation implementation retained for migration compatibility.';
comment on function public.moicalendar_apply_calendar_mutation(jsonb) is
    'Applies an idempotent mutation and materializes missing legacy deletes as server-revisioned tombstones.';
