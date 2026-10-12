import test from 'node:test';
import assert from 'node:assert/strict';
import { avatarAssociations } from '../Glosify/wwwroot/js/assistant/avatar-context.js';

test('reloading retains opaque conversation/chat associations only for that account', () => {
    const values = new Map(); const storage = { getItem: key => values.get(key), setItem: (key, value) => values.set(key, value) };
    const first = avatarAssociations('owner', storage); first.contexts.set('chat', 'conversation'); first.threads.set('conversation', 'chat'); first.persist();
    const reopened = avatarAssociations('owner', storage);
    assert.equal(reopened.contexts.get('chat'), 'conversation'); assert.equal(reopened.threads.get('conversation'), 'chat');
    assert.equal(avatarAssociations('other', storage).contexts.size, 0);
    reopened.contexts.delete('chat'); reopened.persist();
    assert.equal(avatarAssociations('owner', storage).contexts.size, 0);
});
test('blocked/malformed association storage does not break the assistant', () => {
    for (const value of ['null', '{}', 'bad', '{"contexts":[null,[],[{},{}]]}']) {
        const links = avatarAssociations('owner', { getItem: () => value, setItem() { throw Error(); } });
        assert.equal(links.contexts.size, 0); links.contexts.set('chat', 'conversation'); links.persist();
        assert.equal(links.contexts.get('chat'), 'conversation');
    }
});
