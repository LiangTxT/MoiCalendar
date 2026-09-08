-- Preserve the v1 mutation protocol and enrich deterministic conflict results
-- with the current server entity. This gives clients enough data for a future
-- explicit resolution workflow without weakening optimistic concurrency.

alter function public.moicalendar_apply_calendar_mutation(jsonb)
    rename to moicalendar_apply_calendar_mutation_v1;

revoke all on function public.moicalendar_apply_calendar_mutation_v1(jsonb)
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
    v_result jsonb;
    v_conflict_code text;
    v_current_event public.calendar_events%rowtype;
begin
    if v_owner_id is null then
        raise exception using errcode = '42501', message = 'Authentication required';
    end if;

    v_result := public.moicalendar_apply_calendar_mutation_v1(p_mutation);
    if v_result ->> 'status' <> 'conflict' then
        return v_result;
    end if;

    v_conflict_code := v_result #>> '{conflict,code}';
    if v_conflict_code not in ('entity_exists', 'entity_deleted', 'stale_revision') or
       v_result #> '{conflict,current_entity}' is not null then
        return v_result;
    end if;

    v_mutation_id := (p_mutation ->> 'mutation_id')::uuid;
    v_entity_id := (p_mutation ->> 'entity_id')::uuid;
    select * into v_current_event
    from public.calendar_events
    where owner_id = v_owner_id and id = v_entity_id;

    if not found then
        return v_result;
    end if;

    v_result := jsonb_set(
        v_result,
        '{conflict,current_entity}',
        public.moicalendar_calendar_event_json(v_current_event),
        true);

    -- The first enriched response becomes the stable idempotency result for
    -- retries of this authenticated user's mutation ID.
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

comment on function public.moicalendar_apply_calendar_mutation_v1(jsonb) is
    'Internal v1 mutation implementation retained for migration compatibility.';
comment on function public.moicalendar_apply_calendar_mutation(jsonb) is
    'Applies an owner-scoped idempotent mutation and returns current server data for resolvable conflicts.';
