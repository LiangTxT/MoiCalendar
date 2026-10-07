-- Run manually AFTER deployment, after creating these two secrets in Supabase Vault:
-- moi_push_dispatch_url = https://YOUR_PROJECT.supabase.co/functions/v1/dispatch-reminders
-- moi_push_scheduler_secret = same random secret as MOICALENDAR_PUSH_SCHEDULER_SECRET
-- Do not insert secret values into this file or source control.
do $$
begin
    if not exists(select 1 from vault.decrypted_secrets where name='moi_push_dispatch_url') or
       not exists(select 1 from vault.decrypted_secrets where name='moi_push_scheduler_secret') then
        raise exception 'Configure reminder Vault secrets before enabling the scheduler';
    end if;
    if exists(select 1 from cron.job where jobname='moicalendar-reminders') then
        perform cron.unschedule('moicalendar-reminders');
    end if;
    perform cron.schedule('moicalendar-reminders','* * * * *',$job$
        select net.http_post(
            url := (select decrypted_secret from vault.decrypted_secrets where name='moi_push_dispatch_url' limit 1),
            headers := jsonb_build_object('Content-Type','application/json','x-moi-reminder-key',
                (select decrypted_secret from vault.decrypted_secrets where name='moi_push_scheduler_secret' limit 1)),
            body := '{}'::jsonb, timeout_milliseconds := 55000);
    $job$);
end;
$$;
