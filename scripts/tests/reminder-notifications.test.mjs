import test from "node:test";
import assert from "node:assert/strict";
import {readFileSync} from "node:fs";
import vm from "node:vm";
const source=readFileSync(new URL("../../src/MoiCalendar.Storage/wwwroot/reminderNotifications.js",import.meta.url),"utf8");
const data={eventId:"11223344-5566-7788-9900-112233445566",key:"test-key",title:"测试提醒",ownerId:"owner",dueUtc:new Date().toISOString(),
    occurrenceStartUtc:"2026-10-06T00:00:00Z",seriesStartUtc:"2026-10-06T00:00:00Z",date:"2026-10-06",minutes:0,allDayReminderMinuteOfDay:420,
    reminderTimeZoneId:"Asia/Hong_Kong",updatedAtUtc:"2026-10-01T00:00:00Z",isAllDay:true};
function harness(owner="owner",current=null,fail=false) {
    const settings=new Map([["reminders-push-owner",{key:"reminders-push-owner",value:owner}]]);
    const sent=[];
    function request(value) {const r={result:value}; queueMicrotask(()=>r.onsuccess?.()); return r;}
    const db={objectStoreNames:{contains:()=>true},close(){},transaction() {
        const tx={objectStore(name) {return name==="events" ? {get:()=>request(current)} : {
            get:key=>request(settings.get(key)),getAll:()=>request([...settings.values()]),put:value=>settings.set(value.key,value),delete:key=>settings.delete(key)
        };}};
        setImmediate(()=>tx.oncomplete?.());return tx;
    }};
    const context=vm.createContext({indexedDB:{open:()=>request(db)},Date,Intl});
    vm.runInContext(source,context);
    return {helper:context.moicalendarReminderNotifications,settings,sent,registration:{showNotification:async(title,options)=>{if(fail)throw Error("blocked");sent.push({title,options});}}};
}
test("same owner push is shown only once and uses calendar date route",async()=> {
    const h=harness();await h.helper.showPush(data,h.registration);await h.helper.showPush(data,h.registration);
    assert.equal(h.sent.length,1); assert.equal(h.sent[0].options.data.url,"?view=day&date=2026-10-06");
    assert.ok(h.settings.has("reminder-delivered:test-key"));
});
for (const [name,owner,current] of [
    ["logged out",null,null],["other account","other",null],
    ["deleted event","owner",{deletedAtUtc:"2026-10-02T00:00:00Z"}],
    ["excluded occurrence","owner",{excludedOccurrenceStartsUtc:[data.occurrenceStartUtc]}],
    ["locally changed custom time","owner",{updatedAtUtc:"2026-10-02T00:00:00Z",reminderMinutesBeforeStart:0,startUtc:data.seriesStartUtc,reminderTimeZoneId:data.reminderTimeZoneId,allDayReminderMinuteOfDay:480}]
]) test(`push suppressed for ${name}`,async()=> {
    const h=harness(owner,current);await h.helper.showPush(data,h.registration); assert.equal(h.sent.length,0);
});
test("newer local equivalent timestamps do not accidentally suppress push",async()=> {
    const h=harness("owner",{updatedAtUtc:"2026-10-02T00:00:00Z",reminderMinutesBeforeStart:0,startUtc:"2026-10-06T00:00:00.000+00:00",reminderTimeZoneId:data.reminderTimeZoneId,allDayReminderMinuteOfDay:420});
    await h.helper.showPush(data,h.registration); assert.equal(h.sent.length,1);
});
test("failed notification releases dedup claim",async()=> {
    const h=harness("owner",null,true);await assert.rejects(()=>h.helper.showPush(data,h.registration));
    assert.equal(h.settings.has("reminder-delivered:test-key"),false);
});
test("timed notification opens the device-local day, not the event-zone date",()=> {
    const h=harness(); const start=new Date(data.occurrenceStartUtc);
    const local=`${start.getFullYear()}-${String(start.getMonth()+1).padStart(2,"0")}-${String(start.getDate()).padStart(2,"0")}`;
    assert.equal(h.helper.url({...data,isAllDay:false,date:"1970-01-01"}),`?view=day&date=${local}`);
});
test("stale/future/malformed notification is rejected",async()=> {
    const h=harness();
    for (const patch of [{dueUtc:new Date(Date.now()-16*60000).toISOString()},{dueUtc:new Date(Date.now()+60000).toISOString()},{title:"x".repeat(201)},{eventId:"invalid"}])
        await h.helper.showPush({...data,...patch},h.registration);
    assert.equal(h.sent.length,0);
});
const configSource=readFileSync(new URL("../../supabase/functions/push-configuration/index.ts",import.meta.url),"utf8");
async function configuration(request,missing=false,auth=200) {
    let handler,calls=0;
    const context=vm.createContext({Request,Response,AbortSignal,fetch:async()=>{calls++;return new Response(null,{status:auth});},
        Deno:{serve:fn=>handler=fn,env:{get:key=>missing ? undefined : ({MOICALENDAR_ALLOWED_ORIGINS:"https://calendar.test",SUPABASE_URL:"https://backend.test",SUPABASE_ANON_KEY:"anon",MOICALENDAR_VAPID_PUBLIC_KEY:"public",MOICALENDAR_VAPID_PRIVATE_KEY:"private",MOICALENDAR_PUSH_SCHEDULER_SECRET:"secret"})[key]}}});
    vm.runInContext(configSource,context);return {response:await handler(request),calls};
}
test("configuration requires origin and authenticated account; never returns private keys",async()=> {
    const headers={origin:"https://calendar.test",authorization:"Bearer token"};
    const good=await configuration(new Request("https://backend.test",{headers}));
    assert.deepEqual(await good.response.json(),{publicKey:"public"});assert.equal(good.calls,1);
    const badOrigin=await configuration(new Request("https://backend.test",{headers:{...headers,origin:"https://evil.test"}}));
    assert.equal(badOrigin.response.status,403);assert.equal(badOrigin.calls,0);
    const missing=await configuration(new Request("https://backend.test",{headers:{origin:headers.origin}}));assert.equal(missing.response.status,401);
    const failedAuth=await configuration(new Request("https://backend.test",{headers}),false,401);assert.equal(failedAuth.response.status,401);
});
test("SQL worker functions are service-role only; subscriptions are private and device-bound",()=> {
    const sql=readFileSync(new URL("../../supabase/migrations/20261006030000_push_reminders.sql",import.meta.url),"utf8");
    for(const table of ["reminder_push_subscriptions","reminder_push_deliveries"]) assert.ok(sql.includes(`alter table public.${table} enable row level security`));
    for(const signature of ["moicalendar_reminder_events_page(uuid,integer)","moicalendar_claim_reminder(uuid,uuid,uuid,bigint,text,timestamptz,timestamptz)","moicalendar_finish_reminder(uuid,boolean)","moicalendar_prune_push(uuid,uuid,text)"]) {
        assert.ok(sql.includes(`revoke all on function public.${signature} from public, anon, authenticated`));
        assert.ok(sql.includes(`grant execute on function public.${signature} to service_role`));
    }
    assert.match(sql,/moicalendar_require_active_device\(p_device_id, v_owner\)/);
    assert.match(sql,/e.server_revision=p_revision/);assert.match(sql,/attempts<5/);assert.match(sql,/lease_until<now\(\)/);
    const dispatch=readFileSync(new URL("../../supabase/functions/dispatch-reminders/index.ts",import.meta.url),"utf8");
    assert.ok(dispatch.indexOf("!await equalSecret")<dispatch.indexOf("const rpc="));
    assert.match(dispatch,/TTL:900/);assert.doesNotMatch(dispatch,/console\.log/);
});
test("custom reminder time uses strongly typed immediate-input binding",()=> {
    const picker=readFileSync(new URL("../../src/MoiCalendar.App/Components/EventReminderPicker.razor",import.meta.url),"utf8");
    assert.match(picker,/<input[^>]*type="time"[^>]*@bind="Draft.AllDayReminderTime"/);
    assert.match(picker,/@bind:event="oninput"/);
});
