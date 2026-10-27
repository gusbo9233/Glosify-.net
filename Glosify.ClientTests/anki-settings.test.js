import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const script = readFileSync(new URL('../Glosify/wwwroot/js/anki.js', import.meta.url), 'utf8');
function page({ empty = false, storage = new Map(), confirm = () => true, respond } = {}) {
    const element = (extra = {}) => ({ dataset: {}, listeners: {}, hidden: false, disabled: false, scrollTop: 0,
        addEventListener(name, handler) { this.listeners[name] = handler; },
        setAttribute(name, value) { this[name] = value; }, focus() { this.focused = true; },
        classList: { toggle() {} }, ...extra });
    let current;
    let replacement;
    const makePage = () => {
        const box = element({ checked: false, dataset: { initial: 'false', ankiItemKey: 'words:quiz:word' } });
        const list = element({ querySelectorAll: () => [box] });
        const save = element();
        const limit = element();
        const status = element();
        const fields = [];
        const form = element({ method: 'post', action: '/Anki/Items', dataset: { ankiMaxChanges: '2000' },
            fields: [['__RequestVerificationToken', 'token']],
            querySelector: selector => selector === '[data-anki-items-save]' ? save : limit,
            querySelectorAll: () => [], append: field => fields.push(field) });
        form.closest = () => form;
        form.matches = () => false;
        const addForm = element({ method: 'post', action: '/Anki/AddQuiz',
            fields: [['__RequestVerificationToken', 'token'], ['QuizId', 'chosen-quiz']], matches: () => false });
        addForm.closest = () => addForm;
        const quizPicker = element({ id: 'anki-quiz', value: 'chosen-quiz', dataset: { ankiWorkspaceUrl: '/Anki' } });
        const include = element({ checked: false });
        const settings = element({ querySelector: selector => ({
            '[data-anki-quiz-picker]': quizPicker, '[data-anki-request-status]': status,
            '[data-anki-item-list]': list, '[data-anki-items-form]': form,
            '[name="IncludeSentences"]': include,
        })[selector] ?? null });
        const workspace = element({ isConnected: true, dataset: { ankiUrl: '/Anki?quizId=chosen-quiz' },
            querySelector: selector => ({ '[data-anki-settings]': settings, '[data-anki-item-list]': list })[selector] ?? null,
            querySelectorAll: () => [box, save, quizPicker],
            replaceWith(next) { this.isConnected = false; current = next.page; },
        });
        const toggle = element();
        const result = { box, list, form, fields, save, workspace, quizPicker, settings, toggle, addForm, status, include };
        workspace.page = result;
        return result;
    };
    current = makePage();
    const initial = current;
    const handlers = {};
    const navigations = [], requests = [], history = [];
    const document = {
        querySelector: selector => ({ '[data-anki-workspace]': current.workspace,
            '[data-anki-settings]': current.settings, '[data-anki-settings-toggle]': current.toggle,
            '[data-anki-open-settings]': empty && current === initial ? element() : null })[selector] ?? null,
        querySelectorAll: () => [], createElement: () => element(),
        getElementById: () => current.quizPicker,
    };
    vm.runInNewContext(script, { document, window: {
        location: { href: 'https://example.test/Anki?search=old', assign: url => navigations.push(url) },
        history: { replaceState: (_state, _title, url) => history.push(url) }, confirm,
        addEventListener(name, handler) { handlers[name] = handler; },
        removeEventListener(name, handler) { if (handlers[name] === handler) delete handlers[name]; },
        dispatchEvent(event) { handlers[event.type]?.(event); },
    }, URL, URLSearchParams, CustomEvent: class { constructor(type) { this.type = type; } },
        FormData: class extends Map { constructor(form) { super(form.fields); } },
        DOMParser: class { parseFromString() { return { querySelector: () => replacement.workspace }; } },
        fetch: async (url, options) => {
            requests.push({ url: String(url), ...options });
            if (respond) return respond();
            replacement = makePage();
            return { ok: true, text: async () => '<workspace>' };
        },
        localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value) }, Intl });
    const unload = () => { let prevented = false; handlers.beforeunload({ preventDefault() { prevented = true; } }); return prevented; };
    return { ...initial, unload, navigations, requests, history, get current() { return current; },
        async submit(form = current.addForm) {
            const event = { target: form, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } };
            form.listeners.submit?.(event);
            await current.settings.listeners.submit(event);
            assert.equal(event.defaultPrevented, true);
        },
    };
}

test('settings stay open after adding the first quiz despite an older closed preference', () => {
    const storage = new Map([['glosify.anki.settingsOpen', 'false']]);
    const before = page({ empty: true, storage });
    assert.equal(before.settings.hidden, false);

    // AddQuiz redirects to a new page with cards and no empty-state opener.
    const after = page({ storage });
    assert.equal(after.settings.hidden, false);
});

test('explicitly closing settings is still remembered when the collection has cards', () => {
    const storage = new Map();
    const before = page({ empty: true, storage });
    before.toggle.listeners.click();
    assert.equal(before.settings.hidden, true);
    assert.equal(page({ storage }).settings.hidden, true);
});

test('unsaved picker edits warn before search or quiz navigation, but not after reverting', () => {
    const p = page();
    assert.equal(p.unload(), false);
    p.box.checked = true;
    p.list.listeners.change();
    assert.equal(p.save.disabled, false);
    assert.equal(p.unload(), true);
    p.box.checked = false;
    p.list.listeners.change();
    assert.equal(p.save.disabled, true);
    assert.equal(p.unload(), false);
});

test('saving prepares only changed rows and protects edits until the response arrives', () => {
    const p = page();
    p.box.checked = true;
    p.form.listeners.submit({ preventDefault() { assert.fail('valid save was prevented'); } });
    assert.equal(p.fields.length, 1);
    assert.equal(p.fields[0].name, 'Add');
    assert.equal(p.fields[0].value, 'words:quiz:word');
    assert.equal(p.unload(), true);
});

test('replaced workspace does not keep warning on unrelated navigation', () => {
    const p = page();
    p.box.checked = true;
    p.workspace.isConnected = false;
    assert.equal(p.unload(), false);
});

test('choosing a quiz previews it in place without posting additions', async () => {
    const p = page();
    p.include.checked = true;
    await p.quizPicker.listeners.change();
    assert.deepEqual(p.navigations, []);
    assert.equal(p.requests[0].url, 'https://example.test/Anki?quizId=chosen-quiz');
    assert.equal(p.requests[0].method, 'GET');
    assert.equal(p.current.include.checked, true);
    assert.equal(p.current.quizPicker.focused, true);
    assert.equal(p.fields.length, 0);
    assert.equal(p.unload(), false);
});

test('guest quiz picker does not navigate into the personal workspace', () => {
    const p = page();
    delete p.quizPicker.dataset.ankiWorkspaceUrl;
    p.quizPicker.listeners.change();
    assert.equal(p.navigations.length, 0);
});

test('adding a quiz replaces only the workspace and preserves open settings and scroll', async () => {
    const p = page();
    p.settings.scrollTop = 230;
    p.list.scrollTop = 72;
    await p.submit();
    assert.equal(p.requests.length, 1);
    assert.equal(p.requests[0].method, 'POST');
    assert.equal(p.requests[0].body.get('__RequestVerificationToken'), 'token');
    assert.equal(p.requests[0].body.get('QuizId'), 'chosen-quiz');
    assert.equal(p.requests[0].headers['X-Requested-With'], 'XMLHttpRequest');
    assert.equal(p.workspace.isConnected, false);
    assert.equal(p.current.settings.hidden, false);
    assert.equal(p.current.settings.scrollTop, 230);
    assert.equal(p.current.list.scrollTop, 72);
    assert.deepEqual(p.navigations, []);
    assert.deepEqual(p.history, ['/Anki?quizId=chosen-quiz']);
    // Controls in the replacement have handlers too.
    await p.submit();
    assert.equal(p.requests.length, 2);
});

test('canceling unsaved changes preserves the picker and sends no request', async () => {
    const p = page({ confirm: () => false });
    p.box.checked = true;
    p.quizPicker.value = 'another-quiz';
    await p.quizPicker.listeners.change();
    await p.submit();
    assert.equal(p.quizPicker.value, 'chosen-quiz');
    assert.equal(p.requests.length, 0);
    assert.equal(p.unload(), true);
});

test('pending saves show progress and ignore duplicate submissions', async () => {
    let finish;
    const p = page({ respond: () => new Promise(resolve => { finish = resolve; }) });
    const pending = p.submit();
    assert.equal(p.status.textContent, 'Saving changes…');
    assert.equal(p.quizPicker.disabled, true);
    await p.submit();
    assert.equal(p.requests.length, 1);
    finish({ ok: false, json: async () => ({ detail: 'Collection is full.' }) });
    await pending;
    assert.equal(p.status.textContent, 'Collection is full.');
    assert.equal(p.quizPicker.disabled, false);
    assert.equal(p.workspace.isConnected, true);
});

test('failed item saves keep edits and restore unsaved-change protection', async () => {
    const p = page({ respond: async () => { throw new Error('offline'); } });
    p.box.checked = true;
    await p.submit(p.form);
    assert.match(p.status.textContent, /Could not update Anki/);
    assert.equal(p.box.checked, true);
    assert.equal(p.save.disabled, false);
    assert.equal(p.unload(), true);
});

test('a pending card request defers item saves without losing unsaved-change protection', async () => {
    const p = page();
    p.workspace.querySelector = () => ({});
    p.box.checked = true;
    await p.submit(p.form);
    assert.equal(p.requests.length, 0);
    assert.match(p.status.textContent, /current card to finish saving/);
    assert.equal(p.unload(), true);
});

test('expired sessions show an error without replacing the workspace or navigating', async () => {
    const p = page({ respond: async () => ({ ok: true, redirected: true, url: '/login' }) });
    await p.submit();
    assert.match(p.status.textContent, /session or language changed/);
    assert.equal(p.workspace.isConnected, true);
    assert.deepEqual(p.navigations, []);
});
