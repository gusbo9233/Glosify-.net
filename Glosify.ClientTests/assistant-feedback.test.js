import test from 'node:test';
import assert from 'node:assert/strict';
import { feedbackReasons, normalizeFeedback, validClientDuration } from '../Glosify/wwwroot/js/assistant/feedback.js';

test('feedback reason sets preserve the approved analytics codes', () => {
    assert.deepEqual(feedbackReasons.up.map(([code]) => code), [
        'helpful', 'correct', 'clear', 'saved_time', 'tool_worked', 'other',
    ]);
    assert.deepEqual(feedbackReasons.down.map(([code]) => code), [
        'incorrect', 'irrelevant', 'confusing', 'too_slow', 'tool_failed',
        'unsafe_or_inappropriate', 'other',
    ]);
});

test('feedback normalization de-duplicates reasons and removes empty comments', () => {
    assert.deepEqual(normalizeFeedback({
        rating: 'up',
        reasonCodes: ['helpful', 'helpful', 'clear'],
        comment: '',
    }), {
        rating: 'up',
        reasonCodes: ['helpful', 'clear'],
        comment: null,
    });
});

test('client duration validation accepts only the server contract range', () => {
    assert.equal(validClientDuration(0), true);
    assert.equal(validClientDuration(900000), true);
    assert.equal(validClientDuration(-1), false);
    assert.equal(validClientDuration(Number.NaN), false);
    assert.equal(validClientDuration(900001), false);
});
