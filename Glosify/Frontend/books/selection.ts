import { normalizeText, separatorBefore, type PdfTextItem } from './segments.js';
import type { PdfTextLayer } from './pdf-library.js';

export interface ReaderSelection {
    pageNumber: number; kind: 'source' | 'translation'; text: string; indices: number[]; range?: Range;
}
interface SelectionOptions {
    textLayerElement: HTMLElement | null; translationContent: HTMLElement | null;
    getTextLayer(): PdfTextLayer | null; getTextItems(): PdfTextItem[]; getPage(): number;
}

export const findTextNode = (element: Node | null | undefined): Text | null => {
    for (const child of element?.childNodes || []) {
        if (child.nodeType === Node.TEXT_NODE) return child as Text;
        const nested = findTextNode(child);
        if (nested) return nested;
    }
    return null;
};

export function createReaderSelection({ textLayerElement, translationContent, getTextLayer, getTextItems, getPage }: SelectionOptions) {
    const selectedIndices = (container: HTMLElement, selector: string, attribute: string, range: Range) =>
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

    const compareDomPositions = (leftContainer: Node, leftOffset: number, rightContainer: Node, rightOffset: number) => {
        const left = document.createRange();
        left.setStart(leftContainer, leftOffset);
        left.collapse(true);
        const right = document.createRange();
        right.setStart(rightContainer, rightOffset);
        right.collapse(true);
        return left.compareBoundaryPoints(Range.START_TO_START, right);
    };

    const selectedTextWithinNode = (range: Range, textNode: Text) => {
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

    const selectedSourceText = (range: Range) => {
        const textDivs = getTextLayer()?.textDivs || [...textLayerElement?.querySelectorAll('span') || []];
        let rawText = '';
        let previousItem: PdfTextItem | null = null;
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
            const item = getTextItems()[itemIndex] || null;
            rawText += separatorBefore(rawText, value, previousItem, item);
            rawText += value;
            previousItem = item;
        });
        return normalizeText(rawText);
    };

    const captureReaderSelection = (): ReaderSelection | null => {
        const selection = window.getSelection();
        if (!selection || selection.isCollapsed || selection.rangeCount === 0) return null;
        const range = selection.getRangeAt(0);
        if (textLayerElement?.contains(range.commonAncestorContainer)) {
            const text = selectedSourceText(range);
            if (!text) return null;
            return {
                pageNumber: getPage(),
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
                pageNumber: getPage(),
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

    return { selectedSourceText, captureReaderSelection };
}
