const shell = document.querySelector('[data-document-id]');
const canvas = document.querySelector('[data-pdf-canvas]');
const indicator = document.querySelector('[data-page-indicator]');
const prev = document.querySelector('[data-prev-page]');
const next = document.querySelector('[data-next-page]');
const status = document.querySelector('[data-reader-status]');
const assistantPanel = document.querySelector('[data-assistant-panel]');
const zoomValue = document.querySelector('[data-zoom-value]');
const zoomInBtn = document.querySelector('[data-zoom-in]');
const zoomOutBtn = document.querySelector('[data-zoom-out]');
const zoomResetBtn = document.querySelector('[data-zoom-reset]');
const fitToggleBtn = document.querySelector('[data-fit-toggle]');
const rotateBtn = document.querySelector('[data-rotate]');
const documentId = shell?.dataset.documentId;
let pdf = null;
let currentPage = 1;
let rendering = false;
let pendingRender = false;

const ZOOM_MIN = 0.25;
const ZOOM_MAX = 5;
const ZOOM_STEP = 0.25;
let fitMode = 'width'; // 'width' | 'page' | null (custom zoom)
let zoomScale = 1;
let rotation = 0;

const isTextInput = (element) => {
    if (!element) return false;
    const tagName = element.tagName?.toLowerCase();
    return tagName === 'input'
        || tagName === 'textarea'
        || tagName === 'select'
        || element.isContentEditable;
};

const goToPage = (pageNumber) => {
    if (!pdf) return;
    const boundedPage = Math.min(Math.max(pageNumber, 1), pdf.numPages);
    if (boundedPage === currentPage || rendering) return;
    renderPage(boundedPage);
};

const setStatus = (message) => {
    if (!status) return;
    status.hidden = !message;
    status.textContent = message || '';
};

const syncAssistantPage = () => {
    if (assistantPanel) {
        assistantPanel.dataset.currentPage = String(currentPage);
    }
};

const updateToolbar = () => {
    if (zoomValue) zoomValue.textContent = `${Math.round(zoomScale * 100)}%`;
    fitToggleBtn?.classList.toggle('is-active', fitMode === 'page');
    fitToggleBtn?.setAttribute('aria-pressed', String(fitMode === 'page'));
    if (zoomOutBtn) zoomOutBtn.disabled = zoomScale <= ZOOM_MIN + 0.001;
    if (zoomInBtn) zoomInBtn.disabled = zoomScale >= ZOOM_MAX - 0.001;
};

const computeScale = (page, wrap) => {
    const padding = 48;
    const base = page.getViewport({ scale: 1, rotation });
    if (fitMode === 'width') {
        const availableWidth = Math.max((wrap?.clientWidth || 900) - padding, 320);
        return availableWidth / base.width;
    }
    if (fitMode === 'page') {
        const availableWidth = Math.max((wrap?.clientWidth || 900) - padding, 320);
        const availableHeight = Math.max((wrap?.clientHeight || 600) - padding, 320);
        return Math.min(availableWidth / base.width, availableHeight / base.height);
    }
    return zoomScale;
};

const renderPage = async (pageNumber) => {
    if (!pdf || !canvas) return;
    if (rendering) { pendingRender = true; return; }
    rendering = true;
    try {
        const page = await pdf.getPage(pageNumber);
        const wrap = canvas.closest('.pdf-canvas-wrap');
        let scale = computeScale(page, wrap);
        scale = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, scale));
        zoomScale = scale;
        const viewport = page.getViewport({ scale, rotation });
        const context = canvas.getContext('2d');
        const ratio = Math.min(window.devicePixelRatio || 1, 2);

        canvas.width = Math.floor(viewport.width * ratio);
        canvas.height = Math.floor(viewport.height * ratio);
        canvas.style.width = `${Math.floor(viewport.width)}px`;
        canvas.style.height = `${Math.floor(viewport.height)}px`;

        await page.render({
            canvasContext: context,
            viewport,
            transform: ratio !== 1 ? [ratio, 0, 0, ratio, 0, 0] : null
        }).promise;
        currentPage = pageNumber;
        indicator.textContent = `Page ${currentPage} of ${pdf.numPages}`;
        prev.disabled = currentPage <= 1;
        next.disabled = currentPage >= pdf.numPages;
        updateToolbar();
        syncAssistantPage();
        setStatus('');
    } catch {
        setStatus('This page could not be rendered.');
    } finally {
        rendering = false;
        if (pendingRender) {
            pendingRender = false;
            renderPage(currentPage);
        }
    }
};

const applyZoom = (newScale) => {
    fitMode = null;
    zoomScale = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, newScale));
    renderPage(currentPage);
};

prev?.addEventListener('click', () => goToPage(currentPage - 1));
next?.addEventListener('click', () => goToPage(currentPage + 1));

zoomInBtn?.addEventListener('click', () => applyZoom(zoomScale + ZOOM_STEP));
zoomOutBtn?.addEventListener('click', () => applyZoom(zoomScale - ZOOM_STEP));
zoomResetBtn?.addEventListener('click', () => applyZoom(1));
fitToggleBtn?.addEventListener('click', () => {
    fitMode = fitMode === 'page' ? 'width' : 'page';
    renderPage(currentPage);
});
rotateBtn?.addEventListener('click', () => {
    rotation = (rotation + 90) % 360;
    renderPage(currentPage);
});

window.addEventListener('keydown', (event) => {
    if (event.altKey || event.metaKey || event.shiftKey || isTextInput(event.target)) {
        return;
    }

    // Ctrl+/- mirrors browser zoom shortcuts; bare keys also work.
    if (event.key === '+' || event.key === '=') {
        event.preventDefault();
        applyZoom(zoomScale + ZOOM_STEP);
        return;
    }
    if (event.key === '-' || event.key === '_') {
        event.preventDefault();
        applyZoom(zoomScale - ZOOM_STEP);
        return;
    }
    if (event.key === '0') {
        event.preventDefault();
        applyZoom(1);
        return;
    }

    if (event.ctrlKey) return;

    if (event.key === 'r' || event.key === 'R') {
        event.preventDefault();
        rotation = (rotation + 90) % 360;
        renderPage(currentPage);
    }

    if (event.key === 'ArrowLeft') {
        event.preventDefault();
        goToPage(currentPage - 1);
    }

    if (event.key === 'ArrowRight') {
        event.preventDefault();
        goToPage(currentPage + 1);
    }
});

canvas?.closest('.pdf-canvas-wrap')?.addEventListener('wheel', (event) => {
    if (!event.ctrlKey) return;
    event.preventDefault();
    applyZoom(zoomScale + (event.deltaY < 0 ? ZOOM_STEP : -ZOOM_STEP));
}, { passive: false });

window.addEventListener('resize', () => renderPage(currentPage));

try {
    const pdfjs = await import('/lib/pdfjs/pdf.min.mjs');
    pdfjs.GlobalWorkerOptions.workerSrc = '/lib/pdfjs/pdf.worker.min.mjs';
    pdf = await pdfjs.getDocument(`/Books/File/${documentId}`).promise;
    await renderPage(1);
} catch {
    setStatus('The PDF could not be loaded.');
}
