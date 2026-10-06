-- 登录细分错误仅允许服务端查询；匿名及登录用户不能直接枚举 auth.users。
create table public.login_attempt_limits (
    key text primary key,
    window_start timestamptz not null,
    attempts integer not null check (attempts > 0)
);
alter table public.login_attempt_limits enable row level security;
revoke all on public.login_attempt_limits from public, anon, authenticated;

create function public.moicalendar_claim_login_attempt(p_email_key text)
returns boolean language plpgsql security definer
set search_path = pg_catalog, pg_temp as $$
declare
    v_now timestamptz := clock_timestamp();
    v_key text;
    v_window interval;
    v_limit integer;
    v_count integer;
    v_allowed boolean := true;
begin
    if p_email_key is null or p_email_key !~ '^[0-9a-f]{64}$' then
        raise exception 'invalid rate key';
    end if;
    -- 多个函数实例共享限流；全站锁保证全局及邮箱计数一起更新。
    perform pg_advisory_xact_lock(614937281);
    delete from public.login_attempt_limits where window_start < v_now - interval '1 day';
    for v_key, v_window, v_limit in
        select * from (values
            ('global:minute', interval '1 minute', 30),
            ('global:hour', interval '1 hour', 300),
            ('email:' || p_email_key, interval '15 minutes', 10)
        ) as limits(key, duration, maximum)
    loop
        insert into public.login_attempt_limits as target(key, window_start, attempts)
        values(v_key, v_now, 1)
        on conflict(key) do update set
            window_start = case when target.window_start <= v_now - v_window then v_now else target.window_start end,
            attempts = case when target.window_start <= v_now - v_window then 1
                else least(target.attempts + 1, v_limit + 1) end
        returning attempts into v_count;
        v_allowed := v_allowed and v_count <= v_limit;
    end loop;
    return v_allowed;
end;
$$;

create function public.moicalendar_classify_login_email(p_email text)
returns jsonb language sql stable security definer
set search_path = pg_catalog, pg_temp as $$
    select coalesce((select jsonb_build_object(
        'exists', true,
        'password_set', coalesce(u.encrypted_password, '') <> '')
        from auth.users u where lower(u.email) = lower(p_email)
            and u.deleted_at is null limit 1),
        jsonb_build_object('exists', false, 'password_set', false));
$$;

revoke all on function public.moicalendar_claim_login_attempt(text) from public, anon, authenticated;
revoke all on function public.moicalendar_classify_login_email(text) from public, anon, authenticated;
grant execute on function public.moicalendar_claim_login_attempt(text) to service_role;
grant execute on function public.moicalendar_classify_login_email(text) to service_role;
