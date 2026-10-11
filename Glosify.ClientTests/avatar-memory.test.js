import test from 'node:test';
import assert from 'node:assert/strict';
import { createConversationMemory } from '../Glosify/wwwroot/js/avatar/memory.js';
const choice = { language: 'sv', characterId: 'rain', quizId: null };
function storage() {
    const values = new Map();
    return { getItem: key => values.get(key), setItem: (key, value) => values.set(key, value), values };
}

test('a later page visit remembers only the opaque reference and selection, scoped to its account', () => {
    const store = storage(), memory = createConversationMemory('owner', store);
    memory.save(choice, 'reference');
    assert.equal(createConversationMemory('owner', store).get(choice), 'reference');
    assert.deepEqual(createConversationMemory('owner', store).preferences(), choice);
    assert.equal(createConversationMemory('other', store).get(choice), undefined);
    assert.equal(createConversationMemory('owner', storage()).get(choice), undefined);
    assert.deepEqual(JSON.parse([...store.values.values()][0]), { preferences: choice, id: 'reference' });
});
for (const change of [{ language: 'en' }, { characterId: 'rowan' }, { quizId: 'quiz' }]) {
    test(`changing ${Object.keys(change)[0]} discards memory even when returning to the old choice`, () => {
        const memory = createConversationMemory('owner', storage());
        memory.save(choice, 'reference');
        assert.equal(memory.select(choice), undefined);
        assert.equal(memory.get(choice), 'reference');
        assert.equal(memory.select({ ...choice, ...change }), 'reference');
        assert.equal(memory.get(choice), undefined);
        memory.select(choice);
        assert.equal(memory.get(choice), undefined);
    });
}
test('a failed storage write cannot later restore the discarded selection or reference', () => {
    const store = storage(), memory = createConversationMemory('owner', store), next = { ...choice, quizId: 'quiz' };
    store.removeItem = key => store.values.delete(key);
    memory.save(choice, 'reference');
    store.setItem = () => { throw new Error('QuotaExceededError'); };
    assert.equal(memory.select(next), 'reference');
    assert.deepEqual(memory.preferences(), next);
    assert.equal(memory.select(next), undefined);
    assert.equal(memory.get(choice), undefined);
});
test('blocked or malformed session storage does not prevent a conversation', () => {
    for (const store of [undefined, { getItem() { throw Error(); }, setItem() { throw Error(); } },
        ...['null', '{bad', '{"entries":[null]}', '{"preferences":null,"id":42}'].map(value => ({ getItem: () => value }))]) {
        const memory = createConversationMemory('owner', store);
        assert.equal(memory.get(choice), undefined);
        memory.save(choice, 'reference');
        assert.equal(memory.get(choice), 'reference');
        assert.equal(memory.select({ ...choice, quizId: 'quiz' }), 'reference');
        assert.equal(memory.get(choice), undefined);
    }
});
