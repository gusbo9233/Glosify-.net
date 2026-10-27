import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const script = readFileSync(new URL('../Glosify/wwwroot/js/anki.js', import.meta.url), 'utf8');
function page() {
    const element = (extra = {}) => ({ dataset: {}, listeners: {}, hidden: false,
        addEventListener(name, handler) { this.listeners[name] = handler; },
        setAttribute() {}, classList: { toggle() {} }, ...extra });
    const box = element({ checked: false, dataset: { initial: 'false', ankiItemKey: 'words:quiz:word' } });
    const list = element({ querySelectorAll: () => [box] });
    const save = element();
    const limit = element();
    const fields = [];
    const form = element({ dataset: { ankiMaxChanges: '2000' },
        querySelector: selector => selector === '[data-anki-items-save]' ? save : limit,
        querySelectorAll: () => [], append: field => fields.push(field) });
    const quizPicker = element({ value: 'chosen-quiz', dataset: { ankiWorkspaceUrl: '/Anki' } });
    const navigations = [];
    const settings = element({ querySelector: selector => ({
        '[data-anki-quiz-picker]': quizPicker,
        '[data-anki-item-list]': list, '[data-anki-items-form]': form,
    })[selector] ?? null });
    const workspace = element({ isConnected: true });
    const toggle = element();
    const handlers = {};
    const document = {
        querySelector: selector => ({ '[data-anki-workspace]': workspace,
            '[data-anki-settings]': settings, '[data-anki-settings-toggle]': toggle })[selector] ?? null,
        querySelectorAll: () => [], createElement: () => element(),
    };
    vm.runInNewContext(script, { document, window: { location: { href: 'https://example.test/Anki?search=old', assign: url => navigations.push(url) },
        addEventListener(name, handler) { handlers[name] = handler; } }, URL,
        localStorage: { getItem: () => null, setItem() {} }, Intl });
    const unload = () => { let prevented = false; handlers.beforeunload({ preventDefault() { prevented = true; } }); return prevented; };
    return { box, list, form, fields, save, unload, workspace, quizPicker, navigations };
}

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

test('saving posts only changed rows and does not trigger the navigation warning', () => {
    const p = page();
    p.box.checked = true;
    p.form.listeners.submit({ preventDefault() { assert.fail('valid save was prevented'); } });
    assert.equal(p.fields.length, 1);
    assert.equal(p.fields[0].name, 'Add');
    assert.equal(p.fields[0].value, 'words:quiz:word');
    assert.equal(p.unload(), false);
});

test('replaced workspace does not keep warning on unrelated navigation', () => {
    const p = page();
    p.box.checked = true;
    p.workspace.isConnected = false;
    assert.equal(p.unload(), false);
});

test('choosing a quiz previews it without posting additions and retains unsaved-change protection', () => {
    const p = page();
    p.box.checked = true;
    p.quizPicker.listeners.change();
    assert.deepEqual(p.navigations, ['https://example.test/Anki?quizId=chosen-quiz']);
    assert.equal(p.fields.length, 0);
    assert.equal(p.unload(), true);
});

test('guest quiz picker does not navigate into the personal workspace', () => {
    const p = page();
    delete p.quizPicker.dataset.ankiWorkspaceUrl;
    p.quizPicker.listeners.change();
    assert.equal(p.navigations.length, 0);
});
