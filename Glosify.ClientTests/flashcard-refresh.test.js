import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const script = readFileSync(new URL('../Glosify/wwwroot/js/flashcard-quiz.js', import.meta.url), 'utf8');

function eventTarget() {
    const listeners = new Map();
    return {
        addEventListener(type, handler) {
            if (!listeners.has(type)) listeners.set(type, new Set());
            listeners.get(type).add(handler);
        },
        removeEventListener(type, handler) { listeners.get(type)?.delete(handler); },
        async dispatch(type, event = {}) {
            await Promise.all([...listeners.get(type) ?? []].map(handler => handler({
                preventDefault() {}, stopPropagation() {}, ...event,
            })));
        },
        listenerCount(type) { return listeners.get(type)?.size ?? 0; },
    };
}

function harness(initiallyPresent = false) {
    const document = eventTarget();
    const window = { ...eventTarget(), matchMedia: () => ({ matches: true }) };
    let current = null;
    let now = 0;
    const requests = [];
    const submissions = [];
    const makeSession = () => {
        const container = { ...eventTarget(), setAttribute() {}, innerHTML: '' };
        const classes = new Set();
        const card = {
            style: {},
            classList: {
                add: (...names) => names.forEach(name => classes.add(name)),
                remove: (...names) => names.forEach(name => classes.delete(name)),
                contains: name => classes.has(name),
                toggle: (name, enabled) => enabled ? classes.add(name) : classes.delete(name),
            },
            closest: selector => selector === '.flashcard' ? card : null,
        };
        const form = {
            action: '/Anki/Rate', method: 'post',
            closest: selector => selector === '[data-flashcard-form]' ? form : null,
            querySelector: () => null,
            requestSubmit(submitter) { submissions.push(container.dispatch('submit', { target: form, submitter })); },
        };
        const good = { name: 'rating', value: 'good', dataset: { ratingKey: '3' }, closest: () => form };
        container.querySelectorAll = selector => selector === '[data-rating-key]' ? [good] : [];
        container.querySelector = selector => {
            if (selector === '[data-card-reveal-form]' || selector === '[data-flashcard-form]') return form;
            if (selector.includes('value="good"')) return good;
            return null;
        };
        return { container, card, form, good };
    };
    const initial = initiallyPresent ? makeSession() : null;
    current = initial?.container ?? null;
    document.querySelector = () => current;
    vm.runInNewContext(script, {
        document, window, performance: { now: () => now },
        FormData: class extends Map { constructor() { super(); } },
        fetch: async (url, options) => {
            requests.push({ url, ...options });
            return { ok: true, text: async () => '<p>Next card</p>' };
        },
    });
    return {
        document, requests, initial, makeSession,
        tick(ms) { now += ms; },
        async refresh(session) { current = session?.container ?? null; await window.dispatch('learning:updated'); },
        async key(key) {
            await document.dispatch('keydown', { key, target: { tagName: 'BODY', closest: () => null } });
            await Promise.all(submissions.splice(0));
        },
        async swipe({ container, card }) {
            const event = { target: card, pointerId: 1, pointerType: 'touch', clientX: 0, clientY: 0 };
            await container.dispatch('pointerdown', event);
            await container.dispatch('pointermove', { ...event, clientX: 100 });
            await container.dispatch('pointerup', event);
            await Promise.all(submissions.splice(0));
        },
    };
}

test('an initially empty page gains keyboard, swipe and AJAX controls after a learning refresh', async () => {
    const h = harness();
    const session = h.makeSession();
    await h.refresh(session);
    await h.key(' ');
    assert.equal(h.requests.length, 1);
    assert.equal(h.requests[0].headers['X-Requested-With'], 'XMLHttpRequest');
    assert.equal(session.container.innerHTML, '<p>Next card</p>');

    session.card.classList.add('is-revealed');
    await h.swipe(session);
    assert.equal(h.requests.length, 2);
    assert.equal(h.requests[1].body.get('rating'), 'good');
});

test('repeated updates preserve the session timer and do not duplicate handlers', async () => {
    const h = harness(true);
    h.tick(600);
    await h.refresh(h.initial);
    await h.refresh(h.initial);
    h.tick(400);
    await h.key('3');

    assert.equal(h.document.listenerCount('keydown'), 1);
    assert.equal(h.initial.container.listenerCount('submit'), 1);
    assert.equal(h.requests.length, 1);
    assert.equal(h.requests[0].body.get('DurationMilliseconds'), '1000');
});

test('replacing or removing a session retires its keyboard listener', async () => {
    const h = harness(true);
    h.tick(1000);
    const next = h.makeSession();
    await h.refresh(next);
    h.tick(200);
    await h.key('3');

    assert.equal(h.requests.length, 1);
    assert.equal(h.initial.container.innerHTML, '');
    assert.equal(next.container.innerHTML, '<p>Next card</p>');
    assert.equal(h.requests[0].body.get('DurationMilliseconds'), '200');
    await h.refresh(null);
    await h.key('3');
    assert.equal(h.requests.length, 1);
    assert.equal(h.document.listenerCount('keydown'), 0);
});
