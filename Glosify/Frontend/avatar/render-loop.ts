// Keep visible portraits gently alive at a lower idle rate; speech needs more frames.
// Reduced motion settles expressions before stopping non-speaking animation.
export function createRenderLoop(render: (seconds: number) => void, {
    request = callback => requestAnimationFrame(callback),
    cancel = id => cancelAnimationFrame(id),
    now = () => performance.now(),
    onFrame = () => {}
}: { request?: (callback: FrameRequestCallback) => number; cancel?: (id: number) => void; now?: () => number; onFrame?: (sample: { time: number; renderMs: number; frameGap: number }) => void } = {}) {
    let frame: number | null = null;
    let disposed = false, visible = true, state = 'off', reduced = false;
    let last: number | null = null;
    let measurement: { time: number; cost: number } | null = null;
    let settleUntil = 0, due = 0, dirty = false;
    const animated = () => !reduced || state === 'speaking';
    function queue() {
        if (!disposed && visible && frame === null) frame = request(tick);
    }
    function tick(time: number) {
        frame = null;
        if (disposed || !visible) return;
        if (measurement) {
            onFrame({ time, renderMs: measurement.cost, frameGap: time - measurement.time });
            measurement = null;
        }
        const interval = 1000 / (state === 'speaking' ? 24 : ['off', 'idle', 'error'].includes(state) ? 12 : 15);
        if (dirty || time + .5 >= due) {
            dirty = false;
            const before = now();
            render(last === null ? interval / 1000 : Math.min((time - last) / 1000, .1));
            measurement = { time, cost: now() - before };
            last = time;
            // Preserve the remainder so a 60 Hz display really delivers 24 fps.
            due = time + interval - Math.max(0, time - due) % interval;
        }
        if (animated() || time < settleUntil) queue();
    }
    function invalidate() {
        if (disposed) return;
        measurement = null;
        dirty = true;
        settleUntil = now() + 1000; last = null; due = now(); queue();
    }
    invalidate();
    return {
        invalidate,
        setState(value: string) { if (state !== value) { state = value; invalidate(); } },
        setReducedMotion(value: boolean) { if (reduced !== value) { reduced = value; invalidate(); } },
        setVisible(value: boolean) {
            if (visible === value) return;
            visible = value;
            if (value) invalidate();
            else { if (frame !== null) cancel(frame); frame = null; last = null; measurement = null; }
        },
        dispose() { disposed = true; if (frame !== null) cancel(frame); frame = null; }
    };
}

// Frame the portrait from the canvas height alone: a wider stage (a closed sidebar or
// settings drawer) shows more of the room but never shrinks the character. The compact
// phone layout, whose drawers overlay the stage, widens the view just enough to keep
// the shoulders in frame. The view spans `extent` metres vertically with a little
// headroom above the hair.
export function portraitFraming(width: number, height: number, { compact = false, distance = 1.65 } = {}) {
    const extent = compact ? Math.max(.64, .5 * height / Math.max(width, 1)) : .64;
    return { fov: 2 * Math.atan(extent / 2 / distance), targetY: 1.75 - .42 * extent };
}
