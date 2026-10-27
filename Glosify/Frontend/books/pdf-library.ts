import type { PdfTextItem } from './segments.js';

export interface PdfViewport { width: number; height: number; scale: number; }
export interface PdfPage {
    getViewport(options: { scale: number; rotation?: number }): PdfViewport;
    getTextContent(): Promise<PdfTextContent>;
    render(options: { canvasContext: CanvasRenderingContext2D; viewport: PdfViewport; transform?: number[] | null }): PdfRenderTask;
}
export interface PdfDocument {
    numPages: number;
    getPage(page: number): Promise<PdfPage>;
    destroy(): Promise<void>;
}

export interface PdfRenderTask { promise: Promise<void>; cancel(): void; }
export interface PdfTextContent { items: PdfTextItem[]; }
export interface PdfTextLayer { textDivs: HTMLElement[]; render(): Promise<void>; cancel(): void; }

export async function loadPdfLibrary() {
    const library = await import('/lib/pdfjs/pdf.min.mjs');
    library.GlobalWorkerOptions.workerSrc = '/lib/pdfjs/pdf.worker.min.mjs';
    return library;
}
