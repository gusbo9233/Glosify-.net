import test from 'node:test';
import assert from 'node:assert/strict';
import {
    buildExternalRepairPrompt,
    previewMatches,
    problemMessages
} from '../Glosify/wwwroot/js/quiz-json-import.js';

test('validation problems become path-aware messages', () => {
    assert.deepEqual(problemMessages({
        errors: {
            '$.quizzes[0].name': ['Quiz name is required.'],
            '$.version': ['Only version 1 is supported.']
        }
    }), [
        '$.quizzes[0].name: Quiz name is required.',
        '$.version: Only version 1 is supported.'
    ]);
});

test('problem detail is used when field errors are unavailable', () => {
    assert.deepEqual(problemMessages({ detail: 'Insufficient credits.' }), ['Insufficient credits.']);
});

test('editing canonical JSON invalidates its preview', () => {
    assert.equal(previewMatches('{"version":1}', '{"version":1}'), true);
    assert.equal(previewMatches('{"version":1}', '{"version":2}'), false);
    assert.equal(previewMatches(null, '{"version":1}'), false);
});

test('external repair prompt carries errors and content without provider coupling', () => {
    const prompt = buildExternalRepairPrompt('{ bad json }', ['$.version: Missing.']);
    assert.match(prompt, /Glosify version 1/);
    assert.match(prompt, /\$\.version: Missing\./);
    assert.match(prompt, /\{ bad json \}/);
});
