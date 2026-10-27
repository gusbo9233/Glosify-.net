import { normalizeText, fallbackSentenceSegments } from './segments.js';

const TTS_CHUNK_LENGTH = 180;

const splitLongSpeechText = (text: string) => {
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

export const chunkTextForSpeech = (text: string) => {
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
