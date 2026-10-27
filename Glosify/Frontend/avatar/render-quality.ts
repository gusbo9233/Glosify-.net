const densities = [.5, .67, .85, 1, 1.25, 1.5, 2];
export const normalizeQuality = (value: string | null) => value !== null && ['auto', 'high', 'low'].includes(value) ? value : 'auto';

// Babylon's hardware scaling is the inverse of pixels per CSS pixel. Bound both
// dimensions and total allocation, even with browser zoom or a very large screen.
export function renderScaling(width: number, height: number, devicePixelRatio = 1, density = 1, low = false) {
    const w = Math.max(1, width), h = Math.max(1, height);
    const dpr = Number.isFinite(devicePixelRatio) && devicePixelRatio > 0 ? devicePixelRatio : 1;
    const pixelsPerCssPixel = Math.min(density, dpr, 2);
    return Math.max(1 / pixelsPerCssPixel, w / (low ? 1000 : 2560), h / (low ? 1000 : 2560),
        Math.sqrt(w * h / (low ? 750000 : 3000000)));
}

export function createRenderQuality(initialMode = 'auto') {
    let mode = normalizeQuality(initialMode), level = 3, maxLevel = densities.length - 1;
    let backgroundReduced = false;
    // Remember failed settings for this scene size. Repeatedly retrying the same
    // slow resolution makes Auto oscillate and prevents background recovery.
    const failed = new Set();
    let sizeKey: string;
    let samples: number[] = [], started = null, warmUntil = 0, goodWindows = 0, raiseAfter = 0;
    function reset(time: number) {
        samples = []; started = null; warmUntil = time + 2000; goodWindows = 0;
    }
    return {
        reset,
        setMode(value: string, time: number) { mode = normalizeQuality(value); level = 3; backgroundReduced = false; failed.clear(); raiseAfter = 0; reset(time); },
        background() { return mode === 'low' || (mode === 'auto' && backgroundReduced) ? 'reduced' : 'full'; },
        scaling(width: number, height: number, dpr: number | undefined) {
            const nextSize = `${width}:${height}:${dpr}`;
            if (sizeKey !== nextSize) { failed.clear(); sizeKey = nextSize; }
            const ceiling = 1 / renderScaling(width, height, dpr, 2);
            maxLevel = densities.findIndex(density => density >= ceiling);
            if (maxLevel < 0) maxLevel = densities.length - 1;
            return renderScaling(width, height, dpr, mode === 'high' ? 2 : mode === 'low' ? .75 : densities[level], mode === 'low');
        },
        // Sample only the animation callback immediately after a rendered frame.
        // This observes stalls without mistaking our intentional 12/15/24 fps
        // pacing for a slow machine. Isolated pauses cannot lower quality.
        sample({ time, renderMs, frameGap }: { time: number; renderMs: number; frameGap: number }) {
            if (mode !== 'auto' || !Number.isFinite(renderMs) || !Number.isFinite(frameGap)) return false;
            if (frameGap > 1000 || renderMs > 1000) { reset(time); return false; }
            if (time < warmUntil) return false;
            started ??= time;
            samples.push(Math.max(renderMs, frameGap));
            if (time - started < 2000 || samples.length < 12) return false;
            const sorted = samples.sort((a, b) => a - b);
            const cost = sorted[Math.floor((sorted.length - 1) * .75)];
            samples = []; started = null;
            const effectiveLevel = Math.min(level, maxLevel);
            if (cost > 40 && (!backgroundReduced || effectiveLevel > 0)) {
                failed.add(`${effectiveLevel}:${backgroundReduced ? 'reduced' : 'full'}`);
                // Remove backdrop effects before sacrificing face and hair detail.
                if (!backgroundReduced) backgroundReduced = true;
                else level = effectiveLevel - 1;
                raiseAfter = time + 12000; reset(time); return true;
            }
            goodWindows = cost < 23 ? goodWindows + 1 : 0;
            const nextLevel = effectiveLevel + 1;
            const canRaise = nextLevel <= maxLevel && !failed.has(`${nextLevel}:reduced`)
                && (backgroundReduced || !failed.has(`${nextLevel}:full`));
            const canRestoreBackground = backgroundReduced && !failed.has(`${effectiveLevel}:full`);
            if (goodWindows >= 3 && time >= raiseAfter && (canRaise || canRestoreBackground)) {
                // Restore sharpness up to the sustainable ceiling, then effects.
                if (canRaise) level = nextLevel;
                else backgroundReduced = false;
                reset(time); return true;
            }
            return false;
        }
    };
}
