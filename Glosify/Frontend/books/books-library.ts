import { loadPdfLibrary, type PdfDocument } from './pdf-library.js';

const fileInput = document.querySelector<HTMLInputElement>('[data-book-file-input]');
const fileLabel = document.querySelector('[data-book-file-label]');

fileInput?.addEventListener('change', () => {
    if (fileLabel) {
        fileLabel.textContent = fileInput.files?.[0]?.name || 'Choose a PDF';
    }
});

const previews = [...document.querySelectorAll<HTMLCanvasElement>('[data-book-preview]')];

if (previews.length > 0) {
    const pdfjs = await loadPdfLibrary();

    const pending: HTMLCanvasElement[] = [];
    let activeRenders = 0;
    const maxConcurrentRenders = 3;

    const renderPreview = async (canvas: HTMLCanvasElement) => {
        let document: PdfDocument | null = null;

        try {
            const url = canvas.dataset.pdfUrl;
            if (!url) return;

            // Range support on the endpoint is only half of it: pdf.js still
            // prefetches the rest of the document once it has what it needs,
            // and streams the body besides. A shelf thumbnail is page one at
            // 112px, so both are pure download. Turning them off leaves it
            // reading the few ranges the first page actually occupies.
            document = await pdfjs.getDocument({
                url,
                disableAutoFetch: true,
                disableStream: true
            }).promise;
            const page = await document.getPage(1);
            const baseViewport = page.getViewport({ scale: 1 });
            const displayWidth = canvas.parentElement?.clientWidth || 112;
            const pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
            const viewport = page.getViewport({
                scale: (displayWidth / baseViewport.width) * pixelRatio
            });
            const context = canvas.getContext('2d', { alpha: false });
            if (!context) throw new Error('PDF preview canvas is unavailable.');

            canvas.width = Math.max(1, Math.floor(viewport.width));
            canvas.height = Math.max(1, Math.floor(viewport.height));

            await page.render({ canvasContext: context, viewport }).promise;
            canvas.classList.add('is-rendered');
        } catch {
            canvas.classList.add('is-preview-error');
        } finally {
            try {
                await document?.destroy();
            } catch {
                // Worker teardown is best effort; keep the rendered preview and release its queue slot.
            }
        }
    };

    const runNext = () => {
        while (activeRenders < maxConcurrentRenders && pending.length > 0) {
            const canvas = pending.shift()!;
            activeRenders += 1;
            renderPreview(canvas).finally(() => {
                activeRenders -= 1;
                runNext();
            });
        }
    };

    const queuePreview = (canvas: HTMLCanvasElement) => {
        if (canvas.dataset.previewQueued === 'true') return;
        canvas.dataset.previewQueued = 'true';
        pending.push(canvas);
        runNext();
    };

    if ('IntersectionObserver' in window) {
        const observer = new IntersectionObserver((entries) => {
            for (const entry of entries) {
                if (!entry.isIntersecting) continue;
                observer.unobserve(entry.target);
                queuePreview(entry.target as HTMLCanvasElement);
            }
        }, { rootMargin: '240px' });

        previews.forEach((canvas) => observer.observe(canvas));
    } else {
        previews.forEach(queuePreview);
    }
}
