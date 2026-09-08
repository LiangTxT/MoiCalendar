const heartbeatIntervalMilliseconds = 25000;
const maximumReconnectDelayMilliseconds = 30000;

export function createSupabaseRealtimeNotifier(dotNetReference, webSocketUrl, publicKey, accountId) {
    let active = true;
    let socket = null;
    let reconnectTimer = null;
    let heartbeatTimer = null;
    let reconnectAttempt = 0;
    let reference = 0;
    let joinReference = null;
    let joined = false;
    let hasConnected = false;
    let generation = 0;
    let refreshingToken = false;

    const setState = async state => {
        if (!active) {
            return;
        }
        try {
            await dotNetReference.invokeMethodAsync("OnRealtimeConnectionStateChangedAsync", state);
        } catch {
            // The .NET scope may already be disposing; transport failure is non-fatal.
        }
    };

    const wakeUp = async reason => {
        if (!active) {
            return;
        }
        try {
            // Deliberately pass only a reason. Database payloads never cross this boundary.
            await dotNetReference.invokeMethodAsync("OnRealtimeWakeUpAsync", reason);
        } catch {
            // A wake-up is best effort. Incremental sync recovers on the next opportunity.
        }
    };

    const nextReference = () => String(++reference);

    const send = message => {
        if (socket?.readyState === WebSocket.OPEN) {
            socket.send(JSON.stringify(message));
        }
    };

    const sendAccessToken = async currentGeneration => {
        if (!active || currentGeneration !== generation || refreshingToken || !joined) {
            return;
        }
        refreshingToken = true;
        try {
            const token = await dotNetReference.invokeMethodAsync("GetRealtimeAccessTokenAsync");
            if (!active || currentGeneration !== generation || !joined) {
                return;
            }
            send({
                topic: channelTopic,
                event: "access_token",
                payload: { access_token: token },
                ref: nextReference(),
                join_ref: joinReference
            });
        } catch {
            await setState("Unavailable");
        } finally {
            refreshingToken = false;
        }
    };

    const clearTimers = () => {
        if (reconnectTimer !== null) {
            clearTimeout(reconnectTimer);
            reconnectTimer = null;
        }
        if (heartbeatTimer !== null) {
            clearInterval(heartbeatTimer);
            heartbeatTimer = null;
        }
    };

    const scheduleReconnect = currentGeneration => {
        if (!active || currentGeneration !== generation || reconnectTimer !== null) {
            return;
        }
        joined = false;
        const exponential = Math.min(
            1000 * (2 ** Math.min(reconnectAttempt, 5)),
            maximumReconnectDelayMilliseconds);
        const jitter = Math.floor(Math.random() * Math.min(1000, exponential / 4));
        reconnectAttempt++;
        void setState("Reconnecting");
        reconnectTimer = setTimeout(() => {
            reconnectTimer = null;
            void connect();
        }, exponential + jitter);
    };

    const channelTopic = `realtime:moicalendar-sync-state-${accountId}`;

    const markSubscriptionReady = currentGeneration => {
        if (!active || currentGeneration !== generation || joined) {
            return;
        }
        joined = true;
        reconnectAttempt = 0;
        void setState("Connected");
        void wakeUp(hasConnected ? "Reconnected" : "InitialConnection");
        hasConnected = true;
    };

    const connect = async () => {
        if (!active) {
            return;
        }

        const currentGeneration = ++generation;
        clearTimers();
        joined = false;
        await setState(hasConnected ? "Reconnecting" : "Connecting");

        let token;
        try {
            token = await dotNetReference.invokeMethodAsync("GetRealtimeAccessTokenAsync");
        } catch {
            await setState("Unavailable");
            scheduleReconnect(currentGeneration);
            return;
        }
        if (!active || currentGeneration !== generation) {
            return;
        }

        const previousSocket = socket;
        socket = null;
        if (previousSocket && previousSocket.readyState < WebSocket.CLOSING) {
            previousSocket.close(1000, "subscription replaced");
        }
        const endpoint = new URL(webSocketUrl);
        endpoint.searchParams.set("apikey", publicKey);
        endpoint.searchParams.set("vsn", "1.0.0");
        const currentSocket = new WebSocket(endpoint.toString());
        socket = currentSocket;

        currentSocket.onopen = () => {
            if (!active || currentGeneration !== generation) {
                currentSocket.close();
                return;
            }
            joinReference = nextReference();
            send({
                topic: channelTopic,
                event: "phx_join",
                payload: {
                    config: {
                        broadcast: { ack: false, self: false },
                        presence: { enabled: false },
                        postgres_changes: [{
                            event: "UPDATE",
                            schema: "public",
                            table: "sync_state",
                            filter: `owner_id=eq.${accountId}`
                        }],
                        private: false
                    },
                    access_token: token
                },
                ref: joinReference,
                join_ref: joinReference
            });
        };

        currentSocket.onmessage = event => {
            if (!active || currentGeneration !== generation) {
                return;
            }
            let message;
            try {
                message = JSON.parse(event.data);
            } catch {
                return;
            }

            if (message.event === "phx_reply" &&
                message.ref === joinReference &&
                message.payload?.status === "ok") {
                heartbeatTimer = setInterval(() => {
                    send({
                        topic: "phoenix",
                        event: "heartbeat",
                        payload: {},
                        ref: nextReference(),
                        join_ref: null
                    });
                    void sendAccessToken(currentGeneration);
                }, heartbeatIntervalMilliseconds);
                return;
            }

            if (message.event === "phx_reply" &&
                message.ref === joinReference &&
                message.payload?.status !== "ok") {
                void setState("Unavailable");
                currentSocket.close();
                return;
            }

            if (message.event === "system" &&
                message.payload?.extension === "postgres_changes") {
                if (message.payload?.status === "ok") {
                    markSubscriptionReady(currentGeneration);
                } else {
                    void setState("Unavailable");
                }
                return;
            }

            if (message.event === "postgres_changes") {
                markSubscriptionReady(currentGeneration);
                void wakeUp("ChangeNotification");
                return;
            }

            if (message.event === "phx_error") {
                currentSocket.close();
            } else if (message.event === "phx_close") {
                currentSocket.close();
            }
        };

        currentSocket.onerror = () => {
            currentSocket.close();
        };

        currentSocket.onclose = () => {
            if (heartbeatTimer !== null) {
                clearInterval(heartbeatTimer);
                heartbeatTimer = null;
            }
            if (socket === currentSocket) {
                socket = null;
            }
            scheduleReconnect(currentGeneration);
        };
    };

    const resume = () => {
        if (!active || document.hidden) {
            return;
        }
        if (socket?.readyState !== WebSocket.OPEN || !joined) {
            if (reconnectTimer !== null) {
                clearTimeout(reconnectTimer);
                reconnectTimer = null;
            }
            void connect();
        }
        void wakeUp("ForegroundResume");
    };

    document.addEventListener("visibilitychange", resume);
    window.addEventListener("online", resume);
    void connect();

    return {
        async stop() {
            if (!active) {
                return;
            }
            active = false;
            generation++;
            clearTimers();
            document.removeEventListener("visibilitychange", resume);
            window.removeEventListener("online", resume);
            const currentSocket = socket;
            socket = null;
            joined = false;
            if (currentSocket && currentSocket.readyState < WebSocket.CLOSING) {
                currentSocket.close(1000, "subscription stopped");
            }
        }
    };
}
