// Keep visible portraits gently alive at a lower idle rate; speech needs more frames.
// Reduced motion settles expressions before stopping non-speaking animation.
export function createRenderLoop(render, {
    request = callback => requestAnimationFrame(callback),
    cancel = id => cancelAnimationFrame(id),
    now = () => performance.now()
} = {}) {
    let frame = null, disposed = false, visible = true, state = 'off', reduced = false;
    let settleUntil = 0, last = null, due = 0;
    const animated = () => !reduced || state === 'speaking';
    function queue() {
        if (!disposed && visible && frame === null) frame = request(tick);
    }
    function tick(time) {
        frame = null;
        if (disposed || !visible) return;
        const interval = 1000 / (state === 'speaking' ? 24 : ['off', 'idle', 'error'].includes(state) ? 12 : 15);
        if (time + .5 >= due) {
            render(last === null ? interval / 1000 : Math.min((time - last) / 1000, .1));
            last = time;
            // Preserve the remainder so a 60 Hz display really delivers 24 fps.
            due = time + interval - Math.max(0, time - due) % interval;
        }
        if (animated() || time < settleUntil) queue();
    }
    function invalidate() {
        if (disposed) return;
        settleUntil = now() + 1000; last = null; due = now(); queue();
    }
    invalidate();
    return {
        invalidate,
        setState(value) { if (state !== value) { state = value; invalidate(); } },
        setReducedMotion(value) { if (reduced !== value) { reduced = value; invalidate(); } },
        setVisible(value) {
            if (visible === value) return;
            visible = value;
            if (value) invalidate();
            else { if (frame !== null) cancel(frame); frame = null; last = null; }
        },
        dispose() { disposed = true; if (frame !== null) cancel(frame); frame = null; }
    };
}

// Bound the drawing buffer even when the settings drawer closes on a wide screen.
export function portraitScaling(width, height, devicePixelRatio = 1) {
    return Math.max(1, devicePixelRatio / 1.5, width / 1000, height / 1000);
}
