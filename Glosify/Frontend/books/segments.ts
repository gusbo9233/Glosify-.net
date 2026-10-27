export interface PdfTextItem {
    str?: string;
    hasEOL?: boolean;
    width?: number;
    height?: number;
    transform?: number[];
}

export interface PageSegment {
    index: number;
    paragraphIndex: number;
    sourceText: string;
    sourceSeparator: string;
    itemIndices: number[];
    itemParts: { itemIndex: number; startOffset: number; endOffset: number }[];
}

interface Sentence { segment: string; index: number; }
type Segmentation = { Segmenter?: typeof Intl.Segmenter };

export const normalizeText = (value: string | null | undefined) => (value || '')
    .normalize('NFKC')
    .replace(/[\u2010-\u2015\u2212]/gu, '-')
    .replace(/[\u201c\u201d\u201e\u201f]/gu, '"')
    .replace(/[\u2018\u2019\u201a\u201b]/gu, "'")
    .replace(/[\s\u00a0]+/gu, ' ')
    .trim();

const pdfItemsShareVisualWord = (previousItem: PdfTextItem | null, nextItem: PdfTextItem | null) => {
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

export const separatorBefore = (rawText: string, nextText: string, previousItem: PdfTextItem | null = null, nextItem: PdfTextItem | null = null) => {
    if (!rawText || /[\s\n]$/u.test(rawText) || /^[\s,.;:!?…%)\]}»”’]/u.test(nextText)) return '';
    if (/[([{«“‘]$/u.test(rawText)) return '';
    if (pdfItemsShareVisualWord(previousItem, nextItem)) return '';
    return ' ';
};

export const fallbackSentenceSegments = (text: string) => {
    const results = [];
    const expression = /[^.!?…]+(?:[.!?…]+["'”’»)]*|$)/gu;
    let match;
    while ((match = expression.exec(text)) !== null) {
        if (normalizeText(match[0])) results.push({ segment: match[0], index: match.index });
    }
    return results;
};

// Keep aligned with BookPageTranslationService.MaxSegmentCharacters.
const MAX_TRANSLATION_SEGMENT_CHARACTERS = 2000;
const splitTranslationCandidate = (candidate: Sentence, intl: Segmentation) => {
    const text = candidate.segment || '';
    if (normalizeText(text).length <= MAX_TRANSLATION_SEGMENT_CHARACTERS) return [candidate];

    const graphemes = typeof intl?.Segmenter === 'function'
        ? [...new intl.Segmenter(undefined, { granularity: 'grapheme' }).segment(text)].map(value => value.segment)
        // Older browsers: keep combining marks, emoji modifiers, flags, and ZWJ
        // sequences together instead of splitting their constituent code points.
        : [...text.matchAll(/\p{Regional_Indicator}{2}|\P{M}(?:\p{M}|\p{Emoji_Modifier})*(?:\u200d\P{M}(?:\p{M}|\p{Emoji_Modifier})*)*|\p{M}+/gu)].map(value => value[0]);
    const units = new Map<number, string>();
    let offset = 0;
    for (const grapheme of graphemes) {
        // A pathological grapheme can itself exceed the API limit. Only then
        // fall back to code points, still retaining complete surrogate pairs.
        const boundedUnits = grapheme.normalize('NFKC').length > MAX_TRANSLATION_SEGMENT_CHARACTERS
            ? Array.from(grapheme)
            : [grapheme];
        for (const unit of boundedUnits) {
            units.set(offset, unit);
            offset += unit.length;
        }
    }

    const pieces = [];
    let start = 0;
    while (start < text.length) {
        let end = start;
        let normalizedLength = 0;
        let wordBoundary = start;
        while (end < text.length) {
            const unit = units.get(end)!;
            // NFKC can expand PDF characters or compose a base and its marks.
            const length = unit.normalize('NFKC').length;
            if (normalizedLength + length > MAX_TRANSLATION_SEGMENT_CHARACTERS) break;
            normalizedLength += length;
            end += unit.length;
            if (/\s$/u.test(unit)) wordBoundary = end;
        }
        if (end < text.length && wordBoundary > start + (end - start) / 2) end = wordBoundary;
        // Keep a complete paragraph separator with the preceding piece. Trailing
        // whitespace is trimmed from sourceText and does not use its budget.
        while (end < text.length && /^\s+$/u.test(units.get(end)!)) end += units.get(end)!.length;
        pieces.push({ segment: text.slice(start, end), index: (Number(candidate.index) || 0) + start });
        start = end;
    }
    return pieces;
};

const numericItemValue = (item: PdfTextItem | null, property: 'width' | 'height', transformIndex: number) => {
    const direct = Number(item?.[property]);
    if (Number.isFinite(direct) && direct !== 0) return Math.abs(direct);
    const transformed = Number(item?.transform?.[transformIndex]);
    return Number.isFinite(transformed) ? Math.abs(transformed) : 0;
};

const preparePdfLineLayout = (items: PdfTextItem[]) => {
    const nextTextItems: (PdfTextItem | null)[] = new Array(items.length).fill(null);
    let nextTextItem: PdfTextItem | null = null;
    for (let index = items.length - 1; index >= 0; index -= 1) {
        nextTextItems[index] = nextTextItem;
        const item = items[index];
        if (typeof item?.str === 'string' && item.str.trim()) {
            nextTextItem = item;
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

const isPdfParagraphBreak = (item: PdfTextItem, nextItem: PdfTextItem | null, layout: ReturnType<typeof preparePdfLineLayout>) => {
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

export const buildPageSegments = (textContent: { items?: PdfTextItem[] } | null | undefined, intl: Segmentation = Intl): PageSegment[] => {
    let rawText = '';
    const itemRanges = [];
    const items = textContent?.items || [];
    const lineLayout = preparePdfLineLayout(items);
    let textDivIndex = 0;
    let previousTextItem: PdfTextItem | null = null;
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
    // A translation segment belongs to one paragraph, even when a paragraph has
    // no sentence punctuation. Keep delimiters in the preceding source range.
    const paragraphs = [];
    let paragraphStart = 0;
    for (const delimiter of rawText.matchAll(/\n\s*\n/gu)) {
        const end = delimiter.index + delimiter[0].length;
        paragraphs.push({ text: rawText.slice(paragraphStart, end), index: paragraphStart });
        paragraphStart = end;
    }
    if (paragraphStart < rawText.length) paragraphs.push({ text: rawText.slice(paragraphStart), index: paragraphStart });
    const sentenceSegmenter = typeof intl?.Segmenter === 'function'
        ? new intl.Segmenter(undefined, { granularity: 'sentence' })
        : null;
    const candidates = paragraphs.flatMap(paragraph => {
        const sentences = sentenceSegmenter
            ? [...sentenceSegmenter.segment(paragraph.text)]
            : fallbackSentenceSegments(paragraph.text);
        const contentSentences = sentences.filter(sentence => normalizeText(sentence.segment));
        return contentSentences.map((sentence, index) => {
            // Intl can emit a paragraph's final newline as a whitespace-only
            // sentence. Include those source characters with the preceding text.
            const start = index === 0 ? 0 : sentence.index;
            const end = contentSentences[index + 1]?.index ?? paragraph.text.length;
            return { segment: paragraph.text.slice(start, end), index: paragraph.index + start };
        });
    });

    const segments: PageSegment[] = [];
    let previousEnd = 0;
    let previousEndsWithSpace = false;
    for (const candidate of candidates.flatMap(candidate => splitTranslationCandidate(candidate, intl))) {
        const rawSegment = candidate.segment || '';
        const sourceText = normalizeText(rawSegment);
        if (!sourceText) continue;
        const start = Number(candidate.index) || 0;
        const end = start + rawSegment.length;
        const normalizedRawSegment = rawSegment.normalize('NFKC');
        const sourceSeparator = segments.length > 0 && (previousEndsWithSpace
            || /^\s/u.test(normalizedRawSegment)
            || /\s/u.test(rawText.slice(previousEnd, start).normalize('NFKC'))) ? ' ' : '';
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
            sourceSeparator,
            itemIndices,
            itemParts,
        });
        previousEnd = end;
        previousEndsWithSpace = /\s$/u.test(normalizedRawSegment);
    }

    return segments;
};

export const findSelectedSegments = <T extends Pick<PageSegment, 'index' | 'sourceText'>>(selectedText: string, result: { segments: T[] } | null | undefined, preferredIndices: number[] = [], currentSegments: Pick<PageSegment, 'sourceSeparator'>[] = []): T[] => {
    const selected = normalizeText(selectedText);
    if (!selected || !result?.segments?.length) return [];
    const sources = result.segments.map(segment => normalizeText(segment.sourceText));
    let combined = '';
    const ranges = sources.map((source, index) => {
        if (combined) combined += currentSegments[result.segments[index].index]?.sourceSeparator ?? ' ';
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
