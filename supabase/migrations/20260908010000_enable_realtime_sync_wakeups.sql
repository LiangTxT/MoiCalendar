-- Realtime is only a best-effort wake-up for the authoritative cursor-based pull protocol.
-- Publishing the small, owner-scoped high-water row avoids exposing calendar payloads on
-- the WebSocket path. Existing RLS continues to restrict each authenticated subscriber.

revoke all on table public.sync_state from authenticated;
grant select on table public.sync_state to authenticated;

do $$
begin
    if not exists (
        select 1
        from pg_catalog.pg_publication
        where pubname = 'supabase_realtime') then
        create publication supabase_realtime;
    end if;

    if not exists (
        select 1
        from pg_catalog.pg_publication_tables
        where pubname = 'supabase_realtime'
          and schemaname = 'public'
          and tablename = 'sync_state') then
        alter publication supabase_realtime add table public.sync_state;
    end if;
end
$$;

comment on table public.sync_state is
    'Per-owner monotonic revision allocator, synchronization high-water mark, and Realtime wake-up source. Realtime payloads are non-authoritative.';
