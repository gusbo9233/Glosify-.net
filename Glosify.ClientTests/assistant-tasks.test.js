import test from 'node:test';
import assert from 'node:assert/strict';
import { createAssistantTasks } from '../Glosify/wwwroot/js/assistant/tasks.js';

test('a lost start response retries the same idempotency key', async () => {
    const submissions = [];
    let fail = true;
    const tasks = createAssistantTasks({
        request: async (url, options) => {
            if (url.endsWith('capabilities')) return { enabled: true };
            submissions.push(JSON.parse(options.body));
            if (fail) { fail = false; throw new Error('connection lost'); }
            return { id: 'task', status: 'running' };
        }, onProgress() {}, onCompleted() {}, isCurrent: () => true,
        schedule() {}, isHidden: () => false, isOnline: () => true, newKey: () => 'stable-key',
    });
    await assert.rejects(tasks.send('chat', { message: 'Make a quiz' }));
    await tasks.send('chat', { message: 'Make a quiz' });
    assert.equal(submissions[0].idempotencyKey, submissions[1].idempotencyKey);
});

test('new messages steer an active task and do not submit another one', async () => {
    const calls = [];
    const tasks = createAssistantTasks({
        request: async (url, options) => {
            calls.push([url, options]);
            if (url.endsWith('capabilities')) return { enabled: true };
            return { id: 'task', status: 'running', revision: 4 };
        }, onProgress() {}, onCompleted() {}, isCurrent: () => true,
        schedule() {}, isHidden: () => false, isOnline: () => true, newKey: () => 'key',
    });
    await tasks.send('chat', { message: 'Make a quiz' });
    await tasks.send('chat', { message: 'Include sentences too' });
    assert.equal(calls.at(-1)[0], '/Assistant/Tasks/task/steer');
    assert.deepEqual(JSON.parse(calls.at(-1)[1].body), { revision: 4, message: 'Include sentences too' });
});

test('reopening discovers completed work and only refreshes history once', async () => {
    let completed = 0;
    const tasks = createAssistantTasks({
        request: async url => url.endsWith('capabilities') ? { enabled: true } : { id: 'task', status: 'completed' },
        onProgress() {}, onCompleted() { completed++; }, isCurrent: () => true,
        schedule() {}, isHidden: () => false, isOnline: () => true,
    });
    await tasks.discover('chat'); await tasks.discover('chat');
    assert.equal(completed, 1);
});

test('steering refreshes a stale revision before retrying', async () => {
    const revisions = [];
    const tasks = createAssistantTasks({
        request: async (url, options) => {
            if (url.endsWith('capabilities')) return { enabled: true };
            if (url.endsWith('/steer')) {
                const revision = JSON.parse(options.body).revision;
                revisions.push(revision);
                if (revision === 1) { const error = new Error('Changed'); error.status = 409; throw error; }
                return { id: 'task', status: 'running', revision: 3 };
            }
            return { id: 'task', status: 'running', revision: options?.method === 'POST' ? 1 : 2 };
        }, onProgress() {}, onCompleted() {}, isCurrent: () => true,
        schedule() {}, isHidden: () => false, isOnline: () => true, newKey: () => 'key',
    });
    await tasks.send('chat', { message: 'Add words' });
    await tasks.send('chat', { message: 'Also add sentences' });
    assert.deepEqual(revisions, [1, 2]);
});

test('Stop retries across progress revisions but approval never silently approves a new proposal', async () => {
    const revisions = [];
    const tasks = createAssistantTasks({
        request: async (url, options) => {
            if (url.endsWith('capabilities')) return { enabled: true };
            if (url.endsWith('/cancel') || url.endsWith('/approve')) {
                const revision = JSON.parse(options.body).revision;
                revisions.push([url, revision]);
                if (revision === 1 || url.endsWith('/approve')) {
                    const error = new Error('Changed'); error.status = 409; throw error;
                }
                return { id: 'task', status: 'cancelled', revision: 3 };
            }
            return { id: 'task', status: 'running', revision: options?.method === 'POST' ? 1 : 2 };
        }, onProgress() {}, onCompleted() {}, isCurrent: () => true,
        schedule() {}, isHidden: () => false, isOnline: () => true, newKey: () => 'key',
    });
    await tasks.send('chat', { message: 'Add words' });
    await tasks.command('chat', 'cancel');
    assert.deepEqual(revisions.map(x => x[1]), [1, 2]);
    await assert.rejects(tasks.command('chat', 'approve'));
    assert.equal(revisions.filter(x => x[0].endsWith('/approve')).length, 1);
});
