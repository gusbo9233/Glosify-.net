import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { loadBrowserModule } from './browser-module.js';
import { chunkTextForSpeech } from './build/books/speech-chunks.js';

test('reader speech preserves sentence boundaries and limits long passages to 180 characters', () => {
    assert.deepEqual(chunkTextForSpeech('First sentence. Second sentence!'), ['First sentence.', 'Second sentence!']);
    const text = 'A long passage with many words '.repeat(30).trim();
    const chunks = chunkTextForSpeech(text);
    assert.ok(chunks.length > 1);
    assert.ok(chunks.every(chunk => chunk.length > 0 && chunk.length <= 180));
    assert.equal(chunks.join(' '), text);
    assert.deepEqual(chunkTextForSpeech('  \n  '), []);
});

test('reader speech retains sentence splitting when Intl.Segmenter is unavailable', async () => {
    const { chunkTextForSpeech } = await loadBrowserModule(new URL('./build/books/speech-chunks.js', import.meta.url), vm.createContext({ Intl: {} }));
    assert.deepEqual(Array.from(chunkTextForSpeech('First sentence. Second sentence!')), ['First sentence.', 'Second sentence!']);
});
