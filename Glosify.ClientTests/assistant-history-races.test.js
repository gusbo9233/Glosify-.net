import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { createLatestRequestGate } from '../Glosify/wwwroot/js/assistant/feedback.js';
import { isTerminal, isWaiting } from '../Glosify/wwwroot/js/assistant/runs.js';

const source = readFileSync(new URL('../Glosify/wwwroot/js/assistant.js', import.meta.url), 'utf8');
// Execute the production render/reload functions with a minimal DOM and deferred HTTP.
const section = (start, end) => source.slice(source.indexOf(start), source.indexOf(end));
const deferred = () => {
    let resolve, reject;
    const promise = new Promise((done, fail) => { resolve = done; reject = fail; });
    return { promise, resolve, reject };
};
const element = (tagName = 'div') => ({
    tagName, dataset: {}, children: [], listeners: {},
    addEventListener(type, handler) { this.listeners[type] = handler; },
    click() { if (!this.disabled) return this.listeners.click?.(); },
    appendChild(child) { this.children.push(child); return child; },
    append(...children) { this.children.push(...children); },
    replaceChildren(...children) { this.children = children; },
    remove() {},
    get childElementCount() { return this.children.length; },
    set innerHTML(value) { this.children = []; },
    querySelector(selector) { return this.children.find(child => selector === `[data-live-run="${child.dataset.liveRun}"]`); },
    querySelectorAll(selector) {
        if (selector !== 'button') return [];
        return this.children.flatMap(child => [...(child.tagName === 'button' ? [child] : []), ...(child.querySelectorAll?.(selector) || [])]);
    },
});
const harness = () => {
    const transcript = element();
    const selection = { threadId: 'chat', historyLoaded: true };
    const requests = [];
    const discovery = deferred();
    const answers = [];
    const context = vm.createContext({
        transcript, document: { createElement: element }, empty: null,
        historyGate: createLatestRequestGate(), chatSelection: selection, ownsSelection: value => value === selection,
        defaultEmptyText: '', renderEmptyState: element, renderParts() {}, bubble: text => ({ dataset: {}, text }),
        isTerminal, isWaiting, button: element, runStatusText: {}, t: (_key, fallback) => fallback,
        setStatus() {},
        chatHistoryUrl: id => id,
        runs: { discover: () => discovery.promise, answer: (threadId, values) => { const pending = deferred(); answers.push({ ...pending, values }); return pending.promise; } },
        fetch: () => { const pending = deferred(); requests.push(pending); return pending.promise; },
    });
    vm.runInContext([
        section('    const button = (label, className, onClick)', '    const renderUndo ='),
        section('    const renderQuestion =', '    const renderApproval ='),
        section('    const resetTranscript =', '    const getActiveChat ='),
        section('    const renderMessage =', '    const renderFeedback ='),
        section('    const renderRun =', '    const runs ='),
        section('    const loadHistory =', '    const selectCreatedQuiz ='),
        'const callbacks = {' + section('        onUpdate: async run => {', '    });\n\n    const loadHistory') + '};',
        'globalThis.ui = { renderRun, renderMessage, renderQuestion, loadHistory, onUpdate: callbacks.onUpdate };',
    ].join('\n'), context);
    return { ...context.ui, transcript, selection, requests, discovery, answers };
};
const response = messages => ({ ok: true, json: async () => ({ messages }) });
const run = (id, status = 'running') => ({ id, threadId: 'chat', status, parts: [], savedChanges: 0 });

test('a terminal history response cannot erase a newer message and live run', async () => {
    const h = harness();
    h.renderRun(run('A', 'completed'));
    const history = h.loadHistory(h.selection, false);
    h.renderMessage({ id: 'user-B', role: 'user', text: 'Next request' });
    h.renderRun(run('B'));
    h.requests[0].resolve(response([{ id: 'reply-A', role: 'model', text: 'Old result' }]));
    assert.equal(await history, false);
    assert.ok(h.transcript.children.some(row => row.dataset.liveRun === 'B'));
    assert.ok(h.transcript.children.some(row => row.dataset.messageId === 'user-B'));
    assert.equal(h.transcript.children.some(row => row.dataset.messageId === 'reply-A'), false);
});

test('only the latest overlapping history request may rebuild the transcript', async () => {
    const h = harness();
    const older = h.loadHistory(h.selection, false);
    const latest = h.loadHistory(h.selection, false);
    h.requests[1].resolve(response([{ id: 'new', role: 'model', text: 'Latest' }]));
    assert.equal(await latest, true);
    h.requests[0].resolve(response([{ id: 'old', role: 'model', text: 'Stale' }]));
    assert.equal(await older, false);
    assert.ok(h.transcript.children.some(row => row.dataset.messageId === 'new'));
    assert.equal(h.transcript.children.some(row => row.dataset.messageId === 'old'), false);
});

test('delayed run discovery cannot add an older run after a newer render', async () => {
    const h = harness();
    const history = h.loadHistory(h.selection);
    h.requests[0].resolve(response([]));
    assert.equal(await history, true);
    h.renderRun(run('B'));
    h.discovery.resolve(run('A'));
    await Promise.resolve();
    assert.ok(h.transcript.children.some(row => row.dataset.liveRun === 'B'));
    assert.equal(h.transcript.children.some(row => row.dataset.liveRun === 'A'), false);
});

test('stream updates while reopening a chat do not invalidate its initial history load', async () => {
    const h = harness();
    h.selection.historyLoaded = false;
    const history = h.loadHistory(h.selection, false);
    await h.onUpdate(run('existing'));
    h.requests[0].resolve(response([{ id: 'saved', role: 'model', text: 'Saved reply' }]));
    assert.equal(await history, true);
    assert.ok(h.transcript.children.some(row => row.dataset.messageId === 'saved'));
});

test('answering disables all question options and a failure permits a new choice', async () => {
    const h = harness();
    const card = h.renderQuestion({ threadId: 'chat', question: { question: 'Choose', options: ['First', 'Second'], multiple: false } });
    const buttons = card.querySelectorAll('button');
    const first = buttons[0].click();
    assert.ok(buttons.every(button => button.disabled));
    await buttons[1].listeners.click(); // Even an already queued event cannot submit twice.
    assert.equal(h.answers.length, 1);
    assert.equal(h.answers[0].values[0], 'First');
    h.answers[0].reject(new Error('Temporary failure'));
    await first;
    assert.ok(buttons.every(button => !button.disabled));
    const retry = buttons[1].click();
    assert.equal(h.answers.length, 2);
    assert.equal(h.answers[1].values[0], 'Second');
    h.answers[1].resolve({});
    await retry;
    assert.ok(buttons.every(button => button.disabled));
});
