import test from 'node:test';
import assert from 'node:assert/strict';
import { chooseInitialChat, createRecoverablePromiseQueue, materialPayload, removeChat, replaceChat, upsertChat } from '../Glosify/wwwroot/js/assistant/state.js';

test('material context is all-or-nothing', () => {
    assert.deepEqual(materialPayload('book', 'book-1'), { contextTranscriptId: null, contextBookDocumentId: 'book-1' });
    assert.deepEqual(materialPayload(null, null), { contextTranscriptId: null, contextBookDocumentId: null });
});

test('chat updates are case-insensitive and immutable', () => {
    const original = [{ id: 'ABC', title: 'Old' }, { id: 'two', title: 'Two' }];
    const replaced = replaceChat(original, { id: 'abc', title: 'New' });
    assert.equal(replaced[0].title, 'New');
    assert.equal(original[0].title, 'Old');
    assert.deepEqual(removeChat(replaced, 'TWO'), [{ id: 'abc', title: 'New' }]);
    assert.deepEqual(upsertChat(replaced, { id: 'TWO', title: 'Latest' }).map(chat => chat.title), ['Latest', 'New']);
});

test('stored chat wins, otherwise a blank newest chat is reused', () => {
    const chats = [{ id: 'blank', preview: '' }, { id: 'older', preview: 'hello' }];
    assert.equal(chooseInitialChat(chats, 'OLDER').id, 'older');
    assert.equal(chooseInitialChat(chats, null).id, 'blank');
    assert.equal(chooseInitialChat([{ id: 'used', preview: 'hello' }], null), null);
});

test('context writes run in invocation order and continue after failure', async () => {
    const writes = [];
    let stored = null;
    const enqueue = createRecoverablePromiseQueue(async snapshot => {
        writes.push(snapshot.contextQuizId);
        if (snapshot.contextQuizId === 'second') {
            throw new Error('temporary PATCH failure');
        }
        stored = snapshot;
    });

    const results = await Promise.allSettled([
        enqueue({ contextQuizId: 'first', contextBookDocumentId: 'book-1' }),
        enqueue({ contextQuizId: 'second', contextBookDocumentId: 'book-2' }),
        enqueue({ contextQuizId: 'third', contextBookDocumentId: 'book-3' }),
    ]);

    assert.deepEqual(writes, ['first', 'second', 'third']);
    assert.deepEqual(results.map(result => result.status), ['fulfilled', 'rejected', 'fulfilled']);
    assert.deepEqual(stored, { contextQuizId: 'third', contextBookDocumentId: 'book-3' });
});
