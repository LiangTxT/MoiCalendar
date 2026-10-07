import webpush from "npm:web-push@3.6.7";
import rrule from "npm:rrule@2.8.1";
import { createReminderPlanner } from "../_shared/reminderPlanner.mjs";
import { dispatchPage } from "../_shared/reminderDispatch.mjs";
const plan=createReminderPlanner(rrule.RRule);
async function equalSecret(a:string,b:string) {
    const hash=(s:string)=>crypto.subtle.digest("SHA-256",new TextEncoder().encode(s));
    const [x,y]=await Promise.all([hash(a),hash(b)]);
    return new Uint8Array(x).reduce((d,v,i)=>d|(v^new Uint8Array(y)[i]),0)===0;
}
Deno.serve(async request=> {
    if (request.method!=="POST") return new Response(null,{status:405});
    const secret=Deno.env.get("MOICALENDAR_PUSH_SCHEDULER_SECRET")||"";
    if (secret.length<32) return new Response(null,{status:503});
    const provided=request.headers.get("x-moi-reminder-key")||"";
    if (provided.length>256 || !await equalSecret(provided,secret)) return new Response(null,{status:401});
    const base=Deno.env.get("SUPABASE_URL"),key=Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
    const pub=Deno.env.get("MOICALENDAR_VAPID_PUBLIC_KEY"),priv=Deno.env.get("MOICALENDAR_VAPID_PRIVATE_KEY"),subject=Deno.env.get("MOICALENDAR_VAPID_SUBJECT");
    if (!base || !key || !pub || !priv || !subject) return new Response(null,{status:503});
    const rpc=async (name:string,body:Record<string,unknown>)=> {
        const response=await fetch(`${base}/rest/v1/rpc/${name}`,{method:"POST",headers:{apikey:key,authorization:`Bearer ${key}`,"content-type":"application/json"},body:JSON.stringify(body),signal:AbortSignal.timeout(10000)});
        if (!response.ok) throw Error("Reminder storage unavailable");
        const text=await response.text(); return text ? JSON.parse(text) : null;
    };
    try {
        webpush.setVapidDetails(subject,pub,priv);
        const send=(s:any,p:string)=>webpush.sendNotification(s,p,{TTL:900,urgency:"normal",timeout:10000});
        await rpc("moicalendar_prune_push",{});
        let after:string|null=null;
        const now=Date.now(),totals:Record<string,number>={sent:0,failed:0,stale:0,invalid:0};
        for (let page=0;page<50;page++) {
            const rows=await rpc("moicalendar_reminder_events_page",{p_after_id:after,p_limit:100});
            const result=await dispatchPage(rows,{plan,rpc,send,now});
            for (const k of Object.keys(totals)) totals[k]+=result[k];
            if (rows.length<100) return Response.json(totals);
            after=rows.at(-1).event.id;
        }
        return Response.json({error:"Reminder scan capacity exceeded"},{status:503});
    } catch { return Response.json({error:"Reminder dispatch failed"},{status:503}); }
});
