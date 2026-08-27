import test from 'node:test';
import assert from 'node:assert/strict';
import {
    currentBookPageContext,
    matchesOwnedStatus,
    quizOptionText,
    selectedOptionContextLabel,
} from '../Glosify/wwwroot/js/assistant/context-options.js';

test('assistant quiz labels preserve language direction', () => {
    assert.equal(quizOptionText({
        name: 'Market Polish',
        sourceLanguage: 'English',
        targetLanguage: 'Polish',
    }), 'Market Polish (English → Polish)');
});

test('assistant quiz labels identify freestyle without a language arrow', () => {
    assert.equal(quizOptionText({
        name: 'Anatomy',
        sourceLanguage: 'Freestyle',
        targetLanguage: 'Freestyle',
    }), 'Anatomy (Freestyle)');
});

test('assistant sends the visible book page only for the selected book', () => {
    assert.deepEqual(currentBookPageContext({
        pageDocumentId: 'book-on-page',
        currentPage: '7',
        selectedMaterialKind: 'book',
        selectedMaterialId: 'book-on-page',
    }), {
        documentId: 'book-on-page',
        pageNumber: 7,
    });

    assert.equal(currentBookPageContext({
        pageDocumentId: 'book-on-page',
        currentPage: '7',
        selectedMaterialKind: 'book',
        selectedMaterialId: 'another-book',
    }), null);
});

test('assistant preserves a saved out-of-library context label during lazy loading', () => {
    const selector = {
        options: [{
            value: 'saved-quiz',
            textContent: 'Saved quiz (English → Polish)',
            dataset: { contextLabel: 'Saved quiz' },
        }],
    };

    assert.equal(
        selectedOptionContextLabel(selector, 'saved-quiz', 'Home'),
        'Saved quiz');
    assert.equal(
        selectedOptionContextLabel(selector, 'missing-quiz', 'Home'),
        'Home');
});

test('a recovered context request clears only its own stale error', () => {
    assert.equal(matchesOwnedStatus('Context choices failed.', 'Context choices failed.'), true);
    assert.equal(matchesOwnedStatus('Could not save chat context.', 'Context choices failed.'), false);
    assert.equal(matchesOwnedStatus('', null), false);
});
