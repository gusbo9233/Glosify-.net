export function createReaderHighlights(pageSurface: HTMLElement | null, highlightLayer: HTMLElement | null) {
    const removeHighlightFragments = (kind: string) => {
        highlightLayer?.querySelectorAll(`[data-reader-highlight="${kind}"]`)
            .forEach(fragment => fragment.remove());
    };

    const mergeHighlightRects = (clientRects: DOMRect[]) => {
        if (!pageSurface) return [];
        const surface = pageSurface.getBoundingClientRect();
        const rects = clientRects
            .filter(rect => rect.width > 0.5 && rect.height > 0.5)
            .map(rect => ({
                left: Math.max(0, rect.left - surface.left),
                top: Math.max(0, rect.top - surface.top),
                right: Math.min(surface.width, rect.right - surface.left),
                bottom: Math.min(surface.height, rect.bottom - surface.top),
            }))
            .filter(rect => rect.right > rect.left && rect.bottom > rect.top)
            .sort((left, right) => left.top - right.top || left.left - right.left);
        const merged: { left: number; top: number; right: number; bottom: number }[] = [];
        for (const rect of rects) {
            const previous = merged.at(-1);
            if (previous) {
                const previousHeight = previous.bottom - previous.top;
                const rectHeight = rect.bottom - rect.top;
                const centerDistance = Math.abs(
                    (previous.top + previous.bottom) / 2 - (rect.top + rect.bottom) / 2);
                const sameLine = centerDistance <= Math.max(2, Math.min(previousHeight, rectHeight) * 0.42);
                const gap = rect.left - previous.right;
                const mergeGap = Math.max(3, Math.min(8, Math.min(previousHeight, rectHeight) * 0.6));
                if (sameLine && gap <= mergeGap) {
                    previous.left = Math.min(previous.left, rect.left);
                    previous.top = Math.min(previous.top, rect.top);
                    previous.right = Math.max(previous.right, rect.right);
                    previous.bottom = Math.max(previous.bottom, rect.bottom);
                    continue;
                }
            }
            merged.push({ ...rect });
        }
        return merged;
    };

    const paintHighlightRects = (clientRects: DOMRect[], kind: string) => {
        if (!highlightLayer) return;
        removeHighlightFragments(kind);
        for (const rect of mergeHighlightRects(clientRects)) {
            const fragment = document.createElement('div');
            fragment.className = `reader-highlight-fragment is-${kind}`;
            fragment.dataset.readerHighlight = kind;
            // Round the edges outward so snapping the origin cannot shorten the end.
            const left = Math.floor(rect.left);
            const top = Math.floor(rect.top);
            fragment.style.left = `${left}px`;
            fragment.style.top = `${top}px`;
            fragment.style.width = `${Math.ceil(rect.right) - left}px`;
            fragment.style.height = `${Math.ceil(rect.bottom) - top}px`;
            highlightLayer.append(fragment);
        }
    };

    return { removeHighlightFragments, paintHighlightRects };
}
