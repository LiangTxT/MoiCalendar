import { validPushSubscription } from "./reminderPlanner.mjs";
export async function dispatchPage(rows,{plan,rpc,send,now}) {
    const totals={sent:0,failed:0,stale:0,invalid:0};
    for (const row of rows) {
        let reminders;
        try { reminders=plan(row.event,now-15*60000,now); } catch { totals.invalid++; continue; }
        for (const reminder of reminders) for (const target of row.subscriptions||[]) {
            if (!validPushSubscription(target.subscription)) { totals.invalid++; continue; }
            const lease=await rpc("moicalendar_claim_reminder",{p_owner_id:row.ownerId,p_device_id:target.deviceId,
                p_event_id:row.event.id,p_revision:row.revision,p_key:reminder.key,p_start:reminder.occurrenceStartUtc,p_due:reminder.dueUtc});
            if (!lease) { totals.stale++; continue; }
            let success=false;
            try { await send(target.subscription,JSON.stringify({...reminder,ownerId:row.ownerId})); success=true; totals.sent++; }
            catch (error) {
                totals.failed++;
                if ([404,410].includes(error?.statusCode)) await rpc("moicalendar_prune_push",{p_owner_id:row.ownerId,p_device_id:target.deviceId,p_endpoint:target.subscription.endpoint});
            } finally { await rpc("moicalendar_finish_reminder",{p_lease_id:lease,p_success:success}); }
        }
    }
    return totals;
}
