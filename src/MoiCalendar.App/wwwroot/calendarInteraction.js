window.moicalendarInteraction = {
    getElementBounds: element => {
        const rect = element.getBoundingClientRect();
        return {
            left: rect.left,
            top: rect.top,
            width: rect.width,
            height: rect.height,
            scrollLeft: element.scrollLeft ?? 0,
            scrollTop: element.scrollTop ?? 0
        };
    },
    capturePointer: (element, pointerId) => {
        if (element?.setPointerCapture) {
            element.setPointerCapture(pointerId);
        }
    },
    releasePointer: (element, pointerId) => {
        if (element?.hasPointerCapture?.(pointerId)) {
            element.releasePointerCapture(pointerId);
        }
    }
};
