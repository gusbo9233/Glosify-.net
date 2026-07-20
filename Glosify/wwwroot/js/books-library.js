const fileInput = document.querySelector('[data-book-file-input]');
const fileLabel = document.querySelector('[data-book-file-label]');

fileInput?.addEventListener('change', () => {
    if (fileLabel) {
        fileLabel.textContent = fileInput.files?.[0]?.name || 'Choose a PDF';
    }
});

const previews = [...document.querySelectorAll('[data-book-preview]')];

if (previews.length > 0) {
    const pdfjs = await import('/lib/pdfjs/pdf.min.mjs');
    pdfjs.GlobalWorkerOptions.workerSrc = '/lib/pdfjs/pdf.worker.min.mjs';

    const pending = [];
    let activeRenders = 0;
    const maxConcurrentRenders = 3;

    const renderPreview = async (canvas) => {
        let document = null;

        try {
            const url = canvas.dataset.pdfUrl;
            if (!url) return;

            document = await pdfjs.getDocument(url).promise;
            const page = await document.getPage(1);
            const baseViewport = page.getViewport({ scale: 1 });
            const displayWidth = canvas.parentElement?.clientWidth || 112;
            const pixelRatio = Math.min(window.devicePixelRatio || 1, 2);
            const viewport = page.getViewport({
                scale: (displayWidth / baseViewport.width) * pixelRatio
            });
            const context = canvas.getContext('2d', { alpha: false });

            canvas.width = Math.max(1, Math.floor(viewport.width));
            canvas.height = Math.max(1, Math.floor(viewport.height));

            await page.render({ canvasContext: context, viewport }).promise;
            canvas.classList.add('is-rendered');
        } catch {
            canvas.classList.add('is-preview-error');
        } finally {
            await document?.destroy();
        }
    };

    const runNext = () => {
        while (activeRenders < maxConcurrentRenders && pending.length > 0) {
            const canvas = pending.shift();
            activeRenders += 1;
            renderPreview(canvas).finally(() => {
                activeRenders -= 1;
                runNext();
            });
        }
    };

    const queuePreview = (canvas) => {
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
                queuePreview(entry.target);
            }
        }, { rootMargin: '240px' });

        previews.forEach((canvas) => observer.observe(canvas));
    } else {
        previews.forEach(queuePreview);
    }
}
