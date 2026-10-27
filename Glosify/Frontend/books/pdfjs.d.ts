// The PDF.js runtime is vendored under wwwroot/lib; keep its used surface explicit.
import type { PdfDocument, PdfTextContent, PdfTextLayer, PdfViewport } from './pdf-library.js';
export const GlobalWorkerOptions: { workerSrc: string };
export function getDocument(options: string | { url: string; disableAutoFetch?: boolean; disableStream?: boolean }): {
    promise: Promise<PdfDocument>;
};
export const TextLayer: new (options: { textContentSource: PdfTextContent; container: HTMLElement; viewport: PdfViewport }) => PdfTextLayer;
