-- MoiCalendar portable cloud-account export.
-- Account deletion is intentionally not exposed as a database RPC: deleting
-- auth.users requires the trusted delete-account Edge Function.

create function public.moicalendar_export_account_data()
returns jsonb
language plpgsql
stable
security definer
set search_path = public, pg_temp
as $$
declare
    v_owner_id uuid := auth.uid();
    v_preferences jsonb;
    v_calendars jsonb;
    v_calendar_events jsonb;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;

    select jsonb_build_object(
        'displayName', profile_row.display_name,
        'timeZoneId', profile_row.time_zone_id)
    into v_preferences
    from public.profiles profile_row
    where profile_row.id = v_owner_id;

    v_preferences := coalesce(
        v_preferences,
        jsonb_build_object('displayName', null, 'timeZoneId', null));

    select coalesce(
        jsonb_agg(
            jsonb_build_object(
                'id', calendar_row.id,
                'name', calendar_row.name,
                'color', calendar_row.color,
                'isDefault', calendar_row.is_default,
                'createdAtUtc', calendar_row.created_at,
                'updatedAtUtc', calendar_row.updated_at,
                'deletedAtUtc', calendar_row.deleted_at)
            order by calendar_row.id),
        '[]'::jsonb)
    into v_calendars
    from public.calendars calendar_row
    where calendar_row.owner_id = v_owner_id;

    select coalesce(
        jsonb_agg(
            jsonb_build_object(
                'calendarId', event_row.calendar_id,
                'calendarEvent', public.moicalendar_calendar_event_json(event_row))
            order by event_row.id),
        '[]'::jsonb)
    into v_calendar_events
    from public.calendar_events event_row
    where event_row.owner_id = v_owner_id;

    return jsonb_build_object(
        'format', 'moicalendar-cloud-export',
        'schemaVersion', 1,
        'exportedAtUtc', transaction_timestamp(),
        'preferences', v_preferences,
        'calendars', v_calendars,
        'calendarEvents', v_calendar_events);
end;
$$;

revoke all on function public.moicalendar_export_account_data()
    from public, anon;
grant execute on function public.moicalendar_export_account_data()
    to authenticated;

comment on function public.moicalendar_export_account_data() is
    'Exports only the authenticated owner portable calendars, events and user-visible preferences; authentication, device and synchronization internals are excluded.';
