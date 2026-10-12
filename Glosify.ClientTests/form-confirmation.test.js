import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const scripts = new Map(['site', 'anki', 'quiz-details'].map(name => [
    name,
    readFileSync(new URL(`../Glosify/wwwroot/js/${name}.js`, import.meta.url), 'utf8'),
]));

class Element {
    constructor() {
        this.listeners = new Map();
    }

    addEventListener(type, listener) {
        const listeners = this.listeners.get(type) ?? [];
        listeners.push(listener);
        this.listeners.set(type, listeners);
    }

    querySelector() { return null; }
}

function confirmationPage(pageScript, accepted) {
    const form = new Element();
    form.dataset = { confirm: 'Remove this item?' };
    form.closest = selector => selector === 'form[data-confirm]' ? form : null;

    const document = new Element();
    document.body = { dataset: {} };
    document.querySelectorAll = selector => selector === '[data-confirm]' ? [form] : [];

    const confirmations = [];
    let submissions = 0;
    const context = vm.createContext({
        document,
        Element,
        performance,
        AbortController,
        window: {
            addEventListener() {},
            confirm(message) {
                confirmations.push(message);
                return accepted;
            },
        },
    });
    // Match _AppLayout: shared behavior loads before the page's Scripts section.
    for (const name of ['site', pageScript]) {
        vm.runInContext(scripts.get(name), context, { filename: `${name}.js` });
    }

    return {
        confirmations,
        get submissions() { return submissions; },
        submit() {
            const event = new Event('submit', { bubbles: true, cancelable: true });
            Object.defineProperty(event, 'target', { value: form });
            // Native submit events bubble from the form to the document before
            // their default submission. Retain every listener at both levels.
            for (const target of [form, document]) {
                for (const listener of target.listeners.get('submit') ?? []) {
                    listener(event);
                }
            }
            if (!event.defaultPrevented) submissions += 1;
            return event;
        },
    };
}

for (const pageScript of ['anki', 'quiz-details']) {
    test(`${pageScript} confirms once and allows an accepted submission`, () => {
        const page = confirmationPage(pageScript, true);

        const event = page.submit();

        assert.deepEqual(page.confirmations, ['Remove this item?']);
        assert.equal(event.defaultPrevented, false);
        assert.equal(page.submissions, 1);
    });

    test(`${pageScript} confirms once and prevents a canceled submission`, () => {
        const page = confirmationPage(pageScript, false);

        const event = page.submit();

        assert.deepEqual(page.confirmations, ['Remove this item?']);
        assert.equal(event.defaultPrevented, true);
        assert.equal(page.submissions, 0);
    });
}
