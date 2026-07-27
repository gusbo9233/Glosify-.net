const shell = document.querySelector('[data-document-id]');
let canvas = document.querySelector('[data-pdf-canvas]');
const pageSurface = document.querySelector('[data-pdf-page-surface]');
const highlightLayer = document.querySelector('[data-reader-highlight-layer]');
const textLayerElement = document.querySelector('[data-pdf-text-layer]');
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
const readerTtsToggle = document.querySelector('[data-reader-tts]');
const readerTtsIcon = document.querySelector('[data-reader-tts-icon]');
const readerTtsLabel = document.querySelector('[data-reader-tts-label]');
const readerTtsStatus = document.querySelector('[data-reader-tts-status]');
const readerVoiceControl = document.querySelector('[data-reader-voice-control]');
const readerVoiceSelect = document.querySelector('[data-reader-voice]');
const workspace = document.querySelector('[data-reader-workspace]');
const originalPane = document.querySelector('[data-reader-original-pane]');
const translationPane = document.querySelector('[data-reader-translation-pane]');
const translationToggle = document.querySelector('[data-translation-toggle]');
const translationToggleIcon = document.querySelector('[data-translation-toggle-icon]');
const translationToggleStatus = document.querySelector('[data-translation-toggle-status]');
const translationLanguage = document.querySelector('[data-translation-language]');
const splitViewToggle = document.querySelector('[data-split-view-toggle]');
const translationHeadingLanguage = document.querySelector('[data-translation-heading-language]');
const detectedLanguage = document.querySelector('[data-detected-language]');
const translationState = document.querySelector('[data-translation-state]');
const translationContent = document.querySelector('[data-translation-content]');
const mobileTabs = document.querySelector('[data-reader-mobile-tabs]');
const selectionPopover = document.querySelector('[data-selection-popover]');
const hoverTranslation = document.querySelector('[data-hover-translation]');
const antiforgeryToken = document.querySelector(".reader-antiforgery input[name='__RequestVerificationToken']")?.value || '';
const documentId = shell?.dataset.documentId;
const readingLanguage = shell?.dataset.readingLanguage || '';

let pdfjs = null;
let pdf = null;
let currentPage = 1;
let currentSegments = [];
let currentTextLayer = null;
let currentTextItems = [];
let currentCanvasRenderTask = null;
let renderGeneration = 0;
let resizeTimer = null;
let selectionPaintFrame = null;
let hoverCloseTimer = null;
let hoveredSegmentIndex = null;
let popoverMode = null;
let translationTimer = null;
let translationInFlight = false;
let queuedTranslation = null;
let translationEnabled = false;
let splitViewEnabled = false;
let mobileView = 'original';
let activeLinkedIndices = [];
let readerTtsPlaying = false;
let readerTtsStatusTimer = null;
let lastReaderSelection = null;

const translationCache = new Map();
const ZOOM_MIN = 0.25;
const ZOOM_MAX = 5;
const ZOOM_STEP = 0.25;
const TTS_CHUNK_LENGTH = 180;
const POLISH_VOICE_STORAGE_KEY = 'glosify.reader.polishVoice';
const POLISH_VOICES = new Set(['zofia', 'agnieszka', 'marek']);
let fitMode = 'width';
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

const normalizeText = (value) => (value || '')
    .normalize('NFKC')
    .replace(/[\u2010-\u2015\u2212]/gu, '-')
    .replace(/[\u201c\u201d\u201e\u201f]/gu, '"')
    .replace(/[\u2018\u2019\u201a\u201b]/gu, "'")
    .replace(/[\s\u00a0]+/gu, ' ')
    .trim();

const removeHighlightFragments = (kind) => {
    highlightLayer?.querySelectorAll(`[data-reader-highlight="${kind}"]`)
        .forEach(fragment => fragment.remove());
};

const mergeHighlightRects = (clientRects) => {
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
    const merged = [];
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

const paintHighlightRects = (clientRects, kind) => {
    if (!highlightLayer) return;
    removeHighlightFragments(kind);
    for (const rect of mergeHighlightRects(clientRects)) {
        const fragment = document.createElement('div');
        fragment.className = `reader-highlight-fragment is-${kind}`;
        fragment.dataset.readerHighlight = kind;
        fragment.style.left = `${Math.floor(rect.left)}px`;
        fragment.style.top = `${Math.floor(rect.top)}px`;
        fragment.style.width = `${Math.ceil(rect.right - rect.left)}px`;
        fragment.style.height = `${Math.ceil(rect.bottom - rect.top)}px`;
        highlightLayer.append(fragment);
    }
};

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

const findTextNode = (element) => {
    for (const child of element?.childNodes || []) {
        if (child.nodeType === Node.TEXT_NODE) return child;
        const nested = findTextNode(child);
        if (nested) return nested;
    }
    return null;
};

const segmentClientRects = (segment) => {
    const textDivs = currentTextLayer?.textDivs || [...textLayerElement?.querySelectorAll('span') || []];
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

const caretAtPoint = (x, y) => {
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

const sentenceAtPoint = (x, y, target) => {
    const caret = caretAtPoint(x, y);
    const caretElement = caret?.node?.nodeType === Node.ELEMENT_NODE
        ? caret.node
        : caret?.node?.parentElement;
    const textDiv = caretElement?.closest?.('[data-reader-item-index]')
        || target?.closest?.('[data-reader-item-index]');
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
            || (itemOffset === part.endOffset && part.endOffset === textDiv.textContent.length))));
    return candidates.sort((left, right) => left.sourceText.length - right.sourceText.length)[0] || null;
};

const setStatus = (message) => {
    if (!status) return;
    status.hidden = !message;
    status.textContent = message || '';
};

const setTranslationState = (message, isError = false) => {
    if (!translationState) return;
    translationState.hidden = !message;
    translationState.textContent = message || '';
    translationState.classList.toggle('is-error', Boolean(message) && isError);
};

const updateTranslationControl = (state) => {
    if (!translationToggle) return;
    const language = translationLanguage?.value || 'English';
    const states = {
        off: {
            status: 'Off',
            icon: 'translate',
            title: 'Translation is off. Click to translate this page',
        },
        translating: {
            status: 'Translating…',
            icon: 'progress_activity',
            title: `Translating this page to ${language}…`,
        },
        ready: {
            status: 'On',
            icon: 'check_circle',
            title: `Translation is on in ${language}. Hover over a sentence to see its translation`,
        },
        unavailable: {
            status: 'No text',
            icon: 'block',
            title: 'Translation is unavailable because this page has no selectable text',
        },
        error: {
            status: 'Error',
            icon: 'error',
            title: 'Translation failed. Turn translation off and on to retry',
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

const pdfItemsShareVisualWord = (previousItem, nextItem) => {
    if (!previousItem || !nextItem || previousItem.hasEOL) return false;
    const previousX = Number(previousItem.transform?.[4]);
    const nextX = Number(nextItem.transform?.[4]);
    const previousWidth = Math.abs(Number(previousItem.width));
    const previousY = Number(previousItem.transform?.[5]);
    const nextY = Number(nextItem.transform?.[5]);
    if (![previousX, nextX, previousWidth, previousY, nextY].every(Number.isFinite)) return false;

    const fontHeight = Math.max(
        Math.abs(Number(previousItem.height)) || Math.abs(Number(previousItem.transform?.[3])) || 0,
        Math.abs(Number(nextItem.height)) || Math.abs(Number(nextItem.transform?.[3])) || 0,
        1);
    if (Math.abs(previousY - nextY) > fontHeight * 0.35) return false;

    const horizontalGap = nextX - (previousX + previousWidth);
    return horizontalGap <= Math.max(0.75, fontHeight * 0.12);
};

const separatorBefore = (rawText, nextText, previousItem = null, nextItem = null) => {
    if (!rawText || /[\s\n]$/u.test(rawText) || /^[\s,.;:!?…%)\]}»”’]/u.test(nextText)) return '';
    if (/[([{«“‘]$/u.test(rawText)) return '';
    if (pdfItemsShareVisualWord(previousItem, nextItem)) return '';
    return ' ';
};

const fallbackSentenceSegments = (text) => {
    const results = [];
    const expression = /[^.!?…]+(?:[.!?…]+["'”’»)]*|$)/gu;
    let match;
    while ((match = expression.exec(text)) !== null) {
        if (normalizeText(match[0])) results.push({ segment: match[0], index: match.index });
    }
    return results;
};

const numericItemValue = (item, property, transformIndex) => {
    const direct = Number(item?.[property]);
    if (Number.isFinite(direct) && direct !== 0) return Math.abs(direct);
    const transformed = Number(item?.transform?.[transformIndex]);
    return Number.isFinite(transformed) ? Math.abs(transformed) : 0;
};

const preparePdfLineLayout = (items) => {
    const nextTextItems = new Array(items.length).fill(null);
    let nextTextItem = null;
    for (let index = items.length - 1; index >= 0; index -= 1) {
        nextTextItems[index] = nextTextItem;
        if (typeof items[index]?.str === 'string' && items[index].str.trim()) {
            nextTextItem = items[index];
        }
    }

    const lineStarts = [];
    const lineGaps = [];
    let startsLine = true;
    for (let index = 0; index < items.length; index += 1) {
        const item = items[index];
        if (startsLine && typeof item?.str === 'string' && item.str.trim()) lineStarts.push(item);
        if (typeof item?.str === 'string' && item.str) startsLine = false;
        if (!item?.hasEOL) continue;
        startsLine = true;
        const next = nextTextItems[index];
        const currentY = Number(item?.transform?.[5]);
        const nextY = Number(next?.transform?.[5]);
        const gap = Math.abs(currentY - nextY);
        if (Number.isFinite(gap) && gap > 0) lineGaps.push(gap);
    }

    const lineStartXs = lineStarts
        .map(item => Number(item?.transform?.[4]))
        .filter(Number.isFinite)
        .sort((left, right) => left - right);
    const sortedGaps = lineGaps.sort((left, right) => left - right);
    return {
        nextTextItems,
        baselineLeft: lineStartXs.length
            ? lineStartXs[Math.floor((lineStartXs.length - 1) * 0.15)]
            : 0,
        typicalLineGap: sortedGaps.length
            ? sortedGaps[Math.floor(sortedGaps.length / 2)]
            : 0,
    };
};

const isPdfParagraphBreak = (item, nextItem, layout) => {
    if (!nextItem) return false;
    const currentY = Number(item?.transform?.[5]);
    const nextY = Number(nextItem?.transform?.[5]);
    const verticalGap = Math.abs(currentY - nextY);
    const fontHeight = Math.max(
        numericItemValue(item, 'height', 3),
        numericItemValue(nextItem, 'height', 3),
        1);
    const extraVerticalSpace = Number.isFinite(verticalGap)
        && verticalGap > Math.max(layout.typicalLineGap * 1.4, fontHeight * 1.35);
    const nextX = Number(nextItem?.transform?.[4]);
    const indentedNextLine = Number.isFinite(nextX)
        && nextX > layout.baselineLeft + Math.max(4, fontHeight * 0.7);
    return extraVerticalSpace || indentedNextLine;
};

const buildPageSegments = (textContent) => {
    let rawText = '';
    const itemRanges = [];
    const items = textContent?.items || [];
    const lineLayout = preparePdfLineLayout(items);
    let textDivIndex = 0;
    let previousTextItem = null;
    for (let index = 0; index < items.length; index += 1) {
        const item = items[index];
        if (typeof item?.str !== 'string') continue;
        const itemIndex = textDivIndex;
        textDivIndex += 1;
        const value = item.str;
        if (!value) continue;
        rawText += separatorBefore(rawText, value, previousTextItem, item);
        const start = rawText.length;
        rawText += value;
        itemRanges.push({ index: itemIndex, start, end: rawText.length });
        if (item.hasEOL && lineLayout.nextTextItems[index]) {
            rawText += isPdfParagraphBreak(item, lineLayout.nextTextItems[index], lineLayout)
                ? '\n\n'
                : ' ';
        }
        previousTextItem = item;
    }

    if (!normalizeText(rawText)) return [];
    let candidates;
    if (typeof Intl?.Segmenter === 'function') {
        candidates = [...new Intl.Segmenter(undefined, { granularity: 'sentence' }).segment(rawText)];
    } else {
        candidates = fallbackSentenceSegments(rawText);
    }

    const segments = [];
    for (const candidate of candidates) {
        const rawSegment = candidate.segment || '';
        const sourceText = normalizeText(rawSegment);
        if (!sourceText) continue;
        const start = Number(candidate.index) || 0;
        const end = start + rawSegment.length;
        const paragraphIndex = (rawText.slice(0, start).match(/\n\s*\n/gu) || []).length;
        const overlappingItems = itemRanges
            .filter(range => range.end > start && range.start < end);
        const itemIndices = overlappingItems.map(range => range.index);
        const itemParts = overlappingItems.map(range => ({
            itemIndex: range.index,
            startOffset: Math.max(0, start - range.start),
            endOffset: Math.min(range.end, end) - range.start,
        }));
        segments.push({
            index: segments.length,
            paragraphIndex,
            sourceText,
            itemIndices,
            itemParts,
        });
    }

    return segments;
};

const decorateTextLayer = (textLayer, segments) => {
    const textDivs = textLayer?.textDivs || [...textLayerElement.querySelectorAll('span')];
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

const closeSelectionPopover = () => {
    if (selectionPopover) selectionPopover.hidden = true;
    clearTimeout(hoverCloseTimer);
    hoverCloseTimer = null;
    hoveredSegmentIndex = null;
    popoverMode = null;
    activeLinkedIndices = [];
    textLayerElement?.querySelectorAll('.is-linked-source').forEach(element => element.classList.remove('is-linked-source'));
    removeHighlightFragments('linked');
    translationContent?.querySelectorAll('.is-linked').forEach(element => element.classList.remove('is-linked'));
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

const highlightLinkedSegments = (indices, paintSource = true) => {
    activeLinkedIndices = [...new Set(indices.map(Number))];
    const linkedSourceElements = [];
    textLayerElement?.querySelectorAll('[data-reader-segments]').forEach(element => {
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
    translationContent?.querySelectorAll('[data-translation-segment]').forEach(element => {
        element.classList.toggle('is-linked', activeLinkedIndices.includes(Number(element.dataset.translationSegment)));
    });
};

const translationKey = (pageNumber, language, segments) => {
    const signature = segments.map(segment => segment.sourceText).join('\u241e');
    return `${pageNumber}:${language}:${signature}`;
};

const currentTranslation = () => translationCache.get(
    translationKey(currentPage, translationLanguage?.value || 'English', currentSegments));

const sourceSpeechLanguage = () =>
    currentTranslation()?.detectedSourceLanguage?.trim()
    || readingLanguage
    || '';

const isPolishSpeechLanguage = (language) => {
    const normalized = String(language || '').trim().toLowerCase().replace('_', '-');
    return normalized === 'pl' || normalized === 'pl-pl' || normalized === 'polish';
};

const selectedPolishVoice = () => {
    const value = String(readerVoiceSelect?.value || 'zofia').trim().toLowerCase();
    return POLISH_VOICES.has(value) ? value : 'zofia';
};

const voiceForSpeechLanguage = (language) =>
    isPolishSpeechLanguage(language) ? selectedPolishVoice() : '';

const updateReaderVoiceControl = () => {
    if (!readerVoiceControl) return;
    readerVoiceControl.hidden = !isPolishSpeechLanguage(sourceSpeechLanguage())
        && !isPolishSpeechLanguage(translationLanguage?.value);
};

const restoreReaderVoice = () => {
    if (!readerVoiceSelect) return;
    try {
        const storedVoice = String(localStorage.getItem(POLISH_VOICE_STORAGE_KEY) || '').toLowerCase();
        readerVoiceSelect.value = POLISH_VOICES.has(storedVoice) ? storedVoice : 'zofia';
    } catch {
        readerVoiceSelect.value = 'zofia';
    }
};

const clearReaderTtsStatus = () => {
    clearTimeout(readerTtsStatusTimer);
    readerTtsStatusTimer = null;
    if (!readerTtsStatus) return;
    readerTtsStatus.hidden = true;
    readerTtsStatus.textContent = '';
    readerTtsStatus.classList.remove('is-error');
};

const showReaderTtsStatus = (message, { error = false, transient = false } = {}) => {
    clearTimeout(readerTtsStatusTimer);
    readerTtsStatusTimer = null;
    if (!readerTtsStatus) return;
    readerTtsStatus.textContent = message;
    readerTtsStatus.classList.toggle('is-error', error);
    readerTtsStatus.hidden = !message;
    if (message && transient) {
        readerTtsStatusTimer = setTimeout(clearReaderTtsStatus, 2400);
    }
};

const updateReaderTtsPrompt = () => {
    if (!readerTtsToggle || readerTtsPlaying) return;
    const hasSelection = lastReaderSelection?.pageNumber === currentPage;
    const label = hasSelection ? 'Read selected text aloud' : 'Read this page aloud';
    readerTtsToggle.setAttribute('aria-label', label);
    readerTtsToggle.title = label;
};

const updateReaderTtsButton = (playing) => {
    readerTtsPlaying = playing;
    readerTtsToggle?.classList.toggle('is-active', playing);
    readerTtsToggle?.classList.toggle('is-reading', playing);
    readerTtsToggle?.setAttribute('aria-pressed', String(playing));
    if (readerTtsIcon) readerTtsIcon.textContent = playing ? 'stop_circle' : 'volume_up';
    if (readerTtsLabel) readerTtsLabel.textContent = playing ? 'Stop' : 'Read';
    if (playing) {
        readerTtsToggle?.setAttribute('aria-label', 'Stop reading aloud');
        if (readerTtsToggle) readerTtsToggle.title = 'Stop reading aloud';
    } else {
        updateReaderTtsPrompt();
    }
};

const clearSpeechHighlights = () => {
    removeHighlightFragments('speech');
    translationContent?.querySelectorAll('.is-speaking')
        .forEach(element => element.classList.remove('is-speaking'));
};

const stopReaderTts = () => {
    window.GlosifyTts?.stop();
    clearSpeechHighlights();
    updateReaderTtsButton(false);
};

const splitLongSpeechText = (text) => {
    const chunks = [];
    let remaining = normalizeText(text);
    while (remaining.length > TTS_CHUNK_LENGTH) {
        const candidate = remaining.slice(0, TTS_CHUNK_LENGTH + 1);
        const punctuationBreaks = [
            candidate.lastIndexOf('. '),
            candidate.lastIndexOf('? '),
            candidate.lastIndexOf('! '),
            candidate.lastIndexOf('; '),
            candidate.lastIndexOf(', '),
        ];
        let splitAt = Math.max(...punctuationBreaks);
        if (splitAt >= Math.floor(TTS_CHUNK_LENGTH * 0.45)) splitAt += 1;
        else splitAt = candidate.lastIndexOf(' ');
        if (splitAt < Math.floor(TTS_CHUNK_LENGTH * 0.45)) splitAt = TTS_CHUNK_LENGTH;
        chunks.push(remaining.slice(0, splitAt).trim());
        remaining = remaining.slice(splitAt).trim();
    }
    if (remaining) chunks.push(remaining);
    return chunks;
};

const chunkTextForSpeech = (text) => {
    const normalized = normalizeText(text);
    if (!normalized) return [];
    let sentences;
    if (typeof Intl?.Segmenter === 'function') {
        sentences = [...new Intl.Segmenter(undefined, { granularity: 'sentence' }).segment(normalized)]
            .map(candidate => candidate.segment);
    } else {
        sentences = fallbackSentenceSegments(normalized).map(candidate => candidate.segment);
    }
    return sentences.flatMap(splitLongSpeechText).filter(Boolean);
};

const selectedIndices = (container, selector, attribute, range) =>
    [...container.querySelectorAll(selector)]
        .filter(element => {
            try { return range.intersectsNode(element); } catch { return false; }
        })
        .flatMap(element => (element.getAttribute(attribute) || '')
            .split(' ')
            .filter(Boolean)
            .map(Number))
        .filter(Number.isInteger)
        .filter((value, index, values) => values.indexOf(value) === index);

const compareDomPositions = (leftContainer, leftOffset, rightContainer, rightOffset) => {
    const left = document.createRange();
    left.setStart(leftContainer, leftOffset);
    left.collapse(true);
    const right = document.createRange();
    right.setStart(rightContainer, rightOffset);
    right.collapse(true);
    return left.compareBoundaryPoints(Range.START_TO_START, right);
};

const selectedTextWithinNode = (range, textNode) => {
    const length = textNode.textContent?.length || 0;
    if (!length) return '';
    const selectionEndsBeforeNode = compareDomPositions(
        range.endContainer,
        range.endOffset,
        textNode,
        0) <= 0;
    const selectionStartsAfterNode = compareDomPositions(
        range.startContainer,
        range.startOffset,
        textNode,
        length) >= 0;
    if (selectionEndsBeforeNode || selectionStartsAfterNode) return '';

    const intersection = document.createRange();
    if (compareDomPositions(range.startContainer, range.startOffset, textNode, 0) > 0) {
        intersection.setStart(range.startContainer, range.startOffset);
    } else {
        intersection.setStart(textNode, 0);
    }
    if (compareDomPositions(range.endContainer, range.endOffset, textNode, length) < 0) {
        intersection.setEnd(range.endContainer, range.endOffset);
    } else {
        intersection.setEnd(textNode, length);
    }
    return intersection.toString();
};

const selectedSourceText = (range) => {
    const textDivs = currentTextLayer?.textDivs || [...textLayerElement?.querySelectorAll('span') || []];
    let rawText = '';
    let previousItem = null;
    textDivs.forEach((textDiv, itemIndex) => {
        try {
            if (!range.intersectsNode(textDiv)) return;
        } catch {
            return;
        }
        const textNode = findTextNode(textDiv);
        if (!textNode) return;
        const value = selectedTextWithinNode(range, textNode);
        if (!value) return;
        const item = currentTextItems[itemIndex] || null;
        rawText += separatorBefore(rawText, value, previousItem, item);
        rawText += value;
        previousItem = item;
    });
    return normalizeText(rawText);
};

const captureReaderSelection = () => {
    const selection = window.getSelection();
    if (!selection || selection.isCollapsed || selection.rangeCount === 0) return null;
    const range = selection.getRangeAt(0);
    if (textLayerElement?.contains(range.commonAncestorContainer)) {
        const text = selectedSourceText(range);
        if (!text) return null;
        return {
            pageNumber: currentPage,
            kind: 'source',
            text,
            range: range.cloneRange(),
            indices: selectedIndices(
                textLayerElement,
                '[data-reader-segments]',
                'data-reader-segments',
                range),
        };
    }
    if (translationContent?.contains(range.commonAncestorContainer)) {
        const text = normalizeText(selection.toString());
        if (!text) return null;
        return {
            pageNumber: currentPage,
            kind: 'translation',
            text,
            indices: selectedIndices(
                translationContent,
                '[data-translation-segment]',
                'data-translation-segment',
                range),
        };
    }
    return null;
};

const rememberReaderSelection = () => {
    const snapshot = captureReaderSelection();
    if (!snapshot) return;
    lastReaderSelection = snapshot;
    updateReaderTtsPrompt();
};

const paintSpeechSegment = (segmentIndex) => {
    const segment = currentSegments[segmentIndex];
    if (!segment) return;
    paintHighlightRects(segmentClientRects(segment), 'speech');
    const firstSourceElement = [...textLayerElement.querySelectorAll('[data-reader-segments]')]
        .find(element => (element.dataset.readerSegments || '')
            .split(' ')
            .map(Number)
            .includes(segmentIndex));
    firstSourceElement?.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
};

const buildReaderSpeechQueue = (selection) => {
    if (selection) {
        const language = selection.kind === 'translation'
            ? translationLanguage?.value || ''
            : sourceSpeechLanguage();
        return chunkTextForSpeech(selection.text).map((text, index) => ({
            text,
            lang: language,
            quality: 'hd-supported-v2',
            voice: voiceForSpeechLanguage(language),
            meta: {
                mode: 'selection',
                selectionKind: selection.kind,
                selectionIndices: selection.indices,
                selectionRange: selection.range,
                partIndex: index,
            },
        }));
    }

    const language = sourceSpeechLanguage();
    return currentSegments.flatMap(segment =>
        chunkTextForSpeech(segment.sourceText).map(text => ({
            text,
            lang: language,
            quality: 'hd-supported-v2',
            voice: voiceForSpeechLanguage(language),
            meta: { mode: 'page', segmentIndex: segment.index },
        })));
};

const startReaderTts = () => {
    if (readerTtsPlaying) {
        stopReaderTts();
        return;
    }
    if (!currentSegments.length) {
        showReaderTtsStatus('No selectable text is available to read.', { error: true });
        return;
    }
    if (!window.GlosifyTts?.playQueue) {
        showReaderTtsStatus('Text-to-speech is unavailable in this browser.', { error: true });
        return;
    }

    const liveSelection = captureReaderSelection();
    if (liveSelection) lastReaderSelection = liveSelection;
    const selection = lastReaderSelection?.pageNumber === currentPage
        ? lastReaderSelection
        : null;
    const items = buildReaderSpeechQueue(selection);
    if (!items.length) {
        showReaderTtsStatus('No selectable text is available to read.', { error: true });
        return;
    }

    setStatus('');
    clearSpeechHighlights();
    window.GlosifyTts.playQueue(items, {
        onItemStart: (item, index, total) => {
            if (item.meta?.mode === 'page') {
                paintSpeechSegment(item.meta.segmentIndex);
                showReaderTtsStatus(
                    `Reading page ${currentPage} · sentence ${item.meta.segmentIndex + 1} of ${currentSegments.length}`);
                return;
            }
            removeHighlightFragments('speech');
            if (item.meta?.selectionKind === 'source' && item.meta.selectionRange) {
                try {
                    paintHighlightRects([...item.meta.selectionRange.getClientRects()], 'selection');
                } catch {
                    removeHighlightFragments('selection');
                }
            } else if (item.meta?.selectionKind === 'translation') {
                const indices = item.meta.selectionIndices || [];
                translationContent?.querySelectorAll('[data-translation-segment]').forEach(element => {
                    element.classList.toggle(
                        'is-speaking',
                        indices.includes(Number(element.dataset.translationSegment)));
                });
            }
            showReaderTtsStatus(`Reading selection · part ${index + 1} of ${total}`);
        },
        onStateChange: (state, error) => {
            if (state === 'playing') {
                updateReaderTtsButton(true);
                showReaderTtsStatus(selection ? 'Reading selected text…' : `Reading page ${currentPage}…`);
                return;
            }

            clearSpeechHighlights();
            if (selection?.kind === 'source') paintNativeSelection();
            updateReaderTtsButton(false);
            if (state === 'completed') {
                showReaderTtsStatus(
                    selection ? 'Finished reading the selection.' : `Finished reading page ${currentPage}.`,
                    { transient: true });
            } else if (state === 'stopped') {
                showReaderTtsStatus('Reading stopped.', { transient: true });
            } else if (state === 'error') {
                const message = error?.message || 'Text-to-speech playback failed.';
                showReaderTtsStatus(message, { error: true });
                setStatus(message);
            }
        },
    });
};

const renderTranslation = (result) => {
    if (!translationContent) return;
    translationContent.replaceChildren();
    setTranslationState('');
    if (translationEnabled) updateTranslationControl('ready');
    if (translationHeadingLanguage) translationHeadingLanguage.textContent = result.targetLanguage;
    if (detectedLanguage) {
        detectedLanguage.textContent = result.detectedSourceLanguage
            ? `Detected ${result.detectedSourceLanguage}${result.cached ? ' · cached' : ''}`
            : result.cached ? 'Cached translation' : '';
    }
    updateReaderVoiceControl();

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

const requestJson = async (url, options) => {
    const headers = new Headers(options?.headers || {});
    headers.set('Accept', 'application/json');
    headers.set('Content-Type', 'application/json');
    if (antiforgeryToken) headers.set('RequestVerificationToken', antiforgeryToken);
    const response = await fetch(url, { ...options, headers });
    const payload = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(payload.error || 'The page could not be translated.');
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
        const result = await requestJson(
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
    } catch (error) {
        if (translationEnabled && currentPage === job.pageNumber) {
            updateTranslationControl('error');
            setTranslationState(error?.message || 'The page could not be translated.', true);
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

const renderPage = async (pageNumber) => {
    if (!pdf || !canvas || !pageSurface || !textLayerElement) return;
    const boundedPage = Math.min(Math.max(pageNumber, 1), pdf.numPages);
    const generation = ++renderGeneration;
    stopReaderTts();
    clearReaderTtsStatus();
    lastReaderSelection = null;
    if (readerTtsToggle) readerTtsToggle.disabled = true;
    closeSelectionPopover();
    clearReaderHighlights();
    currentSegments = [];
    currentCanvasRenderTask?.cancel();
    currentTextLayer?.cancel();
    currentTextLayer = null;
    const replacementCanvas = canvas.cloneNode(false);
    canvas.replaceWith(replacementCanvas);
    canvas = replacementCanvas;
    textLayerElement.replaceChildren();
    setStatus('');
    if (translationEnabled) updateTranslationControl('translating');

    try {
        const page = await pdf.getPage(boundedPage);
        if (generation !== renderGeneration) return;
        const wrap = originalPane || canvas.closest('.pdf-canvas-wrap');
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

        currentPage = boundedPage;
        currentTextItems = (textContent.items || [])
            .filter(item => typeof item?.str === 'string');
        currentSegments = buildPageSegments(textContent);
        decorateTextLayer(currentTextLayer, currentSegments);
        indicator.textContent = `Page ${currentPage} of ${pdf.numPages}`;
        prev.disabled = currentPage <= 1;
        next.disabled = currentPage >= pdf.numPages;
        splitViewToggle.disabled = !translationEnabled || currentSegments.length === 0;
        if (readerTtsToggle) readerTtsToggle.disabled = currentSegments.length === 0;
        updateToolbar();
        updateReaderTtsPrompt();
        syncAssistantPage();
        if (currentSegments.length === 0) {
            setStatus('No selectable text found on this page.');
        }
        if (translationEnabled) queueCurrentPageTranslation();
    } catch (error) {
        if (generation !== renderGeneration || error?.name === 'RenderingCancelledException') return;
        if (readerTtsToggle) readerTtsToggle.disabled = true;
        setStatus('This page could not be rendered.');
    }
};

const goToPage = (pageNumber) => {
    if (!pdf) return;
    renderPage(Math.min(Math.max(pageNumber, 1), pdf.numPages));
};

const applyZoom = (newScale) => {
    fitMode = null;
    zoomScale = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, newScale));
    renderPage(currentPage);
};

const setMobileView = (view) => {
    mobileView = view === 'translation' ? 'translation' : 'original';
    workspace?.classList.toggle('mobile-show-translation', mobileView === 'translation');
    workspace?.classList.toggle('mobile-show-original', mobileView === 'original');
    mobileTabs?.querySelectorAll('[data-reader-mobile-view]').forEach(button => {
        button.classList.toggle('is-active', button.dataset.readerMobileView === mobileView);
    });
};

const setSplitView = (enabled) => {
    splitViewEnabled = Boolean(enabled && translationEnabled && currentSegments.length > 0);
    workspace?.classList.toggle('is-split', splitViewEnabled);
    splitViewToggle?.classList.toggle('is-active', splitViewEnabled);
    splitViewToggle?.setAttribute('aria-pressed', String(splitViewEnabled));
    if (translationPane) translationPane.hidden = !splitViewEnabled;
    if (mobileTabs) mobileTabs.hidden = !splitViewEnabled;
    setMobileView('original');
    requestAnimationFrame(() => renderPage(currentPage));
};

const setTranslationEnabled = (enabled) => {
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
    if (!documentId || !translationLanguage) return;
    try {
        const result = await requestJson(`/Books/${documentId}/TranslationPreference`, {
            method: 'PUT',
            body: JSON.stringify({ targetLanguage: translationLanguage.value }),
        });
        shell.dataset.preferredTranslationLanguage = result.targetLanguage;
    } catch (error) {
        setTranslationState(error?.message || 'The language preference could not be saved.', true);
    }
};

const findSelectedSegments = (selectedText, result, preferredIndices = []) => {
    const selected = normalizeText(selectedText);
    if (!selected || !result?.segments?.length) return [];
    const sources = result.segments.map(segment => normalizeText(segment.sourceText));
    let combined = '';
    const ranges = sources.map((source, index) => {
        if (combined) combined += ' ';
        const start = combined.length;
        combined += source;
        return { index, start, end: combined.length };
    });
    const candidates = [];
    let searchFrom = 0;
    while (searchFrom <= combined.length - selected.length) {
        const foundAt = combined.indexOf(selected, searchFrom);
        if (foundAt < 0) break;
        const foundEnd = foundAt + selected.length;
        candidates.push(ranges
            .filter(range => range.end > foundAt && range.start < foundEnd)
            .map(range => result.segments[range.index]));
        searchFrom = foundAt + Math.max(selected.length, 1);
    }
    if (candidates.length) {
        const preferred = new Set(preferredIndices.map(Number));
        return candidates.sort((left, right) => {
            const leftScore = left.filter(segment => preferred.has(segment.index)).length;
            const rightScore = right.filter(segment => preferred.has(segment.index)).length;
            return rightScore - leftScore || left.length - right.length;
        })[0];
    }
    const containing = result.segments
        .filter(segment => normalizeText(segment.sourceText).includes(selected))
        .sort((left, right) => {
            const preferred = new Set(preferredIndices.map(Number));
            return Number(preferred.has(right.index)) - Number(preferred.has(left.index))
                || left.sourceText.length - right.sourceText.length;
        });
    return containing.length ? [containing[0]] : [];
};

const positionPopover = (rect, placement = 'auto') => {
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
    const preferredIndices = [...textLayerElement.querySelectorAll('[data-reader-segments]')]
        .filter(element => range.intersectsNode(element))
        .flatMap(element => (element.dataset.readerSegments || '').split(' ').filter(Boolean).map(Number));
    const matches = findSelectedSegments(selection.toString(), result, preferredIndices);
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

const handleSentenceHover = (event) => {
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
    hoverCloseTimer = null;
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
readerTtsToggle?.addEventListener('click', startReaderTts);
readerVoiceSelect?.addEventListener('change', () => {
    stopReaderTts();
    const voice = selectedPolishVoice();
    readerVoiceSelect.value = voice;
    try { localStorage.setItem(POLISH_VOICE_STORAGE_KEY, voice); } catch { /* Optional preference. */ }
});
translationToggle?.addEventListener('click', () => setTranslationEnabled(!translationEnabled));
splitViewToggle?.addEventListener('click', () => setSplitView(!splitViewEnabled));
translationLanguage?.addEventListener('change', async () => {
    stopReaderTts();
    closeSelectionPopover();
    if (translationHeadingLanguage) translationHeadingLanguage.textContent = translationLanguage.value;
    updateReaderVoiceControl();
    await persistTranslationLanguage();
    if (translationEnabled) queueCurrentPageTranslation();
});
mobileTabs?.addEventListener('click', event => {
    const button = event.target.closest('[data-reader-mobile-view]');
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
    let indices = [];
    if (range && translationContent.contains(range.commonAncestorContainer)) {
        rememberReaderSelection();
        indices = [...translationContent.querySelectorAll('[data-translation-segment]')]
            .filter(element => range.intersectsNode(element))
            .map(element => Number(element.dataset.translationSegment));
    } else {
        const segment = event.target.closest('[data-translation-segment]');
        if (segment) indices = [Number(segment.dataset.translationSegment)];
    }
    if (!indices.length) return;
    highlightLinkedSegments(indices);
    const sourceTarget = [...textLayerElement.querySelectorAll('[data-reader-segments]')]
        .find(element => (element.dataset.readerSegments || '').split(' ').map(Number).includes(indices[0]));
    sourceTarget?.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
});
document.addEventListener('pointerdown', event => {
    if (!textLayerElement?.contains(event.target)
        && !translationContent?.contains(event.target)
        && !readerTtsToggle?.contains(event.target)
        && !readerVoiceControl?.contains(event.target)) {
        lastReaderSelection = null;
        updateReaderTtsPrompt();
    }
    if (!selectionPopover?.hidden
        && !selectionPopover.contains(event.target)
        && !textLayerElement?.contains(event.target)) {
        closeSelectionPopover();
    }
});

window.addEventListener('keydown', (event) => {
    if (event.altKey || event.metaKey || event.shiftKey || isTextInput(event.target)) return;
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
        if (readerTtsPlaying) stopReaderTts();
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

restoreReaderVoice();
updateReaderVoiceControl();

try {
    pdfjs = await import('/lib/pdfjs/pdf.min.mjs');
    pdfjs.GlobalWorkerOptions.workerSrc = '/lib/pdfjs/pdf.worker.min.mjs';
    pdf = await pdfjs.getDocument(`/Books/File/${documentId}`).promise;
    await renderPage(1);
} catch {
    setStatus('The PDF could not be loaded.');
}
