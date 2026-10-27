import { text as t } from '../shared/localization.js';
import { chunkTextForSpeech } from './speech-chunks.js';
import type { PageSegment } from './segments.js';
import type { ReaderSelection } from './selection.js';

type ReaderSpeechMetadata = { mode: 'page'; segmentIndex: number } | {
    mode: 'selection'; selectionKind: ReaderSelection['kind']; selectionIndices: number[]; selectionRange?: Range; partIndex: number;
};
interface ReaderSpeechItem { text: string; lang: string; meta: ReaderSpeechMetadata; }
interface SpeechOptions {
    page(): { pageNumber: number; segments: PageSegment[]; documentId?: string; readingLanguage: string; detectedLanguage?: string | null };
    captureReaderSelection(): ReaderSelection | null;
    setStatus(message: string): void;
    paintHighlightRects(rects: DOMRect[], kind: string): void;
    removeHighlightFragments(kind: string): void;
    segmentClientRects(segment: PageSegment): DOMRect[];
    paintNativeSelection(): void;
}
export function createReaderSpeech({ page, captureReaderSelection, setStatus, paintHighlightRects, removeHighlightFragments, segmentClientRects, paintNativeSelection }: SpeechOptions) {
    const readerTtsToggle = document.querySelector<HTMLButtonElement>('[data-reader-tts]');
    const readerTtsIcon = document.querySelector<HTMLElement>('[data-reader-tts-icon]');
    const readerTtsLabel = document.querySelector<HTMLElement>('[data-reader-tts-label]');
    const readerTtsStatus = document.querySelector<HTMLElement>('[data-reader-tts-status]');
    const readerSpeechSettings = document.querySelector<HTMLElement>('[data-reader-speech-settings]');
    const readerSpeechInfo = document.querySelector<HTMLElement>('[data-reader-speech-info]');
    const speechDialog = document.querySelector<HTMLDialogElement>('[data-speech-dialog]');
    const translationLanguage = document.querySelector<HTMLSelectElement>('[data-translation-language]');
    const translationContent = document.querySelector<HTMLElement>('[data-translation-content]');
    const textLayerElement = document.querySelector<HTMLElement>('[data-pdf-text-layer]');
    let speechInfoRevision = 0;
    let readerTtsPlaying = false;
    let readerTtsStatusTimer: number | undefined;
    let lastReaderSelection: ReaderSelection | null = null;
    let preserveReaderSelection = false;
    const sourceSpeechLanguage = () =>
        window.GlosifyTts?.getBookLanguage(page().documentId || '')
        || page().detectedLanguage?.trim()
        || page().readingLanguage
        || '';


    const clearReaderTtsStatus = () => {
        clearTimeout(readerTtsStatusTimer);
        readerTtsStatusTimer = undefined;
        if (!readerTtsStatus) return;
        readerTtsStatus.hidden = true;
        readerTtsStatus.textContent = '';
        readerTtsStatus.classList.remove('is-error');
    };

    const showReaderTtsStatus = (message: string, { error = false, transient = false } = {}) => {
        clearTimeout(readerTtsStatusTimer);
        readerTtsStatusTimer = undefined;
        if (!readerTtsStatus) return;
        readerTtsStatus.textContent = message;
        readerTtsStatus.classList.toggle('is-error', error);
        readerTtsStatus.hidden = !message;
        if (message && transient) {
            readerTtsStatusTimer = setTimeout(clearReaderTtsStatus, 2400);
        }
    };

    const refreshSpeechInfo = async () => {
        if (!readerSpeechInfo || !window.GlosifyTts?.estimateQueue) return;
        const revision = ++speechInfoRevision;
        const selection = lastReaderSelection?.pageNumber === page().pageNumber ? lastReaderSelection : null;
        const label = await window.GlosifyTts.estimateQueue(buildReaderSpeechQueue(selection));
        if (revision === speechInfoRevision) readerSpeechInfo.textContent = label;
    };

    const updateReaderTtsPrompt = () => {
        refreshSpeechInfo();
        if (!readerTtsToggle || readerTtsPlaying) return;
        const hasSelection = lastReaderSelection?.pageNumber === page().pageNumber;
        const label = hasSelection ? 'Read selected text aloud' : 'Read this page aloud';
        readerTtsToggle.setAttribute('aria-label', label);
        readerTtsToggle.title = label;
    };

    const updateReaderTtsButton = (playing: boolean) => {
        readerTtsPlaying = playing;
        readerTtsToggle?.classList.toggle('is-active', playing);
        readerTtsToggle?.classList.toggle('is-reading', playing);
        readerTtsToggle?.setAttribute('aria-pressed', String(playing));
        if (readerTtsIcon) readerTtsIcon.textContent = playing ? 'stop_circle' : 'volume_up';
        if (readerTtsLabel) readerTtsLabel.textContent = playing
            ? t('Client.Stop', 'Stop')
            : t('Client.Read', 'Read');
        if (playing) {
            readerTtsToggle?.setAttribute('aria-label', t('Client.Stop', 'Stop'));
            if (readerTtsToggle) readerTtsToggle.title = t('Client.Stop', 'Stop');
        } else {
            updateReaderTtsPrompt();
        }
    };

    const clearSpeechHighlights = () => {
        removeHighlightFragments('speech');
        translationContent?.querySelectorAll('.is-reading')
            .forEach(element => element.classList.remove('is-reading'));
    };

    const stopReaderTts = () => {
        window.GlosifyTts?.stop();
        clearSpeechHighlights();
        updateReaderTtsButton(false);
    };

    const rememberReaderSelection = () => {
        const snapshot = captureReaderSelection();
        // Speech controls can collapse the native selection while taking focus.
        if (!snapshot && preserveReaderSelection) return;
        lastReaderSelection = snapshot;
        updateReaderTtsPrompt();
    };

    const paintSpeechSegment = (segmentIndex: number) => {
        const segment = page().segments[segmentIndex];
        if (!segment) return;
        paintHighlightRects(segmentClientRects(segment), 'speech');
        const firstSourceElement = [...textLayerElement?.querySelectorAll<HTMLElement>('[data-reader-segments]') || []]
            .find(element => (element.dataset.readerSegments || '')
                .split(' ')
                .map(Number)
                .includes(segmentIndex));
        firstSourceElement?.scrollIntoView({ block: 'center', inline: 'center', behavior: 'smooth' });
    };

    const buildReaderSpeechQueue = (selection: ReaderSelection | null): ReaderSpeechItem[] => {
        if (selection) {
            const language = selection.kind === 'translation'
                ? translationLanguage?.value || ''
                : sourceSpeechLanguage();
            return chunkTextForSpeech(selection.text).map((text, index) => ({
                text,
                lang: language,
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
        return page().segments.flatMap(segment =>
            chunkTextForSpeech(segment.sourceText).map(text => ({
                text,
                lang: language,
                meta: { mode: 'page', segmentIndex: segment.index },
            })));
    };

    const startReaderTts = () => {
        if (readerTtsPlaying) {
            stopReaderTts();
            return;
        }
        if (!page().segments.length) {
            showReaderTtsStatus(t('Client.TtsNoText', 'No selectable text is available to read.'), { error: true });
            return;
        }
        if (!window.GlosifyTts?.playSavedQueue) {
            showReaderTtsStatus(t('Client.TtsUnavailable', 'Text-to-speech is unavailable in this browser.'), { error: true });
            return;
        }

        const liveSelection = captureReaderSelection();
        if (liveSelection) lastReaderSelection = liveSelection;
        const selection = lastReaderSelection?.pageNumber === page().pageNumber
            ? lastReaderSelection
            : null;
        const items = buildReaderSpeechQueue(selection);
        if (!items.length) {
            showReaderTtsStatus(t('Client.TtsNoText', 'No selectable text is available to read.'), { error: true });
            return;
        }

        setStatus('');
        clearSpeechHighlights();
        window.GlosifyTts.playSavedQueue(items, {
            onItemStart: (item, index, total) => {
                const meta = item.meta as ReaderSpeechMetadata;
                if (meta?.mode === 'page') {
                    paintSpeechSegment(meta.segmentIndex);
                    showReaderTtsStatus(t('Reader.ReadingSentence', 'Reading page {0} · sentence {1} of {2}', page().pageNumber, meta.segmentIndex + 1, page().segments.length));
                    return;
                }
                removeHighlightFragments('speech');
                if (meta?.selectionKind === 'source' && meta.selectionRange) {
                    try {
                        paintHighlightRects([...meta.selectionRange.getClientRects()], 'selection');
                    } catch {
                        removeHighlightFragments('selection');
                    }
                } else if (meta?.selectionKind === 'translation') {
                    const indices = meta.selectionIndices || [];
                    translationContent?.querySelectorAll<HTMLElement>('[data-translation-segment]').forEach(element => {
                        element.classList.toggle(
                            'is-reading',
                            indices.includes(Number(element.dataset.translationSegment)));
                    });
                }
                showReaderTtsStatus(t('Reader.ReadingSelectionPart', 'Reading selection · part {0} of {1}', index + 1, total));
            },
            onStateChange: (state) => {
                if (state === 'playing') {
                    updateReaderTtsButton(true);
                    showReaderTtsStatus(selection
                        ? t('Reader.ReadingSelection', 'Reading selected text…')
                        : t('Reader.ReadingPage', 'Reading page {0}…', page().pageNumber));
                    return;
                }

                clearSpeechHighlights();
                if (selection?.kind === 'source') paintNativeSelection();
                updateReaderTtsButton(false);
                if (state === 'completed') {
                    showReaderTtsStatus(
                        selection
                            ? t('Reader.FinishedSelection', 'Finished reading the selection.')
                            : t('Reader.FinishedPage', 'Finished reading page {0}.', page().pageNumber),
                        { transient: true });
                } else if (state === 'stopped') {
                    showReaderTtsStatus(t('Reader.ReadingStopped', 'Reading stopped.'), { transient: true });
                } else if (state === 'error') {
                    const message = t('Client.TtsFailed', 'Text-to-speech playback failed.');
                    showReaderTtsStatus(message, { error: true });
                    setStatus(message);
                }
            },
        }, selection?.kind === 'translation' ? {} : { bookId: page().documentId, lang: sourceSpeechLanguage() });
    };

    readerTtsToggle?.addEventListener('click', startReaderTts);
    readerSpeechSettings?.addEventListener('click', () => {
        const selection = lastReaderSelection?.pageNumber === page().pageNumber ? lastReaderSelection : null;
        window.GlosifyTts?.openSettings(selection?.kind === 'translation'
            ? { lang: translationLanguage?.value || '' }
            : { lang: sourceSpeechLanguage(), bookId: page().documentId });
    });
    document.addEventListener('glosify:speech-settings-changed', refreshSpeechInfo);
    const preserveSelection = (target: EventTarget | null, clear = false) => {
        const node = target as Node | null;
        preserveReaderSelection = Boolean(readerTtsToggle?.contains(node)
            || readerSpeechSettings?.contains(node)
            || (target as Element | null)?.closest?.('[data-speech-settings]')
            || speechDialog?.contains(node));
        if (clear && !preserveReaderSelection) {
            lastReaderSelection = null;
            updateReaderTtsPrompt();
        }
    };
    return {
        get playing() { return readerTtsPlaying; },
        clearSelection() { lastReaderSelection = null; },
        preserveSelection, rememberReaderSelection, startReaderTts, stopReaderTts,
        clearReaderTtsStatus, updateReaderTtsPrompt, refreshSpeechInfo,
    };
}
