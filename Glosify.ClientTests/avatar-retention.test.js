import test from 'node:test';
import assert from 'node:assert/strict';
import { preserveConversation } from './build/avatar/retention.js';

function flow(decision, overrides = {}) {
    const calls = [];
    return { calls, operations: {
        status: async () => ({ turnCount: 20, savedTurns: 10 }),
        choose: async () => decision,
        save: async id => calls.push(['save', id]),
        forget: async id => calls.push(['forget', id]), ...overrides
    }};
}
test('cancel preserves every unsaved turn', async () => {
    const h = flow('cancel'); assert.equal(await preserveConversation('old', h.operations), false); assert.deepEqual(h.calls, []);
});
test('save finishes before forgetting an old conversation', async () => {
    const h = flow('save'); assert.equal(await preserveConversation('old', h.operations), true);
    assert.deepEqual(h.calls, [['save', 'old'], ['forget', 'old']]);
});
test('discard is explicit and does not create a transcript', async () => {
    const h = flow('discard'); assert.equal(await preserveConversation('old', h.operations), true);
    assert.deepEqual(h.calls, [['forget', 'old']]);
});
for (const failure of ['save', 'status']) test(`${failure} failure preserves the conversation`, async () => {
    const h = flow('save', { [failure]: async () => { throw new Error('offline'); } });
    await assert.rejects(preserveConversation('old', h.operations), /offline/); assert.deepEqual(h.calls, []);
});
test('fully saved conversations switch without prompting', async () => {
    const h = flow('cancel', { status: async () => ({ turnCount: 20, savedTurns: 20 }), choose: () => assert.fail('already saved') });
    assert.equal(await preserveConversation('old', h.operations), true); assert.deepEqual(h.calls, [['forget', 'old']]);
});

test('Cancel keeps an active session open and pending turns require a decision', async () => {
    const h = flow('cancel', { pendingTurn: true, status: async () => ({ turnCount: 0, savedTurns: 0 }), stop: () => assert.fail('Cancel must keep session') });
    assert.equal(await preserveConversation('old', h.operations), false);
});
test('Save captures finalized turns after stopping, before forgetting', async () => {
    const h = flow('save'); h.operations.stop = async () => h.calls.push(['stop']);
    await preserveConversation('old', h.operations);
    assert.deepEqual(h.calls, [['stop'], ['save', 'old'], ['forget', 'old']]);
});
