interface ChatRect { x: number; y: number; width: number; height: number; }

const sideInset = (area: { width: number }) => area.width <= 720 ? 44 : 12;
const clamp = (value: number, min: number, max: number) => Math.min(Math.max(value, min), max);

// Keep the entire panel reachable, including at the bottom of the scene.
export function constrainChat(rect: ChatRect, area: { width: number; height: number }, minimized = false, controls: ChatRect | null = null) {
    const margin = 12;
    const side = sideInset(area);
    const maxWidth = Math.max(0, area.width - side * 2);
    const maxHeight = Math.max(0, area.height - margin * 2);
    const width = clamp(rect.width, Math.min(240, maxWidth), maxWidth);
    const height = clamp(rect.height, Math.min(minimized ? 50 : 200, maxHeight), maxHeight);
    const result = {
        x: clamp(rect.x, side, Math.max(side, area.width - width - side)),
        y: clamp(rect.y, margin, Math.max(margin, area.height - height - margin)),
        width, height,
    };
    if (!minimized && controls && result.x < controls.x + controls.width + margin
        && result.x + width > controls.x - margin && result.y + height > controls.y - margin
        && result.y < controls.y + controls.height + margin) {
        result.height = Math.min(height, Math.max(0, controls.y - margin * 2));
        result.y = Math.max(margin, controls.y - margin - result.height);
    }
    return result;
}

export function setupFloatingChat({ panel, stage, move, resize, toggle, controls, unread, Observer = ResizeObserver }: {
    panel: HTMLElement; stage: HTMLElement; move: HTMLElement; resize: HTMLElement;
    toggle: HTMLElement; controls?: HTMLElement | null; unread?: HTMLElement | null;
    Observer?: typeof ResizeObserver;
}) {
    const events = new AbortController();
    const on = <K extends keyof HTMLElementEventMap>(element: HTMLElement, name: K, action: (event: HTMLElementEventMap[K]) => void) => element.addEventListener(name, action, { signal: events.signal });
    let rect!: ChatRect;
    let gesture: { id: number; x: number; y: number; rect: ChatRect } | null;
    let minimized = true, expandedHeight = 390;
    const area = () => ({ width: stage.clientWidth, height: stage.clientHeight });
    const voiceBounds = () => {
        const button = controls?.querySelector('button:not([hidden])');
        if (!button) return null;
        const bounds = button.getBoundingClientRect(), scene = stage.getBoundingClientRect();
        return { x: bounds.left - scene.left, y: bounds.top - scene.top, width: bounds.width, height: bounds.height };
    };
    function apply(next: ChatRect) {
        rect = constrainChat(next, area(), minimized, voiceBounds());
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
        if (!minimized && unread) unread.hidden = true;
        const height = minimized ? 50 : expandedHeight;
        renderState();
        apply({ ...rect, y: minimized ? area().height - height - 12 : bottom - height, height });
    }
    on(toggle, 'click', event => { event.stopPropagation(); toggleChat(); });
    on(panel, 'click', () => { if (minimized) toggleChat(); });
    for (const [handle, resizing] of [[move, false], [resize, true]] as const) {
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
        for (const name of ['pointerup', 'pointercancel', 'lostpointercapture'] as const) {
            on(handle, name, event => {
                if (gesture?.id !== event.pointerId) return;
                gesture = null;
                if (handle.hasPointerCapture(event.pointerId)) handle.releasePointerCapture(event.pointerId);
            });
        }
        on(handle, 'keydown', event => {
            const direction = ({ ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] } as Record<string, [number, number]>)[event.key];
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
    if (controls) observer.observe(controls);
    return {
        markUnread() { if (minimized && unread) unread.hidden = false; },
        clearUnread() { if (unread) unread.hidden = true; },
        destroy() { events.abort(); observer.disconnect(); }
    };
}
