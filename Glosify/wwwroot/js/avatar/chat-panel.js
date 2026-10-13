const sideInset = area => area.width <= 720 ? 44 : 12;
const clamp = (value, min, max) => Math.min(Math.max(value, min), max);

// Keep the entire panel reachable, including at the bottom of the scene.
export function constrainChat(rect, area, minimized = false) {
    const margin = 12;
    const side = sideInset(area);
    const maxWidth = Math.max(0, area.width - side * 2);
    const maxHeight = Math.max(0, area.height - margin * 2);
    const width = clamp(rect.width, Math.min(240, maxWidth), maxWidth);
    const height = clamp(rect.height, Math.min(minimized ? 50 : 200, maxHeight), maxHeight);
    return {
        x: clamp(rect.x, side, Math.max(side, area.width - width - side)),
        y: clamp(rect.y, margin, Math.max(margin, area.height - height - margin)),
        width, height,
    };
}

export function setupFloatingChat({ panel, stage, move, resize, toggle, Observer = ResizeObserver }) {
    const events = new AbortController();
    const on = (element, name, action) => element.addEventListener(name, action, { signal: events.signal });
    let rect, gesture;
    let minimized = true, expandedHeight = 390;
    const area = () => ({ width: stage.clientWidth, height: stage.clientHeight });
    function apply(next) {
        rect = constrainChat(next, area(), minimized);
        Object.assign(panel.style, { left: `${rect.x}px`, top: `${rect.y}px`, right: 'auto',
            width: `${rect.width}px`, height: `${rect.height}px` });
    }
    function restore() {
        gesture = null;
        const size = area();
        apply({ x: size.width - 344, y: size.height - 62, width: 320, height: 50 });
    }
    function renderState() {
        panel.classList.toggle('is-minimized', minimized);
        toggle.setAttribute('aria-expanded', String(!minimized));
        toggle.setAttribute('aria-label', minimized ? 'Expand conversation chat' : 'Minimize conversation chat');
        toggle.title = minimized ? 'Expand conversation chat' : 'Minimize conversation chat';
        toggle.textContent = minimized ? '+' : '−';
        move.setAttribute('aria-label', minimized ? 'Expand conversation chat' : 'Move conversation chat');
        move.title = minimized ? 'Expand conversation chat' : 'Drag to move · Arrow keys to move';
    }
    function toggleChat() {
        gesture = null;
        const bottom = rect.y + rect.height;
        if (!minimized) expandedHeight = rect.height;
        minimized = !minimized;
        const height = minimized ? 50 : expandedHeight;
        renderState();
        apply({ ...rect, y: bottom - height, height });
    }
    on(toggle, 'click', event => { event.stopPropagation(); toggleChat(); });
    on(panel, 'click', () => { if (minimized) toggleChat(); });
    for (const [handle, resizing] of [[move, false], [resize, true]]) {
        on(handle, 'pointerdown', event => {
            if (!event.isPrimary || event.button !== 0 || gesture || minimized) return;
            event.preventDefault();
            handle.focus();
            handle.setPointerCapture(event.pointerId);
            gesture = { id: event.pointerId, x: event.clientX, y: event.clientY, rect: { ...rect } };
        });
        on(handle, 'pointermove', event => {
            if (!gesture || event.pointerId !== gesture.id) return;
            const dx = event.clientX - gesture.x, dy = event.clientY - gesture.y;
            const start = gesture.rect;
            if (resizing) {
                // Resizing never moves the opposite corner.
                const size = area();
                apply({ ...start, width: Math.min(start.width + dx, size.width - start.x - sideInset(size)),
                    height: Math.min(start.height + dy, size.height - start.y - 12) });
            } else apply({ ...start, x: start.x + dx, y: start.y + dy });
        });
        for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) {
            on(handle, name, event => {
                if (gesture?.id !== event.pointerId) return;
                gesture = null;
                if (handle.hasPointerCapture(event.pointerId)) handle.releasePointerCapture(event.pointerId);
            });
        }
        on(handle, 'keydown', event => {
            const direction = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key];
            if (!direction || minimized) return;
            event.preventDefault(); event.stopPropagation();
            const step = event.shiftKey ? 32 : 12, size = area();
            if (resizing) apply({ ...rect,
                width: Math.min(rect.width + direction[0] * step, size.width - rect.x - sideInset(size)),
                height: Math.min(rect.height + direction[1] * step, size.height - rect.y - 12) });
            else apply({ ...rect, x: rect.x + direction[0] * step, y: rect.y + direction[1] * step });
        });
    }
    renderState();
    restore();
    const observer = new Observer(() => {
        gesture = null;
        apply(minimized ? { ...rect, y: area().height - rect.height - 12 } : rect);
    });
    observer.observe(stage);
    return { destroy() { events.abort(); observer.disconnect(); } };
}
