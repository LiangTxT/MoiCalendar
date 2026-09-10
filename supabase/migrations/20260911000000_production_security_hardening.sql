-- MoiCalendar production security hardening.
-- Keep client access RPC-first, make SECURITY DEFINER lookup deterministic,
-- and provide a server-only rate limiter for destructive account deletion.

revoke create on schema public from public, anon, authenticated;

-- Calendar and device mutations are exposed only through owner-verifying RPCs.
-- sync_state keeps SELECT because it is the small RLS-filtered Realtime wake-up row.
revoke all on table public.profiles from anon, authenticated;
revoke all on table public.devices from anon, authenticated;
revoke all on table public.calendars from anon, authenticated;
revoke all on table public.calendar_events from anon, authenticated;
revoke all on table public.calendar_event_mutations from anon, authenticated;
revoke insert, update, delete, truncate, references, trigger
    on table public.sync_state from anon, authenticated;
grant select on table public.sync_state to authenticated;

create table public.account_deletion_rate_limits (
    owner_id uuid primary key references auth.users (id) on delete cascade,
    window_started_at timestamptz not null,
    attempt_count integer not null,
    updated_at timestamptz not null,
    constraint account_deletion_rate_limits_attempt_count
        check (attempt_count between 1 and 1000),
    constraint account_deletion_rate_limits_timestamp_order
        check (updated_at >= window_started_at)
);

alter table public.account_deletion_rate_limits enable row level security;
revoke all on table public.account_deletion_rate_limits from public, anon, authenticated;

create function public.moicalendar_claim_account_deletion_attempt(p_owner_id uuid)
returns boolean
language plpgsql
security definer
set search_path = pg_catalog, pg_temp
as $$
declare
    v_now timestamptz := clock_timestamp();
    v_attempt_count integer;
begin
    if p_owner_id is null then
        raise exception using errcode = '22023', message = 'Account id is required';
    end if;

    insert into public.account_deletion_rate_limits (
        owner_id, window_started_at, attempt_count, updated_at)
    values (p_owner_id, v_now, 1, v_now)
    on conflict (owner_id) do update
    set window_started_at = case
            when public.account_deletion_rate_limits.window_started_at <= v_now - interval '15 minutes'
                then v_now
            else public.account_deletion_rate_limits.window_started_at
        end,
        attempt_count = case
            when public.account_deletion_rate_limits.window_started_at <= v_now - interval '15 minutes'
                then 1
            else public.account_deletion_rate_limits.attempt_count + 1
        end,
        updated_at = v_now
    returning attempt_count into v_attempt_count;

    return v_attempt_count <= 3;
end;
$$;

-- PostgreSQL grants EXECUTE to PUBLIC for new functions by default. This
-- destructive helper is intentionally callable only with the server role.
revoke all on function public.moicalendar_claim_account_deletion_attempt(uuid)
    from public, anon, authenticated;
grant execute on function public.moicalendar_claim_account_deletion_attempt(uuid)
    to service_role;
alter function public.moicalendar_claim_account_deletion_attempt(uuid)
    set search_path = pg_catalog, pg_temp;

-- Pin all existing SECURITY DEFINER and helper routines to trusted schemas.
-- Application-owned objects are schema-qualified inside each function body.
alter function public.moicalendar_next_revision(uuid)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_initialize_profile_sync_state()
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_set_profile_revision()
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_set_owned_row_revision()
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_default_calendar_id(uuid)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_calendar_event_json(public.calendar_events)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_apply_calendar_mutation_v1(jsonb)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_pull_calendar_changes(bigint, integer)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_apply_calendar_mutation_v2(jsonb)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_apply_calendar_mutation(jsonb)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_device_json(public.devices)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_require_active_device(uuid, uuid)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_set_device_revision()
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_register_device(uuid, text, text)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_list_devices()
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_rename_device(uuid, text)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_revoke_device(uuid)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_acknowledge_device_sync(uuid, bigint)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_assert_mutation_device_active()
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_pull_calendar_changes_for_device(uuid, bigint, integer)
    set search_path = pg_catalog, pg_temp;
alter function public.moicalendar_export_account_data()
    set search_path = pg_catalog, pg_temp;

-- Reassert the complete public API after historical migrations renamed several
-- internal versions of the mutation function.
revoke all on function public.moicalendar_next_revision(uuid) from public, anon, authenticated;
revoke all on function public.moicalendar_initialize_profile_sync_state() from public, anon, authenticated;
revoke all on function public.moicalendar_set_profile_revision() from public, anon, authenticated;
revoke all on function public.moicalendar_set_owned_row_revision() from public, anon, authenticated;
revoke all on function public.moicalendar_default_calendar_id(uuid) from public, anon, authenticated;
revoke all on function public.moicalendar_calendar_event_json(public.calendar_events) from public, anon, authenticated;
revoke all on function public.moicalendar_apply_calendar_mutation_v1(jsonb) from public, anon, authenticated;
revoke all on function public.moicalendar_apply_calendar_mutation_v2(jsonb) from public, anon, authenticated;
revoke all on function public.moicalendar_pull_calendar_changes(bigint, integer) from public, anon, authenticated;
revoke all on function public.moicalendar_device_json(public.devices) from public, anon, authenticated;
revoke all on function public.moicalendar_require_active_device(uuid, uuid) from public, anon, authenticated;
revoke all on function public.moicalendar_set_device_revision() from public, anon, authenticated;
revoke all on function public.moicalendar_assert_mutation_device_active() from public, anon, authenticated;

revoke all on function public.moicalendar_apply_calendar_mutation(jsonb) from public, anon;
revoke all on function public.moicalendar_register_device(uuid, text, text) from public, anon;
revoke all on function public.moicalendar_list_devices() from public, anon;
revoke all on function public.moicalendar_rename_device(uuid, text) from public, anon;
revoke all on function public.moicalendar_revoke_device(uuid) from public, anon;
revoke all on function public.moicalendar_acknowledge_device_sync(uuid, bigint) from public, anon;
revoke all on function public.moicalendar_pull_calendar_changes_for_device(uuid, bigint, integer) from public, anon;
revoke all on function public.moicalendar_export_account_data() from public, anon;

grant execute on function public.moicalendar_apply_calendar_mutation(jsonb) to authenticated;
grant execute on function public.moicalendar_register_device(uuid, text, text) to authenticated;
grant execute on function public.moicalendar_list_devices() to authenticated;
grant execute on function public.moicalendar_rename_device(uuid, text) to authenticated;
grant execute on function public.moicalendar_revoke_device(uuid) to authenticated;
grant execute on function public.moicalendar_acknowledge_device_sync(uuid, bigint) to authenticated;
grant execute on function public.moicalendar_pull_calendar_changes_for_device(uuid, bigint, integer) to authenticated;
grant execute on function public.moicalendar_export_account_data() to authenticated;

comment on table public.account_deletion_rate_limits is
    'Server-only fixed-window limiter for authenticated account deletion attempts.';
comment on function public.moicalendar_claim_account_deletion_attempt(uuid) is
    'Claims one server-verified account deletion attempt; executable only by the trusted service role.';
