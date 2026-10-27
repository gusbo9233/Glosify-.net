import { text as t } from '../shared/localization.js';
import { requiredElement } from '../shared/dom.js';
import { loadPdfLibrary, type PdfDocument, type PdfPage, type PdfTextLayer } from './pdf-library.js';

interface PageTranslation {
    targetLanguage: string; detectedSourceLanguage?: string | null; cached?: boolean;
    segments: { index: number; paragraphIndex: number; sourceText: string; translation: string }[];
}
interface TranslationJob { pageNumber: number; language: string; segments: PageSegment[]; key: string; }

import { createPdfRenderer } from './pdf-renderer.js';
import { createReaderSpeech } from './speech.js';
import { createReaderHighlights } from './highlights.js';
import { createReaderSelection, findTextNode } from './selection.js';
import { normalizeText, findSelectedSegments, type PageSegment, type PdfTextItem } from './segments.js';
const shell = requiredElement<HTMLElement>(document, '[data-document-id]');
const canvas = document.querySelector<HTMLCanvasElement>('[data-pdf-canvas]');
const pageSurface = document.querySelector<HTMLElement>('[data-pdf-page-surface]');
const highlightLayer = document.querySelector<HTMLElement>('[data-reader-highlight-layer]');
const textLayerElement = requiredElement<HTMLElement>(document, '[data-pdf-text-layer]');
const indicator = requiredElement<HTMLElement>(document, '[data-page-indicator]');
const prev = requiredElement<HTMLButtonElement>(document, '[data-prev-page]');
const next = requiredElement<HTMLButtonElement>(document, '[data-next-page]');
const status = document.querySelector<HTMLElement>('[data-reader-status]');
const assistantPanel = document.querySelector<HTMLElement>('[data-assistant-panel]');
const zoomValue = document.querySelector<HTMLElement>('[data-zoom-value]');
const zoomInBtn = document.querySelector<HTMLButtonElement>('[data-zoom-in]');
const zoomOutBtn = document.querySelector<HTMLButtonElement>('[data-zoom-out]');
const zoomResetBtn = document.querySelector<HTMLElement>('[data-zoom-reset]');
const fitToggleBtn = document.querySelector<HTMLElement>('[data-fit-toggle]');
const rotateBtn = document.querySelector<HTMLElement>('[data-rotate]');
const readerTtsToggle = document.querySelector<HTMLButtonElement>('[data-reader-tts]');
const speechDialog = document.querySelector<HTMLDialogElement>('[data-speech-dialog]');
const workspace = document.querySelector<HTMLElement>('[data-reader-workspace]');
const originalPane = document.querySelector<HTMLElement>('[data-reader-original-pane]');
const translationPane = document.querySelector<HTMLElement>('[data-reader-translation-pane]');
const translationToggle = document.querySelector<HTMLElement>('[data-translation-toggle]');
const translationToggleIcon = document.querySelector<HTMLElement>('[data-translation-toggle-icon]');
const translationToggleStatus = document.querySelector<HTMLElement>('[data-translation-toggle-status]');
const translationLanguage = document.querySelector<HTMLSelectElement>('[data-translation-language]');
const splitViewToggle = requiredElement<HTMLButtonElement>(document, '[data-split-view-toggle]');
const translationHeadingLanguage = document.querySelector<HTMLElement>('[data-translation-heading-language]');
const detectedLanguage = document.querySelector<HTMLElement>('[data-detected-language]');
const translationState = document.querySelector<HTMLElement>('[data-translation-state]');
const translationContent = document.querySelector<HTMLElement>('[data-translation-content]');
const mobileTabs = document.querySelector<HTMLElement>('[data-reader-mobile-tabs]');
const selectionPopover = document.querySelector<HTMLElement>('[data-selection-popover]');
const hoverTranslation = document.querySelector<HTMLElement>('[data-hover-translation]');
const antiforgeryToken = document.querySelector<HTMLInputElement>(".reader-antiforgery input[name='__RequestVerificationToken']")?.value || '';
const documentId = shell?.dataset.documentId;
const readingLanguage = shell?.dataset.readingLanguage || '';

let pdfjs: Awaited<ReturnType<typeof loadPdfLibrary>> | null = null;
let pdf: PdfDocument | null = null;
let currentPage = 1;
let currentSegments: PageSegment[] = [];
let currentTextLayer: PdfTextLayer | null = null;
let currentTextItems: PdfTextItem[] = [];
let resizeTimer: number | undefined;
let selectionPaintFrame: number | null = null;
let hoverCloseTimer: number | undefined;
let hoveredSegmentIndex: number | null = null;
let popoverMode: 'selection' | 'hover' | null = null;
let translationTimer: number | undefined;
let translationInFlight = false;
let queuedTranslation: TranslationJob | null = null;
let translationEnabled = false;
let splitViewEnabled = false;
let mobileView = 'original';
let activeLinkedIndices: number[] = [];

const translationCache = new Map<string, PageTranslation>();
const ZOOM_MIN = 0.25;
const ZOOM_MAX = 5;
const ZOOM_STEP = 0.25;
let fitMode: string | null = 'width';
let zoomScale = 1;
let rotation = 0;

const isTextInput = (target: EventTarget | null) => {
    const element = target as HTMLElement | null;
    if (!element) return false;
    const tagName = element.tagName?.toLowerCase();
    return tagName === 'input'
        || tagName === 'textarea'
        || tagName === 'select'
        || element.isContentEditable;
};

const { removeHighlightFragments, paintHighlightRects } = createReaderHighlights(pageSurface, highlightLayer);

const paintNativeSelection = () => {
    removeHighlightFragments('selection');
    const selection = window.getSelection();
    if (!selection || selection.isCollapsed || selection.rangeCount === 0) return;
    if (!textLayerElement?.contains(selection.anchorNode)
        || !textLayerElement.contains(selection.focusNode)) return;
    paintHighlightRects([...selection.getRangeAt(0).getClientRects()], 'selection');
};

const scheduleSelectionPaint = () => {
    if (selectionPaintFrame !== null) return;
    selectionPaintFrame = requestAnimationFrame(() => {
        selectionPaintFrame = null;
        paintNativeSelection();
    });
};

const clearReaderHighlights = () => {
    if (selectionPaintFrame !== null) cancelAnimationFrame(selectionPaintFrame);
    selectionPaintFrame = null;
    highlightLayer?.replaceChildren();
};

const segmentClientRects = (segment: PageSegment | null) => {
    const textDivs = currentTextLayer?.textDivs || [...textLayerElement?.querySelectorAll<HTMLElement>('span') || []];
    const rects = [];
    for (const part of segment?.itemParts || []) {
        const textDiv = textDivs[part.itemIndex];
        const textNode = findTextNode(textDiv);
        if (!textNode) continue;
        const length = textNode.textContent?.length || 0;
        const start = Math.min(Math.max(part.startOffset, 0), length);
        const end = Math.min(Math.max(part.endOffset, start), length);
        if (end <= start) continue;
        const range = document.createRange();
        range.setStart(textNode, start);
        range.setEnd(textNode, end);
        rects.push(...range.getClientRects());
    }
    return rects;
};

const caretAtPoint = (x: number, y: number) => {
    if (typeof document.caretPositionFromPoint === 'function') {
        const position = document.caretPositionFromPoint(x, y);
        return position ? { node: position.offsetNode, offset: position.offset } : null;
    }
    if (typeof document.caretRangeFromPoint === 'function') {
        const range = document.caretRangeFromPoint(x, y);
        return range ? { node: range.startContainer, offset: range.startOffset } : null;
    }
    return null;
};

const sentenceAtPoint = (x: number, y: number, target: EventTarget | null) => {
    const caret = caretAtPoint(x, y);
    const caretElement = caret?.node?.nodeType === Node.ELEMENT_NODE
        ? caret.node as HTMLElement
        : caret?.node?.parentElement;
    const textDiv = caretElement?.closest?.<HTMLElement>('[data-reader-item-index]')
        || (target as Element | null)?.closest?.<HTMLElement>('[data-reader-item-index]');
    if (!textDiv || !textLayerElement?.contains(textDiv)) return null;
    const itemIndex = Number(textDiv.dataset.readerItemIndex);
    if (!Number.isInteger(itemIndex)) return null;

    let itemOffset = caret?.offset ?? 0;
    if (caret?.node && textDiv.contains(caret.node)) {
        try {
            const prefix = document.createRange();
            prefix.selectNodeContents(textDiv);
            prefix.setEnd(caret.node, caret.offset);
            itemOffset = prefix.toString().length;
        } catch {
            itemOffset = caret.offset ?? 0;
        }
    }

    const candidates = currentSegments.filter(segment => segment.itemParts?.some(part =>
        part.itemIndex === itemIndex
        && itemOffset >= part.startOffset
        && (itemOffset < part.endOffset
            || (itemOffset === part.endOffset && part.endOffset === (textDiv.textContent?.length || 0)))));
    return candidates.sort((left, right) => left.sourceText.length - right.sourceText.length)[0] || null;
};

const setStatus = (message: string) => {
    if (!status) return;
    status.hidden = !message;
    status.textContent = message || '';
};

const setTranslationState = (message: string, isError = false) => {
    if (!translationState) return;
    translationState.hidden = !message;
    translationState.textContent = message || '';
    translationState.classList.toggle('is-error', Boolean(message) && isError);
};

const updateTranslationControl = (state: 'off' | 'translating' | 'ready' | 'unavailable' | 'error') => {
    if (!translationToggle) return;
    const language = translationLanguage?.value || 'English';
    const states = {
        off: {
            status: t('Client.Off', 'Off'),
            icon: 'translate',
            title: t('Reader.TranslateOffTitle', 'Translation is off. Click to translate this page'),
        },
        translating: {
            status: t('Client.Translating', 'Translating…'),
            icon: 'progress_activity',
            title: t('Client.Translating', 'Translating…'),
        },
        ready: {
            status: t('Client.On', 'On'),
            icon: 'check_circle',
            title: `${t('Client.On', 'On')} · ${language}`,
        },
        unavailable: {
            status: t('Client.TtsNoText', 'No text'),
            icon: 'block',
            title: t('Client.TtsNoText', 'This page has no readable text.'),
        },
        error: {
            status: t('Client.TranslationError', 'Error'),
            icon: 'error',
            title: t('Client.TranslationFailed', 'Translation failed. Please try again.'),
        },
    };
    const next = states[state] || states.off;
    translationToggle.dataset.translationState = state;
    translationToggle.title = next.title;
    translationToggle.setAttribute('aria-label', next.title);
    if (translationToggleStatus) translationToggleStatus.textContent = next.status;
    if (translationToggleIcon) translationToggleIcon.textContent = next.icon;
};

const syncAssistantPage = () => {
    if (assistantPanel) assistantPanel.dataset.currentPage = String(currentPage);
};

const updateToolbar = () => {
    if (zoomValue) zoomValue.textContent = `${Math.round(zoomScale * 100)}%`;
    fitToggleBtn?.classList.toggle('is-active', fitMode === 'page');
    fitToggleBtn?.setAttribute('aria-pressed', String(fitMode === 'page'));
    if (zoomOutBtn) zoomOutBtn.disabled = zoomScale <= ZOOM_MIN + 0.001;
    if (zoomInBtn) zoomInBtn.disabled = zoomScale >= ZOOM_MAX - 0.001;
};

const computeScale = (page: PdfPage, wrap: HTMLElement | null) => {
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

const closeSelectionPopover = () => {
    if (selectionPopover) selectionPopover.hidden = true;
    clearTimeout(hoverCloseTimer);
    hoverCloseTimer = undefined;
    hoveredSegmentIndex = null;
    popoverMode = null;
    activeLinkedIndices = [];
    textLayerElement?.querySelectorAll<HTMLElement>('.is-linked-source').forEach(element => element.classList.remove('is-linked-source'));
    removeHighlightFragments('linked');
    translationContent?.querySelectorAll<HTMLElement>('.is-linked').forEach(element => element.classList.remove('is-linked'));
    removeHighlightFragments('hover');
};

const closeHoverPopover = () => {
    if (popoverMode !== 'hover') return;
    closeSelectionPopover();
};

const scheduleHoverPopoverClose = () => {
    clearTimeout(hoverCloseTimer);
    hoverCloseTimer = setTimeout(closeHoverPopover, 120);
};

const highlightLinkedSegments = (indices: number[], paintSource = true) => {
    activeLinkedIndices = [...new Set(indices.map(Number))];
    const linkedSourceElements: HTMLElement[] = [];
    textLayerElement?.querySelectorAll<HTMLElement>('[data-reader-segments]').forEach(element => {
        const elementIndices = (element.dataset.readerSegments || '').split(' ').map(Number);
        const isLinked = elementIndices.some(index => activeLinkedIndices.includes(index));
        element.classList.toggle('is-linked-source', isLinked);
        if (isLinked) linkedSourceElements.push(element);
    });
    if (paintSource) {
        paintHighlightRects(linkedSourceElements.map(element => element.getBoundingClientRect()), 'linked');
    } else {
        removeHighlightFragments('linked');
    }
    translationContent?.querySelectorAll<HTMLElement>('[data-translation-segment]').forEach(element => {
        element.classList.toggle('is-linked', activeLinkedIndices.includes(Number(element.dataset.translationSegment)));
    });
};

const translationKey = (pageNumber: number, language: string, segments: PageSegment[]) => {
    const signature = segments.map(segment => segment.sourceText).join('\u241e');
    return `${pageNumber}:${language}:${signature}`;
};

const currentTranslation = () => translationCache.get(
    translationKey(currentPage, translationLanguage?.value || 'English', currentSegments));

const { captureReaderSelection, selectedSourceText } = createReaderSelection({
    textLayerElement, translationContent,
    getTextLayer: () => currentTextLayer, getTextItems: () => currentTextItems, getPage: () => currentPage,
});

const speech = createReaderSpeech({
    page: () => ({ pageNumber: currentPage, segments: currentSegments, documentId, readingLanguage,
        detectedLanguage: currentTranslation()?.detectedSourceLanguage }),
    captureReaderSelection, setStatus, paintHighlightRects, removeHighlightFragments, segmentClientRects, paintNativeSelection,
});
const { stopReaderTts, clearReaderTtsStatus, updateReaderTtsPrompt, refreshSpeechInfo, rememberReaderSelection } = speech;

const renderTranslation = (result: PageTranslation) => {
    refreshSpeechInfo();
    if (!translationContent) return;
    translationContent.replaceChildren();
    setTranslationState('');
    if (translationEnabled) updateTranslationControl('ready');
    if (translationHeadingLanguage) translationHeadingLanguage.textContent = result.targetLanguage;
    if (detectedLanguage) {
        detectedLanguage.textContent = result.detectedSourceLanguage
            ? t(result.cached ? 'Reader.DetectedCached' : 'Reader.Detected', result.cached ? 'Detected {0} · cached' : 'Detected {0}', result.detectedSourceLanguage)
            : result.cached ? t('Reader.CachedTranslation', 'Cached translation') : '';
    }

    let paragraph = null;
    let paragraphIndex = null;
    for (const segment of result.segments || []) {
        if (!paragraph || paragraphIndex !== segment.paragraphIndex) {
            paragraph = document.createElement('p');
            paragraph.className = 'reader-translation-paragraph';
            translationContent.append(paragraph);
            paragraphIndex = segment.paragraphIndex;
        } else {
            paragraph.append(document.createTextNode(' '));
        }
        const span = document.createElement('span');
        span.className = 'reader-translation-segment';
        span.dataset.translationSegment = String(segment.index);
        span.textContent = segment.translation;
        paragraph.append(span);
    }
    highlightLinkedSegments(activeLinkedIndices);
};

const showCachedTranslation = () => {
    const result = currentTranslation();
    if (result) {
        renderTranslation(result);
        return true;
    }
    if (translationContent) translationContent.replaceChildren();
    if (detectedLanguage) detectedLanguage.textContent = '';
    return false;
};

const requestJson = async <T>(url: string, options: RequestInit): Promise<T> => {
    if (document.body.dataset.guestSection) {
        document.dispatchEvent(new CustomEvent('glosify:login-required'));
        throw new Error('Sign in to translate your own books.');
    }
    const headers = new Headers(options?.headers || {});
    headers.set('Accept', 'application/json');
    headers.set('Content-Type', 'application/json');
    if (antiforgeryToken) headers.set('RequestVerificationToken', antiforgeryToken);
    const response = await fetch(url, { ...options, headers });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(t('Client.TranslationFailed', 'The page could not be translated.'));
    return payload;
};

const runTranslationQueue = async () => {
    if (translationInFlight || !queuedTranslation) return;
    const job = queuedTranslation;
    queuedTranslation = null;
    translationInFlight = true;
    if (translationEnabled && currentPage === job.pageNumber) {
        updateTranslationControl('translating');
        setTranslationState(`Translating page ${job.pageNumber}…`);
    }
    try {
        const result = await requestJson<PageTranslation>(
            `/Books/${documentId}/Pages/${job.pageNumber}/Translation`,
            {
                method: 'POST',
                body: JSON.stringify({
                    targetLanguage: job.language,
                    segments: job.segments.map(segment => ({
                        index: segment.index,
                        paragraphIndex: segment.paragraphIndex,
                        sourceText: segment.sourceText,
                    })),
                }),
            });
        translationCache.set(job.key, result);
        if (translationEnabled
            && currentPage === job.pageNumber
            && translationLanguage?.value === job.language
            && translationKey(currentPage, job.language, currentSegments) === job.key) {
            renderTranslation(result);
        }
    } catch {
        if (translationEnabled && currentPage === job.pageNumber) {
            updateTranslationControl('error');
            setTranslationState(t('Client.TranslationFailed', 'The page could not be translated.'), true);
        }
    } finally {
        translationInFlight = false;
        if (queuedTranslation) runTranslationQueue();
    }
};

const queueCurrentPageTranslation = () => {
    clearTimeout(translationTimer);
    if (!translationEnabled) return;
    if (currentSegments.length === 0) {
        queuedTranslation = null;
        updateTranslationControl('unavailable');
        setTranslationState('No selectable text found on this page.', true);
        return;
    }
    if (showCachedTranslation()) return;
    updateTranslationControl('translating');
    setTranslationState('Waiting to translate this page…');
    translationTimer = setTimeout(() => {
        const language = translationLanguage?.value || 'English';
        const segments = currentSegments.map(segment => ({ ...segment, itemIndices: [...segment.itemIndices] }));
        const key = translationKey(currentPage, language, segments);
        if (translationCache.has(key)) {
            showCachedTranslation();
            return;
        }
        queuedTranslation = { pageNumber: currentPage, language, segments, key };
        runTranslationQueue();
    }, 400);
};

const renderPage = createPdfRenderer({
    canvas, pageSurface, textLayerElement, originalPane,
    getDocument: () => pdf, getLibrary: () => pdfjs,
    getRotation: () => rotation, computeScale,
    onScale: scale => { zoomScale = scale; },
    onStart: () => {
        stopReaderTts();
        clearReaderTtsStatus();
        speech.clearSelection();
        if (readerTtsToggle) readerTtsToggle.disabled = true;
        closeSelectionPopover();
        clearReaderHighlights();
        currentSegments = [];
        currentTextLayer = null;
        setStatus('');
        if (translationEnabled) updateTranslationControl('translating');
    },
    onRendered: result => {
        currentPage = result.pageNumber;
        currentTextLayer = result.textLayer;
        currentTextItems = result.textItems;
        currentSegments = result.segments;
        indicator.textContent = t('Client.PageOf', 'Page {0} of {1}', currentPage, pdf!.numPages);
        prev.disabled = currentPage <= 1;
        next.disabled = currentPage >= pdf!.numPages;
        splitViewToggle.disabled = !translationEnabled || currentSegments.length === 0;
        if (readerTtsToggle) readerTtsToggle.disabled = currentSegments.length === 0;
        updateToolbar();
        updateReaderTtsPrompt();
        syncAssistantPage();
        if (currentSegments.length === 0) {
            setStatus(t('Reader.NoSelectableTextPage', 'No selectable text found on this page.'));
        }
        if (translationEnabled) queueCurrentPageTranslation();
    },
    onError: () => {
        if (readerTtsToggle) readerTtsToggle.disabled = true;
        setStatus(t('Reader.RenderFailed', 'This page could not be rendered.'));
    },
});

const goToPage = (pageNumber: number) => {
    if (!pdf) return;
    renderPage(Math.min(Math.max(pageNumber, 1), pdf.numPages));
};

const applyZoom = (newScale: number) => {
    fitMode = null;
    zoomScale = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, newScale));
    renderPage(currentPage);
};

const setMobileView = (view: string | undefined) => {
    mobileView = view === 'translation' ? 'translation' : 'original';
    workspace?.classList.toggle('mobile-show-translation', mobileView === 'translation');
    workspace?.classList.toggle('mobile-show-original', mobileView === 'original');
    mobileTabs?.querySelectorAll<HTMLElement>('[data-reader-mobile-view]').forEach(button => {
        button.classList.toggle('is-active', button.dataset.readerMobileView === mobileView);
    });
};

const setSplitView = (enabled: boolean) => {
    splitViewEnabled = Boolean(enabled && translationEnabled && currentSegments.length > 0);
    workspace?.classList.toggle('is-split', splitViewEnabled);
    splitViewToggle?.classList.toggle('is-active', splitViewEnabled);
    splitViewToggle?.setAttribute('aria-pressed', String(splitViewEnabled));
    if (translationPane) translationPane.hidden = !splitViewEnabled;
    if (mobileTabs) mobileTabs.hidden = !splitViewEnabled;
    setMobileView('original');
    requestAnimationFrame(() => renderPage(currentPage));
};

const setTranslationEnabled = (enabled: boolean) => {
    translationEnabled = Boolean(enabled);
    translationToggle?.classList.toggle('is-active', translationEnabled);
    translationToggle?.setAttribute('aria-pressed', String(translationEnabled));
    splitViewToggle.disabled = !translationEnabled || currentSegments.length === 0;
    closeSelectionPopover();
    if (!translationEnabled) {
        clearTimeout(translationTimer);
        queuedTranslation = null;
        setSplitView(false);
        setTranslationState('');
        updateTranslationControl('off');
        return;
    }
    updateTranslationControl('translating');
    queueCurrentPageTranslation();
};

const persistTranslationLanguage = async () => {
    // Bundled samples have no saved preference; choosing a language there stays local.
    if (!documentId || !translationLanguage || document.body.dataset.guestSection) return;
    try {
        const result = await requestJson<{ targetLanguage: string }>(`/Books/${documentId}/TranslationPreference`, {
            method: 'PUT',
            body: JSON.stringify({ targetLanguage: translationLanguage.value }),
        });
        shell.dataset.preferredTranslationLanguage = result.targetLanguage;
    } catch {
        setTranslationState(t('Client.PreferenceFailed', 'The language preference could not be saved.'), true);
    }
};

const positionPopover = (rect: Pick<DOMRect, 'left' | 'top' | 'width' | 'bottom'>, placement = 'auto') => {
    if (!selectionPopover) return;
    const margin = 12;
    selectionPopover.hidden = false;
    const bounds = selectionPopover.getBoundingClientRect();
    const left = Math.min(
        Math.max(rect.left + rect.width / 2 - bounds.width / 2, margin),
        window.innerWidth - bounds.width - margin);
    const gap = placement === 'above' ? 6 : 10;
    let top = placement === 'above'
        ? rect.top - bounds.height - gap
        : rect.bottom + 10;
    if (top < margin) top = Math.min(window.innerHeight - bounds.height - margin, rect.bottom + gap);
    if (top + bounds.height > window.innerHeight - margin) {
        top = Math.max(margin, rect.top - bounds.height - gap);
    }
    selectionPopover.style.left = `${left}px`;
    selectionPopover.style.top = `${top}px`;
};

const handleSourceSelection = () => {
    if (!translationEnabled || !textLayerElement) return;
    const selection = window.getSelection();
    if (!selection || selection.isCollapsed || selection.rangeCount === 0) return;
    const range = selection.getRangeAt(0);
    if (!textLayerElement.contains(range.commonAncestorContainer)) return;
    const result = currentTranslation();
    const preferredIndices = [...textLayerElement.querySelectorAll<HTMLElement>('[data-reader-segments]')]
        .filter(element => range.intersectsNode(element))
        .flatMap(element => (element.dataset.readerSegments || '').split(' ').filter(Boolean).map(Number));
    const matches = findSelectedSegments(selection.toString(), result, preferredIndices, currentSegments);
    if (!matches.length) return;
    const translatedText = matches
        .filter(segment => normalizeText(segment.translation) !== normalizeText(segment.sourceText))
        .map(segment => segment.translation)
        .join(' ')
        .trim();
    if (!translatedText) {
        closeSelectionPopover();
        return;
    }
    if (hoverTranslation) hoverTranslation.textContent = translatedText;
    popoverMode = 'selection';
    hoveredSegmentIndex = null;
    removeHighlightFragments('hover');
    highlightLinkedSegments(matches.map(segment => segment.index), false);
    const target = translationContent?.querySelector(`[data-translation-segment="${matches[0].index}"]`);
    target?.scrollIntoView({ block: 'center', behavior: 'smooth' });
    positionPopover(range.getBoundingClientRect());
};

const handleSentenceHover = (event: PointerEvent) => {
    if (!translationEnabled || event.pointerType === 'touch' || textLayerElement?.classList.contains('selecting')) {
        return;
    }
    const selection = window.getSelection();
    if (selection && !selection.isCollapsed && textLayerElement?.contains(selection.anchorNode)) return;
    const segment = sentenceAtPoint(event.clientX, event.clientY, event.target);
    const rects = segmentClientRects(segment);
    const hoveredLine = rects.find(rect =>
        event.clientX >= rect.left - 1
        && event.clientX <= rect.right + 1
        && event.clientY >= rect.top - 1
        && event.clientY <= rect.bottom + 1);
    const translated = segment && hoveredLine
        ? currentTranslation()?.segments?.find(candidate => candidate.index === segment.index)
        : null;
    const translatedText = translated?.translation?.trim() || '';
    if (!segment
        || !hoveredLine
        || !translatedText
        || normalizeText(translatedText) === normalizeText(segment.sourceText)) {
        scheduleHoverPopoverClose();
        return;
    }
    clearTimeout(hoverCloseTimer);
    hoverCloseTimer = undefined;
    if (popoverMode === 'hover' && hoveredSegmentIndex === segment.index) return;

    const anchor = {
        left: event.clientX,
        top: hoveredLine.top,
        right: event.clientX,
        bottom: hoveredLine.bottom,
        width: 0,
        height: hoveredLine.height,
    };
    hoveredSegmentIndex = segment.index;
    popoverMode = 'hover';
    if (hoverTranslation) hoverTranslation.textContent = translatedText;
    paintHighlightRects(rects, 'hover');
    positionPopover(anchor, 'above');
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
translationToggle?.addEventListener('click', () => setTranslationEnabled(!translationEnabled));
splitViewToggle?.addEventListener('click', () => setSplitView(!splitViewEnabled));
translationLanguage?.addEventListener('change', async () => {
    stopReaderTts();
    closeSelectionPopover();
    if (translationHeadingLanguage) translationHeadingLanguage.textContent = translationLanguage.value;
    await persistTranslationLanguage();
    if (translationEnabled) queueCurrentPageTranslation();
});
mobileTabs?.addEventListener('click', event => {
    const button = (event.target as Element | null)?.closest<HTMLElement>('[data-reader-mobile-view]');
    if (button) setMobileView(button.dataset.readerMobileView);
});
textLayerElement?.addEventListener('mouseup', () => setTimeout(() => {
    paintNativeSelection();
    rememberReaderSelection();
    handleSourceSelection();
}));
textLayerElement?.addEventListener('touchend', () => setTimeout(() => {
    paintNativeSelection();
    rememberReaderSelection();
    handleSourceSelection();
}, 50));
textLayerElement?.addEventListener('copy', event => {
    const selection = window.getSelection();
    if (!selection || selection.isCollapsed || selection.rangeCount === 0) return;
    const range = selection.getRangeAt(0);
    if (!textLayerElement.contains(range.commonAncestorContainer)) return;
    const text = selectedSourceText(range);
    if (!text || !event.clipboardData) return;
    event.clipboardData.setData('text/plain', text);
    event.preventDefault();
});
textLayerElement?.addEventListener('pointermove', handleSentenceHover);
textLayerElement?.addEventListener('pointerleave', scheduleHoverPopoverClose);
textLayerElement?.addEventListener('pointerdown', () => {
    closeHoverPopover();
    textLayerElement.classList.add('selecting');
});
document.addEventListener('pointerup', () => textLayerElement?.classList.remove('selecting'));
document.addEventListener('selectionchange', () => {
    scheduleSelectionPaint();
    rememberReaderSelection();
});
translationContent?.addEventListener('mouseup', event => {
    const selection = window.getSelection();
    const range = selection && !selection.isCollapsed && selection.rangeCount > 0
        ? selection.getRangeAt(0)
        : null;
    let indices: number[] = [];
    if (range && translationContent.contains(range.commonAncestorContainer)) {
        rememberReaderSelection();
        indices = [...translationContent.querySelectorAll<HTMLElement>('[data-translation-segment]')]
            .filter(element => range.intersectsNode(element))
            .map(element => Number(element.dataset.translationSegment));
    } else {
        const segment = (event.target as Element | null)?.closest<HTMLElement>('[data-translation-segment]');
        if (segment) indices = [Number(segment.dataset.translationSegment)];
    }
    if (!indices.length) return;
    highlightLinkedSegments(indices);
    const sourceTarget = [...textLayerElement.querySelectorAll<HTMLElement>('[data-reader-segments]')]
        .find(element => (element.dataset.readerSegments || '').split(' ').map(Number).includes(indices[0]));
    sourceTarget?.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
});
document.addEventListener('pointerdown', event => {
    speech.preserveSelection(event.target, true);
    if (selectionPopover && !selectionPopover.hidden
        && !selectionPopover.contains(event.target as Node | null)
        && !textLayerElement?.contains(event.target as Node | null)) {
        closeSelectionPopover();
    }
});

window.addEventListener('keydown', (event) => {
    speech.preserveSelection(event.target);
    if (speechDialog?.open || event.altKey || event.metaKey || event.shiftKey || isTextInput(event.target)) return;
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
    } else if (event.key === 'ArrowLeft') {
        event.preventDefault();
        goToPage(currentPage - 1);
    } else if (event.key === 'ArrowRight') {
        event.preventDefault();
        goToPage(currentPage + 1);
    } else if (event.key === 'Escape') {
        if (speech.playing) stopReaderTts();
        else closeSelectionPopover();
    }
});

originalPane?.addEventListener('wheel', (event) => {
    if (!event.ctrlKey) return;
    event.preventDefault();
    applyZoom(zoomScale + (event.deltaY < 0 ? ZOOM_STEP : -ZOOM_STEP));
}, { passive: false });
originalPane?.addEventListener('scroll', closeHoverPopover, { passive: true });

window.addEventListener('resize', () => {
    clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => renderPage(currentPage), 140);
});
window.addEventListener('beforeunload', () => window.GlosifyTts?.stop());


try {
    pdfjs = await loadPdfLibrary();
    pdf = await pdfjs.getDocument(document.querySelector<HTMLElement>("[data-pdf-url]")?.dataset.pdfUrl || `/Books/File/${documentId}`).promise;
    await renderPage(1);
} catch {
    setStatus(t('Reader.PdfLoadFailed', 'The PDF could not be loaded.'));
}
