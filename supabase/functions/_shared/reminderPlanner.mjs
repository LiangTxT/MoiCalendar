const day=86400000, formats=new Map();
function format(zone) {
    if (!formats.has(zone)) formats.set(zone,new Intl.DateTimeFormat("en-CA",{timeZone:zone,year:"numeric",month:"2-digit",day:"2-digit",hour:"2-digit",minute:"2-digit",second:"2-digit",hourCycle:"h23"}));
    return formats.get(zone);
}
export function wall(time,zone) {
    const p=Object.fromEntries(format(zone).formatToParts(new Date(time)).map(x=>[x.type,x.value]));
    return Date.UTC(+p.year,+p.month-1,+p.day,+p.hour,+p.minute,+p.second)+(time%1000+1000)%1000;
}
export function instantAt(local,zone) {
    const offsets=new Set([-2,-1,0,1,2].map(d=>wall(local+d*day,zone)-(local+d*day)));
    const matches=[...offsets].map(o=>local-o).filter(t=>wall(t,zone)===local);
    return matches.length ? Math.min(...matches) : null;
}
export function createReminderPlanner(RRule) {
    return (e,from,to)=> {
        if (e.deletedAtUtc || e.reminderMinutesBeforeStart==null) return [];
        const minutes=e.reminderMinutesBeforeStart, minuteOfDay=e.allDayReminderMinuteOfDay??420;
        if (![0,5,15,30,60,1440].includes(minutes) || !Number.isInteger(minuteOfDay) || minuteOfDay<0 || minuteOfDay>1439) throw Error("Invalid reminder time");
        let zone=e.timeZoneId;
        try { format(zone); } catch { zone=e.reminderTimeZoneId; format(zone); }
        const start=Date.parse(e.startUtc),localStart=wall(start,zone);
        const lower=wall(from-2*day,zone),upper=wall(to+2*day,zone);
        const excluded=new Set((e.excludedOccurrenceStartsUtc||[]).map(x=>Date.parse(x)));
        let count=null,untilDate=null,untilUtc=null,rule=null,number=0,iterations=0;
        if (e.recurrenceRule) {
            const parts=new Map();
            for (const part of e.recurrenceRule.trim().replace(/^RRULE:/i,"").toUpperCase().split(";")) {
                const [k,v,...rest]=part.split("=");
                if (!v || rest.length || parts.has(k) || !["FREQ","INTERVAL","COUNT","UNTIL","BYDAY","WKST"].includes(k)) throw Error("Unsupported recurrence");
                parts.set(k,v);
            }
            if (!["DAILY","WEEKLY","MONTHLY","YEARLY"].includes(parts.get("FREQ"))) throw Error("Unsupported frequency");
            if (parts.has("BYDAY") && (parts.get("FREQ")!=="WEEKLY" || !/^(MO|TU|WE|TH|FR|SA|SU)(,(MO|TU|WE|TH|FR|SA|SU))*$/.test(parts.get("BYDAY")))) throw Error("Unsupported BYDAY");
            for (const k of ["COUNT","INTERVAL"]) if (parts.has(k) && (!/^\d+$/.test(parts.get(k)) || +parts.get(k)<1 || +parts.get(k)>2147483647)) throw Error("Invalid integer");
            if (parts.has("COUNT") && parts.has("UNTIL")) throw Error("Conflicting ends");
            if (parts.has("COUNT")) { count=+parts.get("COUNT"); parts.delete("COUNT"); }
            if (parts.has("UNTIL")) {
                const u=parts.get("UNTIL");
                if (/^\d{8}$/.test(u)) untilDate=`${u.slice(0,4)}-${u.slice(4,6)}-${u.slice(6,8)}`;
                else if (/^\d{8}T\d{6}Z$/.test(u)) untilUtc=Date.parse(`${u.slice(0,4)}-${u.slice(4,6)}-${u.slice(6,8)}T${u.slice(9,11)}:${u.slice(11,13)}:${u.slice(13,15)}Z`);
                else throw Error("Invalid UNTIL");
                parts.delete("UNTIL");
            }
            rule=new RRule({...RRule.parseString([...parts].map(([k,v])=>`${k}=${v}`).join(";")),dtstart:new Date(localStart)},true);
        }
        const result=[];
        function visit(local) {
            if (++iterations>1000000) throw Error("Recurrence budget exceeded");
            if (local>=upper) return false;
            const occurrence=rule ? instantAt(local,zone) : start;
            if (occurrence==null) return true;
            const date=new Date(local).toISOString().slice(0,10);
            if (untilDate && date>untilDate || untilUtc!=null && occurrence>untilUtc) return false;
            if (count!=null && ++number>count) return false;
            if (local<lower || excluded.has(occurrence)) return true;
            const due=e.isAllDay ? instantAt(Date.parse(date+"T00:00:00Z")+(minuteOfDay-minutes)*60000,e.reminderTimeZoneId||"UTC") : occurrence-minutes*60000;
            if (due!=null && due>from && due<=to) result.push({eventId:e.id.toLowerCase(),key:`${e.id.toLowerCase()}:${occurrence}:${minutes}:${due}`,title:e.title,
                occurrenceStartUtc:new Date(occurrence).toISOString(),dueUtc:new Date(due).toISOString(),isAllDay:e.isAllDay,date,
                minutes,allDayReminderMinuteOfDay:minuteOfDay,seriesStartUtc:e.startUtc,reminderTimeZoneId:e.reminderTimeZoneId,updatedAtUtc:e.updatedAtUtc});
            return true;
        }
        if (!rule) visit(localStart);
        else if (count!=null) rule.all(d=>visit(d.getTime()));
        else rule.between(new Date(lower),new Date(upper),true).forEach(d=>visit(d.getTime()));
        return result;
    };
}
export function validPushSubscription(s) {
    if (!s || typeof s.endpoint!=="string" || s.endpoint.length>4096) return false;
    try {
        const u=new URL(s.endpoint);
        return u.protocol==="https:" && !u.username && !u.password && !u.port && !u.hash &&
            /^(fcm\.googleapis\.com|updates\.push\.services\.mozilla\.com|web\.push\.apple\.com|[a-z0-9-]+\.(notify|wns)\.windows\.com)$/.test(u.hostname) &&
            /^[A-Za-z0-9_-]{87}=?$/.test(s.keys?.p256dh) && /^[A-Za-z0-9_-]{22}={0,2}$/.test(s.keys?.auth);
    } catch { return false; }
}
