import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const script = readFileSync(new URL('../Glosify/wwwroot/js/word-tabs.js', import.meta.url), 'utf8');
function content(prefix) {
    const tabs = ['words', 'sentences'].map(name => ({
        dataset: { tab: name }, selected: name === 'words', active: name === 'words',
        setAttribute(name, value) { if (name === 'aria-selected') this.selected = value === 'true'; },
        classList: { toggle(name, value) { tabs.find(tab => tab.classList === this).active = value; } },
    }));
    return { tabs, wordsPanel: { hidden: false, dataset: { title: `${prefix} words`, subtitle: 'Words subtitle' } },
        sentencesPanel: { hidden: true, dataset: { title: `${prefix} sentences`, subtitle: 'Sentences subtitle' } },
        collectionTitle: { textContent: `${prefix} words` }, collectionSubtitle: { textContent: 'Words subtitle' } };
}
test('quiz tabs switch the newly rendered panels after a learning refresh', () => {
    let page = content('Original');
    let click;
    const document = { addEventListener(type, handler) { assert.equal(type, 'click'); click = handler; },
        querySelectorAll: () => page.tabs, getElementById: id => page[id] };
    vm.runInNewContext(script, { document });
    const original = page;
    page = content('Updated'); // learning-updates replaces the contents, without reexecuting scripts.
    click({ target: { closest: () => page.tabs[1] } });
    assert.equal(page.wordsPanel.hidden, true);
    assert.equal(page.sentencesPanel.hidden, false);
    assert.equal(page.tabs[1].selected, true);
    assert.equal(page.tabs[0].active, false);
    assert.equal(page.collectionTitle.textContent, 'Updated sentences');
    assert.equal(page.collectionSubtitle.textContent, 'Sentences subtitle');
    assert.equal(original.wordsPanel.hidden, false);
    assert.equal(original.sentencesPanel.hidden, true);
    click({ target: { closest: () => page.tabs[0] } });
    assert.equal(page.wordsPanel.hidden, false);
    assert.equal(page.sentencesPanel.hidden, true);
    assert.equal(page.collectionTitle.textContent, 'Updated words');
    assert.equal(page.collectionSubtitle.textContent, 'Words subtitle');
    click({ target: { closest: () => null } });
    assert.equal(page.tabs[0].selected, true);
});
