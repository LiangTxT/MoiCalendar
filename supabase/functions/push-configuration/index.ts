Deno.serve(async request=> {
    const origin=request.headers.get("origin")||"";
    const allowed=(Deno.env.get("MOICALENDAR_ALLOWED_ORIGINS")||"").split(",").map(s=>s.trim());
    if (!origin || !allowed.includes(origin)) return new Response(null,{status:403});
    const headers={"Access-Control-Allow-Origin":origin,"Access-Control-Allow-Methods":"GET, OPTIONS",
        "Access-Control-Allow-Headers":"authorization, apikey, content-type","Vary":"Origin","Cache-Control":"no-store"};
    if (request.method==="OPTIONS") return new Response(null,{status:204,headers});
    if (request.method!=="GET") return new Response(null,{status:405,headers});
    const token=request.headers.get("authorization")||"";
    if (!token.startsWith("Bearer ")) return new Response(null,{status:401,headers});
    const base=Deno.env.get("SUPABASE_URL"),key=Deno.env.get("SUPABASE_ANON_KEY"),publicKey=Deno.env.get("MOICALENDAR_VAPID_PUBLIC_KEY");
    if (!base || !key || !publicKey || !Deno.env.get("MOICALENDAR_VAPID_PRIVATE_KEY") || !Deno.env.get("MOICALENDAR_PUSH_SCHEDULER_SECRET")) return new Response(null,{status:503,headers});
    try {
        const user=await fetch(`${base}/auth/v1/user`,{headers:{apikey:key,authorization:token},signal:AbortSignal.timeout(10000)});
        if (!user.ok) return new Response(null,{status:401,headers});
        return Response.json({publicKey},{headers});
    } catch { return new Response(null,{status:503,headers}); }
});
