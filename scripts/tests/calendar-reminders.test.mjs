import test from "node:test";
import assert from "node:assert/strict";
import rrule from "rrule";
import {createReminderPlanner, instantAt, validPushSubscription} from "../../supabase/functions/_shared/reminderPlanner.mjs";
import {dispatchPage} from "../../supabase/functions/_shared/reminderDispatch.mjs";
const plan=createReminderPlanner(rrule.RRule);
const base={id:"11223344-5566-7788-9900-112233445566",title:"测试提醒",startUtc:"2026-10-06T09:00:00Z",timeZoneId:"UTC",isAllDay:false,
    reminderMinutesBeforeStart:15,reminderTimeZoneId:"Asia/Hong_Kong",updatedAtUtc:"2026-10-01T00:00:00Z"};
const run=(e,a,b)=>plan(e,Date.parse(a),Date.parse(b));
const subscription={endpoint:"https://fcm.googleapis.com/fcm/send/test",keys:{p256dh:"A".repeat(87),auth:"B".repeat(22)}};
test("timed key is identical to Core Unix-millisecond key",()=> {
    const result=run(base,"2026-10-06T08:44:59Z","2026-10-06T08:45:00Z");
    assert.equal(result[0].key,`${base.id}:${Date.parse(base.startUtc)}:15:${Date.parse("2026-10-06T08:45:00Z")}`);
    assert.equal(run(base,"2026-10-06T08:45:00Z","2026-10-06T08:46:00Z").length,0);
});
for (const [minute,due] of [[420,"2026-10-05T23:00:00Z"],[510,"2026-10-06T00:30:00Z"],[0,"2026-10-05T16:00:00Z"],[1439,"2026-10-06T15:59:00Z"]])
    test(`all-day custom time ${minute}`,()=> {
        const result=run({...base,isAllDay:true,startUtc:"2026-10-06T00:00:00Z",reminderMinutesBeforeStart:0,allDayReminderMinuteOfDay:minute},"2026-10-05T00:00:00Z","2026-10-07T00:00:00Z");
        assert.equal(result[0].dueUtc,new Date(due).toISOString()); assert.equal(result[0].date,"2026-10-06");
    });
test("all-day day-before keeps 07:00 across DST",()=> {
    const result=run({...base,isAllDay:true,startUtc:"2026-03-08T00:00:00Z",reminderMinutesBeforeStart:1440,reminderTimeZoneId:"America/New_York"},"2026-03-07T00:00:00Z","2026-03-08T00:00:00Z");
    assert.equal(result[0].dueUtc,"2026-03-07T12:00:00.000Z");
});
test("COUNT excludes deleted occurrence without replacement",()=> {
    const result=run({...base,recurrenceRule:"FREQ=DAILY;COUNT=3",excludedOccurrenceStartsUtc:["2026-10-07T09:00:00Z"]},"2026-10-05T00:00:00Z","2026-10-12T00:00:00Z");
    assert.deepEqual(result.map(x=>x.date),["2026-10-06","2026-10-08"]);
});
test("invalid DST starts don't consume COUNT; ambiguous starts choose earliest",()=> {
    const result=run({...base,startUtc:"2026-03-07T07:30:00Z",timeZoneId:"America/New_York",recurrenceRule:"FREQ=DAILY;COUNT=3"},"2026-03-07T00:00:00Z","2026-03-12T00:00:00Z");
    assert.deepEqual(result.map(x=>x.date),["2026-03-07","2026-03-09","2026-03-10"]);
    assert.equal(instantAt(Date.parse("2026-11-01T01:30:00Z"),"America/New_York"),Date.parse("2026-11-01T05:30:00Z"));
});
test("weekly WKST interval matches week boundaries",()=> {
    const result=run({...base,startUtc:"2026-10-04T09:00:00Z",recurrenceRule:"FREQ=WEEKLY;INTERVAL=2;BYDAY=SU,MO;WKST=SU;COUNT=4"},"2026-10-01T00:00:00Z","2026-10-30T00:00:00Z");
    assert.deepEqual(result.map(x=>x.date),["2026-10-04","2026-10-05","2026-10-18","2026-10-19"]);
});
test("monthly missing day and leap-year yearly recurrence",()=> {
    const monthly=run({...base,startUtc:"2026-01-31T09:00:00Z",recurrenceRule:"FREQ=MONTHLY;COUNT=3"},"2026-01-01T00:00:00Z","2026-06-01T00:00:00Z");
    assert.deepEqual(monthly.map(x=>x.date),["2026-01-31","2026-03-31","2026-05-31"]);
    const yearly=run({...base,startUtc:"2024-02-29T09:00:00Z",recurrenceRule:"FREQ=YEARLY;COUNT=2"},"2024-01-01T00:00:00Z","2029-01-01T00:00:00Z");
    assert.deepEqual(yearly.map(x=>x.date),["2024-02-29","2028-02-29"]);
});
test("inclusive UNTIL and tombstones",()=> {
    for (const until of ["20261007","20261007T090000Z"]) assert.equal(run({...base,recurrenceRule:`FREQ=DAILY;UNTIL=${until}`},"2026-10-05T00:00:00Z","2026-10-09T00:00:00Z").length,2);
    assert.deepEqual(run({...base,deletedAtUtc:base.updatedAtUtc},"2026-10-05T00:00:00Z","2026-10-09T00:00:00Z"),[]);
});
test("reject unsupported recurrence and custom-time values",()=> {
    for (const recurrenceRule of ["FREQ=DAILY;COUNT=1;UNTIL=20261009","FREQ=DAILY;BYDAY=MO","FREQ=DAILY;INTERVAL=0","FREQ=HOURLY"]) assert.throws(()=>run({...base,recurrenceRule},"2026-10-01T00:00:00Z","2026-10-09T00:00:00Z"));
    assert.throws(()=>run({...base,allDayReminderMinuteOfDay:1440},"2026-10-01T00:00:00Z","2026-10-09T00:00:00Z"));
});
test("push endpoints cannot reach arbitrary hosts, credentials or ports",()=> {
    assert.ok(validPushSubscription(subscription));
    for (const endpoint of ["https://localhost/test","https://127.0.0.1/test","https://fcm.googleapis.com.evil.test/x","https://user@fcm.googleapis.com/x","http://fcm.googleapis.com/x","https://fcm.googleapis.com:444/x","https://fcm.googleapis.com/x#bad"]) assert.equal(validPushSubscription({...subscription,endpoint}),false);
});
for (const status of [0,410,503]) test(`delivery completion and expired subscription ${status}`,async()=> {
    const calls=[], sent=[];
    const totals=await dispatchPage([{ownerId:"owner",revision:1,event:base,subscriptions:[{deviceId:"device",subscription}]}],{
        plan,rpc:async(name,args)=> {calls.push([name,args]);return name==="moicalendar_claim_reminder" ? "lease" : null;},
        send:async(s,p)=> {sent.push(JSON.parse(p));if(status)throw {statusCode:status};},now:Date.parse("2026-10-06T08:45:00Z")});
    assert.equal(totals.sent,status ? 0 : 1); assert.equal(totals.failed,status ? 1 : 0);
    assert.equal(calls.at(-1)[1].p_success,status===0);
    assert.equal(calls.some(([name])=>name==="moicalendar_prune_push"),status===410);
    assert.equal(sent[0].ownerId,"owner"); assert.equal(sent[0].description,undefined);
});
test("already claimed and invalid subscriptions are not sent",async()=> {
    let sent=0;
    const result=await dispatchPage([{ownerId:"owner",revision:1,event:base,subscriptions:[{deviceId:"device",subscription},{subscription:{endpoint:"https://localhost/x"}}]}],{plan,rpc:async()=>null,send:async()=>sent++,now:Date.parse("2026-10-06T08:45:00Z")});
    assert.equal(sent,0);assert.equal(result.stale,1);assert.equal(result.invalid,1);
});
