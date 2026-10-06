import { createLoginHandler } from "./handler.mjs";

Deno.serve(createLoginHandler({ env: (name: string) => Deno.env.get(name) }));
