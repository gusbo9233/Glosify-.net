import test from 'node:test';
import assert from 'node:assert/strict';
import {
    createLatestRequestGate,
    feedbackFormValues,
    feedbackPanelState,
    feedbackReasons,
    normalizeFeedback,
    validClientDuration,
} from '../Glosify/wwwroot/js/assistant/feedback.js';

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

test('feedback normalization handles an unrated turn and omitted details', () => {
    assert.equal(normalizeFeedback(null), null);
    assert.equal(normalizeFeedback(undefined), null);
    assert.deepEqual(normalizeFeedback({ rating: 'down' }), {
        rating: 'down',
        reasonCodes: [],
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

test('feedback request gate rejects stale save responses', () => {
    const gate = createLatestRequestGate();
    const first = gate.next();
    const second = gate.next();

    assert.equal(gate.isCurrent(first), false);
    assert.equal(gate.isCurrent(second), true);
});

test('no rating shows neither the detail form nor the thanks', () => {
    assert.deepEqual(feedbackPanelState(null, false), {
        showDetails: false,
        showThanks: false,
    });
    assert.deepEqual(feedbackPanelState(null, true), {
        showDetails: false,
        showThanks: false,
    });
});

test('a rating opens the detail form so reasons can be added', () => {
    assert.deepEqual(feedbackPanelState({ rating: 'down' }, false), {
        showDetails: true,
        showThanks: false,
    });
});

// Re-rendering the same open form on success read as the button doing nothing, which is the
// bug this replaces: saving details has to visibly conclude.
test('saving details closes the form and thanks the user', () => {
    assert.deepEqual(feedbackPanelState({ rating: 'down' }, true), {
        showDetails: false,
        showThanks: true,
    });
});

test('the form shows the persisted feedback when there is no draft', () => {
    assert.deepEqual(
        feedbackFormValues({ rating: 'down', reasonCodes: ['incorrect'], comment: 'Stored' }, null),
        { reasonCodes: ['incorrect'], comment: 'Stored' });
});

// A failed save reverts the rating to the last persisted value. The words the user just tried
// to send must not revert with it, or they have to retype them to retry.
test('an unsent draft survives a failed save and outranks the persisted value', () => {
    assert.deepEqual(
        feedbackFormValues(
            { rating: 'down', reasonCodes: ['incorrect'], comment: 'Stored' },
            { reasonCodes: ['too_slow', 'confusing'], comment: 'added phrases to words' }),
        { reasonCodes: ['too_slow', 'confusing'], comment: 'added phrases to words' });
});

test('an empty comment renders as an empty field rather than null', () => {
    assert.deepEqual(
        feedbackFormValues({ rating: 'up', reasonCodes: [], comment: null }, null),
        { reasonCodes: [], comment: '' });
});
