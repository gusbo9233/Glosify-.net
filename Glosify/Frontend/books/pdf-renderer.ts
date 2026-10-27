import { buildPageSegments, type PageSegment, type PdfTextItem } from './segments.js';
import type { PdfDocument, PdfTextLayer, PdfPage, PdfRenderTask, loadPdfLibrary } from './pdf-library.js';

type PdfLibrary = Awaited<ReturnType<typeof loadPdfLibrary>>;
interface RenderedPage { pageNumber: number; textLayer: PdfTextLayer; textItems: PdfTextItem[]; segments: PageSegment[]; }
interface RendererOptions {
    canvas: HTMLCanvasElement | null; pageSurface: HTMLElement | null; textLayerElement: HTMLElement | null; originalPane: HTMLElement | null;
    getDocument(): PdfDocument | null; getLibrary(): PdfLibrary | null; getRotation(): number;
    computeScale(page: PdfPage, wrap: HTMLElement | null): number;
    onScale(scale: number): void; onStart(): void; onRendered(page: RenderedPage): void; onError(): void;
}
export function createPdfRenderer({ canvas, pageSurface, textLayerElement, originalPane, getDocument, getLibrary, getRotation, computeScale, onScale, onStart, onRendered, onError }: RendererOptions) {
    let renderGeneration = 0;
    let currentCanvasRenderTask: PdfRenderTask | null = null;
    let currentTextLayer: PdfTextLayer | null = null;
    const decorateTextLayer = (textLayer: PdfTextLayer, segments: PageSegment[]) => {
        const textDivs = textLayer?.textDivs || [...textLayerElement?.querySelectorAll('span') || []];
        textDivs.forEach((textDiv, itemIndex) => {
            textDiv.dataset.readerItemIndex = String(itemIndex);
        });
        for (const segment of segments) {
            for (const itemIndex of segment.itemIndices) {
                const textDiv = textDivs[itemIndex];
                if (!textDiv) continue;
                const values = new Set((textDiv.dataset.readerSegments || '').split(' ').filter(Boolean));
                values.add(String(segment.index));
                textDiv.dataset.readerSegments = [...values].join(' ');
            }
        }
    };

    return async (pageNumber: number) => {
        const pdf = getDocument(), pdfjs = getLibrary();
        if (!pdf || !pdfjs || !canvas || !pageSurface || !textLayerElement) return;
        const boundedPage = Math.min(Math.max(pageNumber, 1), pdf.numPages);
        const generation = ++renderGeneration;
        onStart();
        currentCanvasRenderTask?.cancel();
        currentTextLayer?.cancel();
        currentTextLayer = null;
        const replacementCanvas = canvas.cloneNode(false) as HTMLCanvasElement;
        canvas.replaceWith(replacementCanvas);
        canvas = replacementCanvas;
        textLayerElement.replaceChildren();

        try {
            const page = await pdf.getPage(boundedPage);
            if (generation !== renderGeneration) return;
            const wrap = originalPane || canvas.closest<HTMLElement>('.pdf-canvas-wrap');
            let scale = computeScale(page, wrap);
            scale = Math.min(5, Math.max(0.25, scale));
            onScale(scale);
            const viewport = page.getViewport({ scale, rotation: getRotation() });
            // PDF.js text spans use this CSS scale for their font sizes and positions.
            pageSurface.style.setProperty('--scale-factor', String(viewport.scale));
            const context = canvas.getContext('2d');
            if (!context) throw new Error('PDF canvas is unavailable.');
            const ratio = Math.min(window.devicePixelRatio || 1, 2);

            canvas.width = Math.floor(viewport.width * ratio);
            canvas.height = Math.floor(viewport.height * ratio);
            canvas.style.width = `${Math.floor(viewport.width)}px`;
            canvas.style.height = `${Math.floor(viewport.height)}px`;
            pageSurface.style.width = `${Math.floor(viewport.width)}px`;
            pageSurface.style.height = `${Math.floor(viewport.height)}px`;
            textLayerElement.style.width = `${Math.floor(viewport.width)}px`;
            textLayerElement.style.height = `${Math.floor(viewport.height)}px`;

            currentCanvasRenderTask = page.render({
                canvasContext: context,
                viewport,
                transform: ratio !== 1 ? [ratio, 0, 0, ratio, 0, 0] : null,
            });
            const [textContent] = await Promise.all([
                page.getTextContent(),
                currentCanvasRenderTask.promise,
            ]);
            if (generation !== renderGeneration) return;

            currentTextLayer = new pdfjs.TextLayer({
                textContentSource: textContent,
                container: textLayerElement,
                viewport,
            });
            await currentTextLayer.render();
            if (generation !== renderGeneration) return;
            const endOfContent = document.createElement('div');
            endOfContent.className = 'endOfContent';
            textLayerElement.append(endOfContent);

            const textItems = (textContent.items || []).filter(item => typeof item?.str === 'string');
            const segments = buildPageSegments(textContent);
            decorateTextLayer(currentTextLayer, segments);
            onRendered({ pageNumber: boundedPage, textLayer: currentTextLayer, textItems, segments });
        } catch (error) {
            if (generation !== renderGeneration || (error && typeof error === 'object' && 'name' in error && error.name === 'RenderingCancelledException')) return;
            onError();
        }
    };

}
