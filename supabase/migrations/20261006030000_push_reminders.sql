-- Private, per-device Web Push subscriptions and idempotent delivery leases.
create table public.reminder_push_subscriptions (
    owner_id uuid not null references auth.users(id) on delete cascade,
    device_id uuid not null references public.devices(id) on delete cascade,
    subscription jsonb not null,
    updated_at timestamptz not null default now(),
    primary key (owner_id, device_id)
);
create table public.reminder_push_deliveries (
    owner_id uuid not null references auth.users(id) on delete cascade,
    device_id uuid not null references public.devices(id) on delete cascade,
    reminder_key text not null check (length(reminder_key) <= 300),
    lease_id uuid not null,
    lease_until timestamptz not null,
    attempts integer not null default 1,
    delivered_at timestamptz,
    created_at timestamptz not null default now(),
    primary key (owner_id, device_id, reminder_key)
);
alter table public.reminder_push_subscriptions enable row level security;
alter table public.reminder_push_deliveries enable row level security;
revoke all on public.reminder_push_subscriptions, public.reminder_push_deliveries from public, anon, authenticated;

create function public.moicalendar_register_push_subscription(p_device_id uuid, p_subscription jsonb)
returns jsonb language plpgsql security definer set search_path = pg_catalog, pg_temp as $$
declare v_owner uuid := auth.uid(); v_old public.reminder_push_subscriptions%rowtype;
begin
    if v_owner is null then raise exception using errcode = '42501', message = 'Authentication required'; end if;
    perform public.moicalendar_require_active_device(p_device_id, v_owner);
    -- Serialize registration limits across all devices belonging to one owner.
    perform pg_advisory_xact_lock(hashtextextended(v_owner::text, 76103));
    if p_subscription is null or jsonb_typeof(p_subscription) <> 'object' or
       coalesce(p_subscription ->> 'endpoint', '') !~ '^https://(fcm\.googleapis\.com|updates\.push\.services\.mozilla\.com|web\.push\.apple\.com|[a-z0-9-]+\.(notify|wns)\.windows\.com)/[^[:space:]]+$' or
       length(p_subscription ->> 'endpoint') > 4096 or
       coalesce(p_subscription #>> '{keys,p256dh}', '') !~ '^[A-Za-z0-9_-]{87}={0,1}$' or
       coalesce(p_subscription #>> '{keys,auth}', '') !~ '^[A-Za-z0-9_-]{22}={0,2}$' then
        raise exception using errcode = '22023', message = 'Invalid push subscription';
    end if;
    select * into v_old from public.reminder_push_subscriptions where owner_id = v_owner and device_id = p_device_id;
    if not found and (select count(*) from public.reminder_push_subscriptions where owner_id = v_owner) >= 20 then
        raise exception using errcode = '54000', message = 'Push subscription limit reached';
    end if;
    if v_old.subscription = p_subscription then return '{}'::jsonb; end if;
    if v_old.updated_at > now() - interval '5 seconds' then
        raise exception using errcode = '54000', message = 'Push registration too frequent';
    end if;
    insert into public.reminder_push_subscriptions(owner_id,device_id,subscription)
    values(v_owner,p_device_id,jsonb_build_object('endpoint',p_subscription ->> 'endpoint','keys',p_subscription -> 'keys'))
    on conflict(owner_id,device_id) do update set subscription = excluded.subscription, updated_at = now();
    return '{}'::jsonb;
end;
$$;
revoke all on function public.moicalendar_register_push_subscription(uuid,jsonb) from public, anon;
grant execute on function public.moicalendar_register_push_subscription(uuid,jsonb) to authenticated;

create function public.moicalendar_unregister_push_subscription(p_device_id uuid)
returns jsonb language plpgsql security definer set search_path = pg_catalog, pg_temp as $$
begin
    if auth.uid() is null then raise exception using errcode = '42501', message = 'Authentication required'; end if;
    delete from public.reminder_push_subscriptions where owner_id = auth.uid() and device_id = p_device_id;
    return '{}'::jsonb;
end;
$$;
revoke all on function public.moicalendar_unregister_push_subscription(uuid) from public, anon;
grant execute on function public.moicalendar_unregister_push_subscription(uuid) to authenticated;

-- Only the server worker can enumerate titles/subscriptions or claim a delivery.
create function public.moicalendar_reminder_events_page(p_after_id uuid default null, p_limit integer default 100)
returns jsonb language sql stable security definer set search_path = pg_catalog, pg_temp as $$
select coalesce(jsonb_agg(item order by id), '[]'::jsonb) from (
    select e.id, jsonb_build_object('ownerId',e.owner_id,'revision',e.server_revision,
        'event',public.moicalendar_calendar_event_json(e), 'subscriptions',
        (select jsonb_agg(jsonb_build_object('deviceId',s.device_id,'subscription',s.subscription))
         from public.reminder_push_subscriptions s join public.devices d on d.id=s.device_id and d.owner_id=s.owner_id
         where s.owner_id=e.owner_id and d.deleted_at is null)) item
    from public.calendar_events e
    where e.deleted_at is null and e.reminder_minutes_before_start is not null
      and (p_after_id is null or e.id > p_after_id)
      and exists(select 1 from public.reminder_push_subscriptions s join public.devices d
          on d.id=s.device_id and d.owner_id=s.owner_id where s.owner_id=e.owner_id and d.deleted_at is null)
    order by e.id limit least(greatest(p_limit,1),200)
) page;
$$;
revoke all on function public.moicalendar_reminder_events_page(uuid,integer) from public, anon, authenticated;
grant execute on function public.moicalendar_reminder_events_page(uuid,integer) to service_role;

create function public.moicalendar_claim_reminder(p_owner_id uuid,p_device_id uuid,p_event_id uuid,
    p_revision bigint,p_key text,p_start timestamptz,p_due timestamptz)
returns uuid language plpgsql security definer set search_path = pg_catalog, pg_temp as $$
declare v_lease uuid;
begin
    if p_key is null or length(p_key)>300 or p_due>now()+interval '5 seconds' or p_due<now()-interval '15 minutes' then return null; end if;
    if not exists(select 1 from public.calendar_events e join public.devices d on d.id=p_device_id and d.owner_id=e.owner_id
        join public.reminder_push_subscriptions s on s.owner_id=e.owner_id and s.device_id=d.id
        where e.id=p_event_id and e.owner_id=p_owner_id and e.server_revision=p_revision and e.deleted_at is null
          and e.reminder_minutes_before_start is not null and d.deleted_at is null
          and not exists(select 1 from jsonb_array_elements_text(e.excluded_occurrence_starts_utc) x where x::timestamptz=p_start)) then return null; end if;
    insert into public.reminder_push_deliveries(owner_id,device_id,reminder_key,lease_id,lease_until)
    values(p_owner_id,p_device_id,p_key,gen_random_uuid(),now()+interval '60 seconds')
    on conflict(owner_id,device_id,reminder_key) do update set
        lease_id=gen_random_uuid(),lease_until=now()+interval '60 seconds',attempts=public.reminder_push_deliveries.attempts+1
    where public.reminder_push_deliveries.delivered_at is null and public.reminder_push_deliveries.lease_until<now()
        and public.reminder_push_deliveries.attempts<5
    returning lease_id into v_lease;
    return v_lease;
end;
$$;
revoke all on function public.moicalendar_claim_reminder(uuid,uuid,uuid,bigint,text,timestamptz,timestamptz) from public, anon, authenticated;
grant execute on function public.moicalendar_claim_reminder(uuid,uuid,uuid,bigint,text,timestamptz,timestamptz) to service_role;

create function public.moicalendar_finish_reminder(p_lease_id uuid,p_success boolean)
returns void language sql security definer set search_path = pg_catalog, pg_temp as $$
    update public.reminder_push_deliveries set delivered_at=case when p_success then now() else null end,
        lease_until=now()+interval '30 seconds' where lease_id=p_lease_id and delivered_at is null;
$$;
revoke all on function public.moicalendar_finish_reminder(uuid,boolean) from public, anon, authenticated;
grant execute on function public.moicalendar_finish_reminder(uuid,boolean) to service_role;

create function public.moicalendar_prune_push(p_owner_id uuid default null,p_device_id uuid default null,p_endpoint text default null)
returns void language plpgsql security definer set search_path = pg_catalog, pg_temp as $$
begin
    -- Match endpoint as well: a failed send must not erase a newly rotated subscription.
    delete from public.reminder_push_subscriptions where owner_id=p_owner_id and device_id=p_device_id and subscription ->> 'endpoint'=p_endpoint;
    delete from public.reminder_push_deliveries where created_at<now()-interval '30 days';
    delete from public.reminder_push_subscriptions s using public.devices d where d.id=s.device_id and d.deleted_at is not null;
end;
$$;
revoke all on function public.moicalendar_prune_push(uuid,uuid,text) from public, anon, authenticated;
grant execute on function public.moicalendar_prune_push(uuid,uuid,text) to service_role;
