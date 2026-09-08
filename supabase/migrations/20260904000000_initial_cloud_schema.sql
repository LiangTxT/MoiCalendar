-- MoiCalendar cloud schema v1.
-- PostgreSQL-native tables are used throughout. The only Supabase-specific
-- references are auth.users and auth.uid(), which isolate the authentication
-- boundary for managed and self-hosted Supabase installations.

create table public.profiles (
    id uuid primary key references auth.users (id) on delete cascade,
    display_name text,
    time_zone_id text,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    deleted_at timestamptz,
    server_revision bigint not null default 1,
    server_changed_at timestamptz not null default now(),
    constraint profiles_display_name_length
        check (display_name is null or char_length(display_name) between 1 and 200),
    constraint profiles_time_zone_id_length
        check (time_zone_id is null or char_length(time_zone_id) between 1 and 255),
    constraint profiles_timestamp_order
        check (updated_at >= created_at and (deleted_at is null or deleted_at <= updated_at)),
    constraint profiles_server_revision_positive check (server_revision > 0)
);

-- A per-owner counter serializes revision allocation. Locking this single row
-- prevents a later revision from committing before an earlier revision for the
-- same owner, so clients can safely use server_revision as an incremental cursor.
create table public.sync_state (
    owner_id uuid primary key references public.profiles (id) on delete cascade,
    latest_revision bigint not null default 1,
    updated_at timestamptz not null default now(),
    constraint sync_state_latest_revision_positive check (latest_revision > 0)
);

create table public.devices (
    id uuid primary key,
    owner_id uuid not null references public.profiles (id) on delete cascade,
    name text not null,
    platform text,
    last_seen_at timestamptz,
    last_acknowledged_revision bigint not null default 0,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    deleted_at timestamptz,
    server_revision bigint not null,
    server_changed_at timestamptz not null,
    constraint devices_name_length check (char_length(name) between 1 and 200),
    constraint devices_platform_length
        check (platform is null or char_length(platform) between 1 and 100),
    constraint devices_last_acknowledged_revision_nonnegative
        check (last_acknowledged_revision >= 0),
    constraint devices_timestamp_order
        check (updated_at >= created_at and (deleted_at is null or deleted_at <= updated_at)),
    constraint devices_server_revision_positive check (server_revision > 0),
    unique (id, owner_id)
);

create table public.calendars (
    id uuid primary key,
    owner_id uuid not null references public.profiles (id) on delete cascade,
    name text not null,
    color text,
    is_default boolean not null default false,
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    deleted_at timestamptz,
    server_revision bigint not null,
    server_changed_at timestamptz not null,
    constraint calendars_name_length check (char_length(name) between 1 and 200),
    constraint calendars_color_format
        check (color is null or color ~ '^#[0-9A-Fa-f]{6}$'),
    constraint calendars_timestamp_order
        check (updated_at >= created_at and (deleted_at is null or deleted_at <= updated_at)),
    constraint calendars_server_revision_positive check (server_revision > 0),
    unique (id, owner_id)
);

create table public.calendar_events (
    id uuid primary key,
    owner_id uuid not null references public.profiles (id) on delete cascade,
    calendar_id uuid not null,
    title text not null,
    description text not null default '',
    location text not null default '',
    start_at timestamptz not null,
    end_at timestamptz not null,
    time_zone_id text not null,
    is_all_day boolean not null default false,
    recurrence_rule text,
    external_uid text,
    created_at timestamptz not null,
    updated_at timestamptz not null,
    deleted_at timestamptz,
    server_revision bigint not null,
    server_changed_at timestamptz not null,
    constraint calendar_events_calendar_owner_fk
        foreign key (calendar_id, owner_id)
        references public.calendars (id, owner_id)
        on delete cascade,
    constraint calendar_events_title_length check (char_length(title) between 1 and 200),
    constraint calendar_events_description_length check (char_length(description) <= 4000),
    constraint calendar_events_location_length check (char_length(location) <= 300),
    constraint calendar_events_time_zone_id_length check (char_length(time_zone_id) between 1 and 255),
    constraint calendar_events_recurrence_rule_length
        check (recurrence_rule is null or char_length(recurrence_rule) between 1 and 2048),
    constraint calendar_events_external_uid_length
        check (external_uid is null or char_length(external_uid) between 1 and 1024),
    constraint calendar_events_time_range check (end_at > start_at),
    constraint calendar_events_timestamp_order
        check (updated_at >= created_at and (deleted_at is null or deleted_at <= updated_at)),
    constraint calendar_events_server_revision_positive check (server_revision > 0)
);

create index devices_owner_revision_idx
    on public.devices (owner_id, server_revision);
create index devices_owner_last_seen_idx
    on public.devices (owner_id, last_seen_at desc)
    where deleted_at is null;

create index calendars_owner_revision_idx
    on public.calendars (owner_id, server_revision);
create unique index calendars_one_live_default_per_owner_idx
    on public.calendars (owner_id)
    where is_default and deleted_at is null;

create index calendar_events_owner_revision_idx
    on public.calendar_events (owner_id, server_revision);
create index calendar_events_calendar_time_range_idx
    on public.calendar_events (calendar_id, start_at, end_at)
    where deleted_at is null;
create index calendar_events_calendar_external_uid_idx
    on public.calendar_events (calendar_id, external_uid)
    where external_uid is not null;
create index calendar_events_owner_updated_idx
    on public.calendar_events (owner_id, updated_at desc);

create function public.moicalendar_next_revision(p_owner_id uuid)
returns bigint
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
    allocated_revision bigint;
begin
    update public.sync_state
    set latest_revision = latest_revision + 1,
        updated_at = transaction_timestamp()
    where owner_id = p_owner_id
    returning latest_revision into allocated_revision;

    if allocated_revision is null then
        raise exception 'MoiCalendar profile sync state does not exist for owner %', p_owner_id;
    end if;

    return allocated_revision;
end;
$$;

create function public.moicalendar_initialize_profile_sync_state()
returns trigger
language plpgsql
security definer
set search_path = public, pg_temp
as $$
begin
    insert into public.sync_state (owner_id, latest_revision, updated_at)
    values (new.id, new.server_revision, new.server_changed_at);
    return new;
end;
$$;

create function public.moicalendar_set_profile_revision()
returns trigger
language plpgsql
security definer
set search_path = public, pg_temp
as $$
begin
    if tg_op = 'INSERT' then
        new.server_revision := 1;
    else
        if new.id is distinct from old.id then
            raise exception 'Profile id cannot be changed';
        end if;
        if new.created_at is distinct from old.created_at then
            raise exception 'Profile created_at cannot be changed';
        end if;
        new.server_revision := public.moicalendar_next_revision(new.id);
    end if;

    new.server_changed_at := transaction_timestamp();
    return new;
end;
$$;

create function public.moicalendar_set_owned_row_revision()
returns trigger
language plpgsql
security definer
set search_path = public, pg_temp
as $$
begin
    if tg_op = 'UPDATE' then
        if new.id is distinct from old.id then
            raise exception '% id cannot be changed', tg_table_name;
        end if;
        if new.owner_id is distinct from old.owner_id then
            raise exception '% owner_id cannot be changed', tg_table_name;
        end if;
        if new.created_at is distinct from old.created_at then
            raise exception '% created_at cannot be changed', tg_table_name;
        end if;
    end if;

    new.server_revision := public.moicalendar_next_revision(new.owner_id);
    new.server_changed_at := transaction_timestamp();
    return new;
end;
$$;

create trigger profiles_set_revision
before insert or update on public.profiles
for each row execute function public.moicalendar_set_profile_revision();

create trigger profiles_initialize_sync_state
after insert on public.profiles
for each row execute function public.moicalendar_initialize_profile_sync_state();

create trigger devices_set_revision
before insert or update on public.devices
for each row execute function public.moicalendar_set_owned_row_revision();

create trigger calendars_set_revision
before insert or update on public.calendars
for each row execute function public.moicalendar_set_owned_row_revision();

create trigger calendar_events_set_revision
before insert or update on public.calendar_events
for each row execute function public.moicalendar_set_owned_row_revision();

revoke all on function public.moicalendar_next_revision(uuid) from public, anon, authenticated;
revoke all on function public.moicalendar_initialize_profile_sync_state() from public, anon, authenticated;
revoke all on function public.moicalendar_set_profile_revision() from public, anon, authenticated;
revoke all on function public.moicalendar_set_owned_row_revision() from public, anon, authenticated;

alter table public.profiles enable row level security;
alter table public.sync_state enable row level security;
alter table public.devices enable row level security;
alter table public.calendars enable row level security;
alter table public.calendar_events enable row level security;

create policy profiles_select_own
on public.profiles for select to authenticated
using ((select auth.uid()) = id);
create policy profiles_insert_own
on public.profiles for insert to authenticated
with check ((select auth.uid()) = id);
create policy profiles_update_own
on public.profiles for update to authenticated
using ((select auth.uid()) = id)
with check ((select auth.uid()) = id);

create policy sync_state_select_own
on public.sync_state for select to authenticated
using ((select auth.uid()) = owner_id);

create policy devices_select_own
on public.devices for select to authenticated
using ((select auth.uid()) = owner_id);
create policy devices_insert_own
on public.devices for insert to authenticated
with check ((select auth.uid()) = owner_id);
create policy devices_update_own
on public.devices for update to authenticated
using ((select auth.uid()) = owner_id)
with check ((select auth.uid()) = owner_id);

create policy calendars_select_own
on public.calendars for select to authenticated
using ((select auth.uid()) = owner_id);
create policy calendars_insert_own
on public.calendars for insert to authenticated
with check ((select auth.uid()) = owner_id);
create policy calendars_update_own
on public.calendars for update to authenticated
using ((select auth.uid()) = owner_id)
with check ((select auth.uid()) = owner_id);

create policy calendar_events_select_own
on public.calendar_events for select to authenticated
using ((select auth.uid()) = owner_id);
create policy calendar_events_insert_own
on public.calendar_events for insert to authenticated
with check ((select auth.uid()) = owner_id);
create policy calendar_events_update_own
on public.calendar_events for update to authenticated
using ((select auth.uid()) = owner_id)
with check ((select auth.uid()) = owner_id);

revoke all on table public.profiles from public, anon;
revoke all on table public.sync_state from public, anon;
revoke all on table public.devices from public, anon;
revoke all on table public.calendars from public, anon;
revoke all on table public.calendar_events from public, anon;

grant select, insert, update on table public.profiles to authenticated;
grant select on table public.sync_state to authenticated;
grant select, insert, update on table public.devices to authenticated;
grant select, insert, update on table public.calendars to authenticated;
grant select, insert, update on table public.calendar_events to authenticated;

comment on table public.sync_state is
    'Per-owner monotonic revision allocator and synchronization high-water mark.';
comment on column public.devices.last_acknowledged_revision is
    'Highest owner revision durably processed by this client device.';
comment on column public.calendar_events.server_revision is
    'Server-assigned per-owner revision used for incremental synchronization.';
comment on column public.calendar_events.server_changed_at is
    'Server timestamp for the row change; separate from client-authored updated_at.';
