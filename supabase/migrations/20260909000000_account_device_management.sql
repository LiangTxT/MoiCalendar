-- MoiCalendar account device management.
-- Device administration remains application-level: it does not revoke Supabase Auth sessions.

create function public.moicalendar_device_json(p_device public.devices)
returns jsonb
language sql
stable
set search_path = public, pg_temp
as $$
    select jsonb_build_object(
        'id', p_device.id,
        'name', p_device.name,
        'platform', p_device.platform,
        'created_at', p_device.created_at,
        'last_seen_at', p_device.last_seen_at,
        'deleted_at', p_device.deleted_at
    );
$$;

create function public.moicalendar_require_active_device(
    p_device_id uuid,
    p_owner_id uuid)
returns void
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    v_deleted_at timestamptz;
begin
    select deleted_at into v_deleted_at
    from public.devices
    where id = p_device_id and owner_id = p_owner_id;

    if not found then
        raise exception using errcode = '42501', message = 'moicalendar_device_not_found';
    end if;
    if v_deleted_at is not null then
        raise exception using errcode = '42501', message = 'moicalendar_device_revoked';
    end if;
end;
$$;

-- Heartbeat/acknowledgement fields do not allocate owner revisions. This avoids
-- turning a successful-sync acknowledgement into another Realtime sync wake-up.
create function public.moicalendar_set_device_revision()
returns trigger
language plpgsql
security definer
set search_path = public, pg_temp
as $$
begin
    if tg_op = 'UPDATE' then
        if new.id is distinct from old.id then
            raise exception 'devices id cannot be changed';
        end if;
        if new.owner_id is distinct from old.owner_id then
            raise exception 'devices owner_id cannot be changed';
        end if;
        if new.created_at is distinct from old.created_at then
            raise exception 'devices created_at cannot be changed';
        end if;

        if new.name is not distinct from old.name and
           new.platform is not distinct from old.platform and
           new.updated_at is not distinct from old.updated_at and
           new.deleted_at is not distinct from old.deleted_at then
            new.server_revision := old.server_revision;
            new.server_changed_at := old.server_changed_at;
            return new;
        end if;
    end if;

    new.server_revision := public.moicalendar_next_revision(new.owner_id);
    new.server_changed_at := transaction_timestamp();
    return new;
end;
$$;

drop trigger devices_set_revision on public.devices;
create trigger devices_set_revision
before insert or update on public.devices
for each row execute function public.moicalendar_set_device_revision();

create function public.moicalendar_register_device(
    p_device_id uuid,
    p_name text,
    p_platform text)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    v_owner_id uuid := auth.uid();
    v_device public.devices%rowtype;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;
    if p_device_id is null or p_name is null or p_platform is null or
       char_length(trim(p_name)) not between 1 and 200 or
       char_length(trim(p_platform)) not between 1 and 100 then
        raise exception using errcode = '22023', message = 'Device registration is invalid';
    end if;

    insert into public.profiles (id)
    values (v_owner_id)
    on conflict (id) do nothing;

    select * into v_device
    from public.devices
    where id = p_device_id
    for update;

    if found and v_device.owner_id <> v_owner_id then
        raise exception using errcode = '42501', message = 'moicalendar_device_id_unavailable';
    end if;
    if found then
        if v_device.deleted_at is not null then
            raise exception using errcode = '42501', message = 'moicalendar_device_revoked';
        end if;
        return public.moicalendar_device_json(v_device);
    end if;

    begin
        insert into public.devices (
            id, owner_id, name, platform, server_revision, server_changed_at)
        values (
            p_device_id, v_owner_id, trim(p_name), trim(p_platform), 1, transaction_timestamp())
        returning * into v_device;
    exception when unique_violation then
        select * into v_device
        from public.devices
        where id = p_device_id;
        if not found or v_device.owner_id <> v_owner_id then
            raise exception using errcode = '42501', message = 'moicalendar_device_id_unavailable';
        end if;
        if v_device.deleted_at is not null then
            raise exception using errcode = '42501', message = 'moicalendar_device_revoked';
        end if;
    end;

    return public.moicalendar_device_json(v_device);
end;
$$;

create function public.moicalendar_list_devices()
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
declare
    v_owner_id uuid := auth.uid();
    v_devices jsonb;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;

    select coalesce(
        jsonb_agg(public.moicalendar_device_json(device_row)
            order by device_row.deleted_at nulls first,
                     device_row.last_seen_at desc nulls last,
                     device_row.created_at desc),
        '[]'::jsonb)
    into v_devices
    from public.devices device_row
    where device_row.owner_id = v_owner_id;
    return v_devices;
end;
$$;

create function public.moicalendar_rename_device(
    p_device_id uuid,
    p_name text)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    v_owner_id uuid := auth.uid();
    v_device public.devices%rowtype;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;
    if p_name is null or char_length(trim(p_name)) not between 1 and 200 then
        raise exception using errcode = '22023', message = 'Device name is invalid';
    end if;

    perform public.moicalendar_require_active_device(p_device_id, v_owner_id);
    update public.devices
    set name = trim(p_name), updated_at = transaction_timestamp()
    where id = p_device_id and owner_id = v_owner_id
    returning * into v_device;
    return public.moicalendar_device_json(v_device);
end;
$$;

create function public.moicalendar_revoke_device(p_device_id uuid)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    v_owner_id uuid := auth.uid();
    v_device public.devices%rowtype;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;

    perform public.moicalendar_require_active_device(p_device_id, v_owner_id);
    update public.devices
    set deleted_at = transaction_timestamp(), updated_at = transaction_timestamp()
    where id = p_device_id and owner_id = v_owner_id
    returning * into v_device;
    return public.moicalendar_device_json(v_device);
end;
$$;

create function public.moicalendar_acknowledge_device_sync(
    p_device_id uuid,
    p_server_revision bigint)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    v_owner_id uuid := auth.uid();
    v_device public.devices%rowtype;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;
    if p_server_revision is null or p_server_revision < 0 then
        raise exception using errcode = '22023', message = 'Server revision is invalid';
    end if;
    if not exists (
        select 1 from public.sync_state
        where owner_id = v_owner_id and latest_revision >= p_server_revision) then
        raise exception using errcode = '22023', message = 'Server revision exceeds the owner high-water mark';
    end if;

    perform public.moicalendar_require_active_device(p_device_id, v_owner_id);
    update public.devices
    set last_seen_at = transaction_timestamp(),
        last_acknowledged_revision = greatest(last_acknowledged_revision, p_server_revision)
    where id = p_device_id and owner_id = v_owner_id
    returning * into v_device;
    return public.moicalendar_device_json(v_device);
end;
$$;

create function public.moicalendar_assert_mutation_device_active()
returns trigger
language plpgsql
security definer
set search_path = public, pg_temp
as $$
begin
    perform public.moicalendar_require_active_device(new.device_id, new.owner_id);
    return new;
end;
$$;

create trigger calendar_event_mutations_require_active_device
before insert on public.calendar_event_mutations
for each row execute function public.moicalendar_assert_mutation_device_active();

create function public.moicalendar_pull_calendar_changes_for_device(
    p_device_id uuid,
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
    perform public.moicalendar_require_active_device(p_device_id, v_owner_id);

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

-- Device writes are only allowed through the audited functions above. Direct
-- SELECT remains protected by the existing owner RLS policy.
revoke insert, update on table public.devices from authenticated;
revoke execute on function public.moicalendar_pull_calendar_changes(bigint, integer) from authenticated;

revoke all on function public.moicalendar_device_json(public.devices) from public, anon, authenticated;
revoke all on function public.moicalendar_require_active_device(uuid, uuid) from public, anon, authenticated;
revoke all on function public.moicalendar_set_device_revision() from public, anon, authenticated;
revoke all on function public.moicalendar_assert_mutation_device_active() from public, anon, authenticated;
revoke all on function public.moicalendar_register_device(uuid, text, text) from public, anon;
revoke all on function public.moicalendar_list_devices() from public, anon;
revoke all on function public.moicalendar_rename_device(uuid, text) from public, anon;
revoke all on function public.moicalendar_revoke_device(uuid) from public, anon;
revoke all on function public.moicalendar_acknowledge_device_sync(uuid, bigint) from public, anon;
revoke all on function public.moicalendar_pull_calendar_changes_for_device(uuid, bigint, integer) from public, anon;

grant execute on function public.moicalendar_register_device(uuid, text, text) to authenticated;
grant execute on function public.moicalendar_list_devices() to authenticated;
grant execute on function public.moicalendar_rename_device(uuid, text) to authenticated;
grant execute on function public.moicalendar_revoke_device(uuid) to authenticated;
grant execute on function public.moicalendar_acknowledge_device_sync(uuid, bigint) to authenticated;
grant execute on function public.moicalendar_pull_calendar_changes_for_device(uuid, bigint, integer) to authenticated;

comment on function public.moicalendar_revoke_device(uuid) is
    'Creates an application-level device tombstone; it does not revoke a Supabase Auth session.';
comment on function public.moicalendar_acknowledge_device_sync(uuid, bigint) is
    'Updates last-successful-sync metadata without allocating a synchronization revision.';
comment on function public.moicalendar_pull_calendar_changes_for_device(uuid, bigint, integer) is
    'Returns cursor-based calendar changes only after verifying that the calling device is active.';
