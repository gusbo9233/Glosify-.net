import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { affectsStudy, createLearningUpdates } from '../Glosify/wwwroot/js/learning-updates.js';

test('committed revisions coalesce, deduplicate, and invalidate stale refreshes', () => {
    const callbacks = new Map();
    const checks = [];
    let next = 0;
    const updates = createLearningUpdates({ refresh: current => checks.push(current), schedule: callback => { callbacks.set(++next, callback); return next; }, cancel: id => callbacks.delete(id) });
    updates.receive({ id: 'run', changeRevision: '' });
    assert.equal(callbacks.size, 0);
    updates.receive({ id: 'run', changeRevision: 'one' });
    updates.receive({ id: 'run', changeRevision: 'one' });
    assert.equal(callbacks.size, 1);
    [...callbacks.values()][0]();
    assert.equal(checks[0](), true);
    updates.receive({ id: 'run', changeRevision: 'two' });
    assert.equal(checks[0](), false);
    [...callbacks.values()][0]();
    assert.equal(checks[1](), true);
    updates.receive({ id: 'run', changeRevision: 'undone' });
    assert.equal(checks[1](), false);
});

const quiz = { studyQuizId: 'quiz-1' };
const anki = { ...quiz, studyAnkiCollectionId: 'anki-1', studyCardId: 'card-1' };
const change = (entityType, entityId, kind, quizId) => ({ entityType, entityId, kind, quizId });
for (const [label, resources, regularAffected, ankiAffected] of [
    ['another quiz edit', [change('word', 'word-2', 'edit_word', 'quiz-2')], false, false],
    ['current quiz edit', [change('word', 'word-1', 'edit_word', 'quiz-1')], true, true],
    ['current quiz deletion', [change('quiz', 'quiz-1', 'delete_quiz')], true, true],
    ['another quiz deletion', [change('quiz', 'quiz-2', 'delete_quiz')], false, false],
    ['quiz rename', [change('quiz', 'quiz-2', 'rename_quiz')], false, false],
    ['folder rename', [change('collection', 'folder', 'rename_collection')], false, false],
    ['another Anki direction', [change('anki', 'anki-2', 'anki_direction')], false, false],
    ['current Anki direction', [change('anki', 'anki-1', 'anki_direction')], false, true],
    ['current Anki deletion', [change('anki', 'anki-1', 'anki_delete')], false, true],
    ['another Anki settings change', [change('anki', 'anki-2', 'anki_settings')], false, false],
    ['another card removal', [change('anki_card', 'card-2', 'remove_anki_cards')], false, false],
    ['current card removal', [change('anki_card', 'card-1', 'remove_anki_cards')], false, true],
    ['mixed quiz edits', [change('word', 'word-2', 'edit_word', 'quiz-2'), change('sentence', 'sentence-1', 'delete_sentence', 'quiz-1')], true, true],
    ['legacy event', undefined, true, true],
    ['folder deletion with unknown contents', [change('collection', 'folder', 'delete_collection')], true, true],
]) test(`${label} only invalidates potentially affected study sessions`, () => {
    assert.equal(affectsStudy({ resourceChanges: resources }, quiz), regularAffected);
    assert.equal(affectsStudy({ resourceChanges: resources }, anki), ankiAffected);
});

test('an unrelated change cannot schedule an interruption or cancel a pending relevant update', () => {
    const callbacks = new Map();
    let next = 0;
    const updates = createLearningUpdates({ refresh() {}, accept: run => affectsStudy(run, anki),
        schedule: callback => { callbacks.set(++next, callback); return next; }, cancel: id => callbacks.delete(id) });
    const unrelated = { id: 'other', changeRevision: 'one', resourceChanges: [change('quiz', 'quiz-2', 'rename_quiz')] };
    updates.receive(unrelated);
    assert.equal(callbacks.size, 0);
    updates.receive({ id: 'current', changeRevision: 'one', resourceChanges: [change('anki', 'anki-1', 'anki_direction')] });
    const callback = [...callbacks.values()][0];
    updates.receive({ ...unrelated, changeRevision: 'two' });
    assert.deepEqual([...callbacks.values()], [callback]);
});

test('cross-tab notifications preserve affected resources without broadcasting chat content', () => {
    const source = readFileSync(new URL('../Glosify/wwwroot/js/learning-updates.js', import.meta.url), 'utf8');
    const listeners = new Map();
    const sent = [];
    vm.runInNewContext(source.replaceAll('export function', 'function'), {
        window: { addEventListener: (type, callback) => listeners.set(type, callback) },
        document: { querySelector: () => null },
        BroadcastChannel: class { postMessage(value) { sent.push(value); } },
    });
    const resources = [change('quiz', 'quiz-2', 'rename_quiz')];
    listeners.get('assistant:changes')({ detail: { id: 'run', changeRevision: 'revision', resourceChanges: resources, message: 'private chat' } });
    assert.equal(JSON.stringify(sent), JSON.stringify([{ id: 'run', changeRevision: 'revision', resourceChanges: resources }]));
});

function refreshHarness(response, path = '/Quiz/Details/deleted') {
    const callbacks = [], listeners = new Map();
    const node = tag => ({ tag, children: [], hidden: false, textContent: '',
        setAttribute() {}, addEventListener() {},
        replaceChildren(...children) { this.children = children; }, append(...children) { this.children.push(...children); } });
    const old = node('old-quiz'), screen = node('screen'); screen.children = [old];
    screen.dataset = { learningController: path.startsWith('/Anki') ? 'anki' : 'quiz' };
    screen.querySelector = () => null; screen.querySelectorAll = () => [];
    let notice;
    screen.before = value => { notice = value; };
    const location = new URL(path, 'https://glosify.test');
    vm.runInNewContext(readFileSync(new URL('../Glosify/wwwroot/js/learning-updates.js', import.meta.url), 'utf8')
        .replaceAll('export function', 'function'), {
        window: { scrollX: 0, scrollY: 50, scrollTo() {},
            addEventListener(type, listener) { const group = listeners.get(type) || []; group.push(listener); listeners.set(type, group); } },
        document: { querySelector: () => screen, createElement: node, createTextNode: text => ({ textContent: text }) },
        URL, location, setTimeout: callback => { callbacks.push(callback); return callbacks.length; }, clearTimeout() {},
        fetch: async () => ({ text: async () => '<html>redirected library</html>', ...response }),
        DOMParser: class { parseFromString() { assert.fail('Removed or unexpected redirects must not inject their response HTML'); } },
    });
    return { screen, old, notice: () => notice, refresh: async () => {
        for (const listener of listeners.get('assistant:changes')) listener({ detail: { id: 'run', changeRevision: 'deleted' } });
        await callbacks.at(-1)();
    }};
}

for (const [path, destination] of [
    ['/Quiz/Details/deleted', 'https://glosify.test/Quiz'],
    ['/Quiz/Collection/deleted', 'https://glosify.test/Quiz/Index'],
    ['/Anki/Collection/deleted', 'https://glosify.test/Anki'],
]) test(`a deleted ${path} redirect replaces stale content while keeping the chat shell`, async () => {
    const h = refreshHarness({ ok: true, redirected: true, status: 200, url: destination }, path);
    await h.refresh();
    assert.equal(h.screen.children.length, 2);
    assert.equal(h.screen.children[0].textContent, 'This item was removed.');
    assert.equal(h.screen.children[1].href, path.startsWith('/Anki') ? '/Anki' : '/Quiz');
    assert.equal(h.notice().hidden, true);
});

for (const destination of ['https://glosify.test/login', 'https://other.test/Quiz', 'https://glosify.test/Anki', 'https://glosify.test/Quiz?redirect=login'])
    test(`unexpected refresh redirect ${destination} preserves the page and offers retry`, async () => {
        const h = refreshHarness({ ok: true, redirected: true, status: 200, url: destination });
        await h.refresh();
        assert.deepEqual(h.screen.children, [h.old]);
        assert.equal(h.notice().hidden, false);
        assert.equal(h.notice().children.at(-1).textContent, 'Update view');
    });
