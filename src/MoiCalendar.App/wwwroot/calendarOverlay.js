window.moicalendarOverlay = {
    activate: host => {
        if (!host) {
            return;
        }

        const selector = 'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';
        const focusable = () => Array.from(host.querySelectorAll(selector)).filter(element => element.offsetParent !== null);
        const handler = event => {
            if (event.key !== "Tab") {
                return;
            }

            const elements = focusable();
            if (elements.length === 0) {
                event.preventDefault();
                return;
            }

            const first = elements[0];
            const last = elements[elements.length - 1];
            if (event.shiftKey && document.activeElement === first) {
                event.preventDefault();
                last.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first.focus();
            }
        };

        host._moicalendarOverlayKeydown = handler;
        host.addEventListener("keydown", handler);
        queueMicrotask(() => {
            const preferred = host.querySelector("[autofocus], [data-overlay-initial-focus]");
            if (preferred?.offsetParent !== null) {
                preferred.focus();
                return;
            }
            focusable()[0]?.focus();
        });
    },
    deactivate: host => {
        if (host?._moicalendarOverlayKeydown) {
            host.removeEventListener("keydown", host._moicalendarOverlayKeydown);
            delete host._moicalendarOverlayKeydown;
        }
    }
};
