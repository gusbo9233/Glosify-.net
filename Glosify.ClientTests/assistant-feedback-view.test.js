import test from 'node:test';
import assert from 'node:assert/strict';
import { setImmediate as tick } from 'node:timers/promises';
import { createFeedbackView } from './build/assistant/feedback-view.js';

function element(tagName) {
    return {
        tagName, children: [], attributes: {}, listeners: {},
        classList: { toggle() {} },
        append(...children) { this.children.push(...children); },
        appendChild(child) { this.children.push(child); return child; },
        setAttribute(name, value) { this.attributes[name] = value; },
        addEventListener(name, listener) { this.listeners[name] = listener; },
        click() { if (!this.disabled) return this.listeners.click?.(); },
        set innerHTML(value) { assert.equal(value, ''); this.children = []; },
        querySelectorAll(selector) {
            assert.equal(selector, 'input:checked');
            return descendants(this).filter(child => child.tagName === 'input' && child.checked);
        },
    };
}
const descendants = root => root.children.flatMap(child => [child, ...descendants(child)]);
const find = (root, className) => descendants(root).find(child => child.className === className);
globalThis.document = { createElement: element };

test('feedback failure keeps the draft; a successful retry acknowledges the saved details', async () => {
    let fail = true;
    const writes = [], errors = [];
    const view = createFeedbackView({ rating: 'down', comment: 'Stored' }, {
        put: async next => { writes.push(next); if (fail) throw new Error('Offline'); return next; },
        remove: async () => {}, onError: error => errors.push(error),
    });
    const comment = find(view, 'assistant-feedback-comment');
    const save = descendants(view).find(child => child.textContent === 'Save details');
    const reason = descendants(view).find(child => child.tagName === 'input' && child.value === 'incorrect');
    comment.value = 'Keep my explanation';
    reason.checked = true;
    await save.click();
    assert.equal(comment.value, 'Keep my explanation');
    assert.equal(find(view, 'assistant-feedback-details').hidden, false);
    assert.equal(find(view, 'assistant-feedback-note').hidden, true);
    assert.equal(save.disabled, false);
    assert.equal(errors[0].message, 'Offline');
    fail = false;
    await save.click();
    assert.deepEqual(writes[1], { rating: 'down', reasonCodes: ['incorrect'], comment: 'Keep my explanation' });
    assert.equal(find(view, 'assistant-feedback-details').hidden, true);
    assert.equal(find(view, 'assistant-feedback-note').hidden, false);
});

test('clearing while a vote is saving stays cleared after the older save completes', async () => {
    let finishVote;
    const operations = [];
    const view = createFeedbackView(null, {
        put: next => { operations.push('vote'); return new Promise(resolve => { finishVote = () => resolve(next); }); },
        remove: async () => { operations.push('clear'); },
        onError: error => { throw error; },
    });
    const up = descendants(view).find(child => child.attributes['aria-label'] === 'Helpful');
    up.click();
    await tick();
    const cleared = find(view, 'assistant-feedback-clear').click();
    assert.deepEqual(operations, ['vote']);
    finishVote();
    await cleared;
    assert.deepEqual(operations, ['vote', 'clear']);
    assert.equal(up.attributes['aria-pressed'], 'false');
    assert.equal(find(view, 'assistant-feedback-details').hidden, true);
    assert.equal(find(view, 'assistant-feedback-note').hidden, true);
});
