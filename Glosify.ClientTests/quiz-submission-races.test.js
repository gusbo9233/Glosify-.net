import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const typingQuizScript = readFileSync(
    new URL('../Glosify/wwwroot/js/typing-quiz.js', import.meta.url),
    'utf8');
const flashcardQuizScript = readFileSync(
    new URL('../Glosify/wwwroot/js/flashcard-quiz.js', import.meta.url),
    'utf8');

const deferred = () => {
    let resolve;
    let reject;
    const promise = new Promise((resolvePromise, rejectPromise) => {
        resolve = resolvePromise;
        reject = rejectPromise;
    });
    return { promise, resolve, reject };
};

const classList = () => {
    const values = new Set();
    return {
        add: (...names) => names.forEach(name => values.add(name)),
        remove: (...names) => names.forEach(name => values.delete(name)),
        contains: name => values.has(name),
        toggle: (name, force) => force ? values.add(name) : values.delete(name),
    };
};

const element = () => ({
    attributes: new Map(),
    classList: classList(),
    className: '',
    dataset: {},
    disabled: false,
    hidden: false,
    innerHTML: '',
    style: {},
    textContent: '',
    value: '',
    addEventListener(type, handler) {
        this.listeners ??= {};
        this.listeners[type] = handler;
    },
    focus() {
        this.focusCount = (this.focusCount || 0) + 1;
    },
    setAttribute(name, value) {
        this.attributes.set(name, String(value));
    },
});

const submitEvent = (form, submitter = null) => ({
    submitter,
    target: { closest: () => form },
    preventDefault() {},
});

test('typing quiz allows only one answer request and becomes retryable after failure', async () => {
    const form = element();
    const input = element();
    input.value = 'answer';
    const checkButton = element();
    const feedback = element();
    const firstRequest = deferred();
    const responses = [
        firstRequest.promise,
        Promise.resolve({ ok: false, status: 500, json: async () => null }),
    ];
    let fetchCount = 0;

    const elements = new Map([
        ['[data-typing-form]', form],
        ['[data-answer-input]', input],
        ['[data-prompt]', element()],
        ['[data-feedback]', feedback],
        ['[data-progress-count]', element()],
        ['[data-accuracy]', element()],
        ['[data-progress-fill]', element()],
        ['[data-check-button]', checkButton],
        ['[data-card-label]', element()],
        ['[data-reveal]', element()],
        ['[data-correct-answer]', element()],
        ['[data-example]', element()],
        ['[data-card]', element()],
        ['[data-results]', element()],
    ]);
    const shell = element();
    shell.dataset = {
        initialIndex: '0',
        correctCount: '0',
        incorrectCount: '0',
        totalWords: '2',
        isComplete: 'false',
        sessionId: 'session-id',
        submitUrl: '/typing/submit',
        showUkrainianKeyboard: 'false',
    };
    shell.querySelector = selector => elements.get(selector) ?? null;
    form.querySelector = () => ({ value: 'antiforgery-token' });

    vm.runInNewContext(typingQuizScript, {
        document: { querySelector: () => shell },
        fetch: () => {
            const response = responses[fetchCount];
            fetchCount += 1;
            return response;
        },
        window: { glosifyText: null },
    });

    const firstSubmit = form.listeners.submit(submitEvent(form));
    const duplicateSubmit = form.listeners.submit(submitEvent(form));

    assert.equal(fetchCount, 1);
    assert.equal(checkButton.disabled, true);
    assert.equal(input.disabled, true);
    assert.equal(form.attributes.get('aria-busy'), 'true');

    firstRequest.resolve({ ok: false, status: 500, json: async () => null });
    await Promise.all([firstSubmit, duplicateSubmit]);

    assert.equal(checkButton.disabled, false);
    assert.equal(input.disabled, false);
    assert.equal(form.attributes.get('aria-busy'), 'false');

    await form.listeners.submit(submitEvent(form));
    assert.equal(fetchCount, 2);
});

test('double-clicking next advances once without submitting the new blank answer', async () => {
    const form = element();
    const input = element();
    input.value = 'answer';
    const checkButton = element();
    const elements = new Map([
        ['[data-typing-form]', form],
        ['[data-answer-input]', input],
        ['[data-prompt]', element()],
        ['[data-feedback]', element()],
        ['[data-progress-count]', element()],
        ['[data-accuracy]', element()],
        ['[data-progress-fill]', element()],
        ['[data-check-button]', checkButton],
        ['[data-card-label]', element()],
        ['[data-reveal]', element()],
        ['[data-correct-answer]', element()],
        ['[data-example]', element()],
        ['[data-card]', element()],
        ['[data-results]', element()],
    ]);
    const shell = element();
    shell.dataset = {
        initialIndex: '0',
        correctCount: '0',
        incorrectCount: '0',
        totalWords: '2',
        isComplete: 'false',
        sessionId: 'session-id',
        submitUrl: '/typing/submit',
        showUkrainianKeyboard: 'false',
    };
    shell.querySelector = selector => elements.get(selector) ?? null;
    form.querySelector = () => ({ value: 'antiforgery-token' });

    let fetchCount = 0;
    const animationFrames = [];
    vm.runInNewContext(typingQuizScript, {
        document: { querySelector: () => shell },
        fetch: async () => {
            fetchCount += 1;
            return {
                ok: true,
                json: async () => ({
                    currentIndex: 1,
                    correctCount: 1,
                    incorrectCount: 0,
                    totalWords: 2,
                    nextWord: { prompt: 'next prompt' },
                    isComplete: false,
                    isCorrect: true,
                    correctAnswer: 'answer',
                    exampleSentence: '',
                    exampleTranslation: '',
                }),
            };
        },
        window: {
            glosifyText: null,
            requestAnimationFrame: callback => animationFrames.push(callback),
        },
    });

    await form.listeners.submit(submitEvent(form));
    assert.equal(fetchCount, 1);

    const advance = form.listeners.submit(submitEvent(form));
    const duplicate = form.listeners.submit(submitEvent(form));
    await Promise.all([advance, duplicate]);

    assert.equal(fetchCount, 1);
    assert.equal(checkButton.disabled, true);
    assert.equal(animationFrames.length, 1);

    animationFrames.shift()();
    assert.equal(checkButton.disabled, false);
    assert.equal(input.disabled, false);
});

test('flashcards allow only one rating request and restore controls after failure', async () => {
    const form = element();
    form.action = '/flashcards/rate';
    form.method = 'post';
    form.querySelector = () => null;
    const againButton = element();
    const goodButton = element();
    const firstRequest = deferred();
    const responses = [
        firstRequest.promise,
        Promise.resolve({ ok: false, redirected: false }),
    ];
    let fetchCount = 0;
    const status = element();
    status.hidden = true;

    const container = element();
    container.querySelector = selector => selector === '[data-flashcard-status]' ? status : null;
    container.querySelectorAll = () => [againButton, goodButton];

    class FakeFormData {
        set() {}
    }

    vm.runInNewContext(flashcardQuizScript, {
        document: { querySelector: () => container },
        fetch: () => {
            const response = responses[fetchCount];
            fetchCount += 1;
            return response;
        },
        FormData: FakeFormData,
        window: {
            glosifyText: null,
            location: { href: '' },
            matchMedia: () => ({ matches: false }),
            setTimeout,
        },
    });

    const firstSubmit = container.listeners.submit(submitEvent(form, { name: 'rating', value: 'again' }));
    const duplicateSubmit = container.listeners.submit(submitEvent(form, { name: 'rating', value: 'good' }));

    assert.equal(fetchCount, 1);
    assert.equal(againButton.disabled, true);
    assert.equal(goodButton.disabled, true);
    assert.equal(container.attributes.get('aria-busy'), 'true');

    firstRequest.reject(new Error('network unavailable'));
    await Promise.all([firstSubmit, duplicateSubmit]);

    assert.equal(againButton.disabled, false);
    assert.equal(goodButton.disabled, false);
    assert.equal(container.attributes.get('aria-busy'), 'false');
    assert.equal(status.hidden, false);
    assert.match(status.textContent, /try again/i);

    await container.listeners.submit(submitEvent(form, { name: 'rating', value: 'again' }));
    assert.equal(fetchCount, 2);
});
