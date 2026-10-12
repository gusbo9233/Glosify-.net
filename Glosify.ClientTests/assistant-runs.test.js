import test from 'node:test';
import assert from 'node:assert/strict';
import { createAssistantRuns, isTerminal, isWaiting, isWorking, newer, sendAction } from '../Glosify/wwwroot/js/assistant/runs.js';

const run = (overrides = {}) => ({ id: 'run-1', threadId: 'chat-1', status: 'running', revision: 1, ...overrides });

class FakeEvents {
    constructor(url) {
        this.url = url;
        this.readyState = 1;
        this.listeners = {};
        FakeEvents.opened.push(this);
    }
    addEventListener(type, listener) { this.listeners[type] = listener; }
    emit(view) { this.listeners.run?.({ data: JSON.stringify(view) }); }
    close() { this.closed = true; this.readyState = 2; }
}
FakeEvents.opened = [];

const harness = (handlers = {}, options = {}) => {
    FakeEvents.opened = [];
    const calls = [];
    const updates = [];
    const scheduled = [];
    const runs = createAssistantRuns({
        request: async (url, init = {}) => {
            calls.push({ url, method: init.method || 'GET', body: init.body ? JSON.parse(init.body) : null });
            const handler = handlers[`${init.method || 'GET'} ${url}`];
            if (!handler) throw new Error(`Unexpected ${init.method || 'GET'} ${url}`);
            return handler(calls.at(-1).body);
        },
        onUpdate: view => updates.push(view),
        isCurrent: () => true,
        openEvents: options.noEvents ? () => null : url => new FakeEvents(url),
        schedule: callback => scheduled.push(callback),
        newKey: (() => { let next = 0; return () => `key-${++next}`; })(),
    });
    return { runs, calls, updates, scheduled };
};

test('status helpers separate working, waiting, and finished runs', () => {
    assert.ok(isWorking('queued') && isWorking('running') && isWorking('retry_wait'));
    assert.ok(isWaiting('awaiting_input') && isWaiting('awaiting_approval') && isWaiting('paused'));
    assert.ok(isTerminal('completed') && isTerminal('cancelled') && isTerminal('failed'));
    assert.equal(isWorking('completed'), false);
});

test('rapid answers share one pending command, with retry after failure', async () => {
    let reject;
    let attempts = 0;
    const first = new Promise((_, fail) => { reject = fail; });
    const h = harness({
        'GET /Assistant/Runs/chats/chat-1': () => run({ status: 'awaiting_input' }),
        'POST /Assistant/Runs/run-1/answer': () => ++attempts === 1 ? first : run({ status: 'queued', revision: 2 }),
    });
    await h.runs.discover('chat-1');
    const one = h.runs.answer('chat-1', ['First']);
    const two = h.runs.answer('chat-1', ['Second']);
    assert.equal(one, two);
    assert.equal(attempts, 1);
    reject(new Error('Temporary failure'));
    await assert.rejects(one, /Temporary/);
    await h.runs.answer('chat-1', ['Retry']);
    assert.equal(attempts, 2);
    assert.deepEqual(h.calls.filter(call => call.method === 'POST').map(call => call.body.answers), [['First'], ['Retry']]);
});

for (const nextStatus of ['queued', 'completed']) test(`typing during an option answer preserves new text after it becomes ${nextStatus}`, async () => {
    let resolve;
    const pending = new Promise(done => { resolve = done; });
    const h = harness({
        'GET /Assistant/Runs/chats/chat-1': () => run({ status: 'awaiting_input' }),
        'POST /Assistant/Runs/run-1/answer': () => pending,
        'POST /Assistant/Runs/run-1/steer': () => run({ status: 'queued', revision: 3 }),
        'POST /Assistant/Runs/chats/chat-1': () => run({ id: 'run-2', status: 'queued' }),
    });
    await h.runs.discover('chat-1');
    const option = h.runs.answer('chat-1', ['First']);
    const typed = h.runs.send('chat-1', { message: 'Additional detail' });
    assert.equal(h.calls.filter(call => call.method === 'POST').length, 1);
    resolve(run({ status: nextStatus, revision: 2 }));
    await Promise.all([option, typed]);
    const posts = h.calls.filter(call => call.method === 'POST');
    assert.equal(posts.length, 2);
    if (nextStatus === 'queued') {
        assert.equal(posts[1].url, '/Assistant/Runs/run-1/steer');
        assert.equal(posts[1].body.message, 'Additional detail');
        assert.equal(posts[1].body.revision, 2);
    } else {
        assert.equal(posts[1].url, '/Assistant/Runs/chats/chat-1');
        assert.equal(posts[1].body.request.message, 'Additional detail');
    }
});

test('a message goes to the active run as an answer or a follow-up', () => {
    assert.equal(sendAction(null), 'start');
    assert.equal(sendAction(run({ status: 'completed' })), 'start');
    assert.equal(sendAction(run({ status: 'awaiting_input' })), 'answer');
    assert.equal(sendAction(run({ status: 'running' })), 'steer');
    assert.equal(sendAction(run({ status: 'paused' })), 'steer');
});

test('an older revision never replaces a newer one', () => {
    assert.ok(newer(null, run()));
    assert.ok(newer(run({ revision: 2 }), run({ revision: 3 })));
    assert.equal(newer(run({ revision: 3 }), run({ revision: 2 })), false);
    assert.ok(newer(run({ id: 'old', revision: 9 }), run({ revision: 1 })));
});

test('a retried send reuses its submission key and the run streams its updates', async () => {
    let attempts = 0;
    const { runs, calls, updates } = harness({
        'POST /Assistant/Runs/chats/chat-1': () => {
            if (++attempts === 1) throw new TypeError('Failed to fetch');
            return run({ status: 'queued' });
        },
    });
    const input = { message: 'Add dom' };

    await assert.rejects(runs.start('chat-1', input));
    await runs.start('chat-1', input);

    assert.deepEqual(calls.map(call => call.body.idempotencyKey), ['key-1', 'key-1']);
    const stream = FakeEvents.opened[0];
    assert.equal(stream.url, '/Assistant/Runs/run-1/events');
    stream.emit(run({ revision: 3 }));
    stream.emit(run({ revision: 2 }));
    stream.emit(run({ revision: 4, status: 'completed' }));
    assert.deepEqual(updates.map(update => update.revision), [1, 3, 4]);
    assert.ok(stream.closed);
});

test('typing while a question is open answers it, otherwise it steers', async () => {
    const { runs, calls } = harness({
        'GET /Assistant/Runs/chats/chat-1': () => run({ status: 'awaiting_input', revision: 5 }),
        'POST /Assistant/Runs/run-1/answer': () => run({ status: 'queued', revision: 6 }),
        'POST /Assistant/Runs/run-1/steer': () => run({ status: 'queued', revision: 7 }),
    });

    await runs.discover('chat-1');
    await runs.send('chat-1', { message: 'Travel' });
    await runs.send('chat-1', { message: 'Also food' });

    assert.deepEqual(calls.slice(1).map(call => [call.url, call.body.revision, call.body.message]), [
        ['/Assistant/Runs/run-1/answer', 5, 'Travel'],
        ['/Assistant/Runs/run-1/steer', 6, 'Also food'],
    ]);
});

test('Stop retries against the latest revision, a decision does not', async () => {
    let cancels = 0;
    const conflict = () => Object.assign(new Error('changed'), { status: 409 });
    const { runs, calls } = harness({
        'GET /Assistant/Runs/chats/chat-1': () => run({ status: 'awaiting_approval', revision: 1 }),
        'GET /Assistant/Runs/run-1': () => run({ status: 'running', revision: 4 }),
        'POST /Assistant/Runs/run-1/cancel': () => { if (++cancels === 1) throw conflict(); return run({ status: 'cancelled', revision: 5 }); },
        'POST /Assistant/Runs/run-1/approve': () => { throw conflict(); },
    });
    await runs.discover('chat-1');

    await assert.rejects(runs.approve('chat-1', true), error => error.status === 409);
    const stopped = await runs.stop('chat-1');

    assert.equal(stopped.status, 'cancelled');
    const approve = calls.find(call => call.url.endsWith('/approve'));
    assert.deepEqual(approve.body, { revision: 1, always: true });
    assert.deepEqual(calls.filter(call => call.url.endsWith('/cancel')).map(call => call.body.revision), [1, 4]);
});

for (const status of ['completed', 'cancelled', 'failed']) test(`a stale follow-up starts a new run after the server confirms ${status}`, async () => {
    const input = { message: 'Also add food', quizId: 'quiz-1', documentContext: { bookId: 'book-1', page: 3 } };
    const h = harness({
        'GET /Assistant/Runs/chats/chat-1': () => run(),
        'POST /Assistant/Runs/run-1/steer': () => { throw Object.assign(new Error('ended'), { status: 409 }); },
        'GET /Assistant/Runs/run-1': () => run({ status, revision: 3 }),
        'POST /Assistant/Runs/chats/chat-1': () => run({ id: 'run-2', status: 'queued' }),
    });
    await h.runs.discover('chat-1');
    const next = await h.runs.send('chat-1', input);
    assert.equal(next.id, 'run-2');
    const posts = h.calls.filter(call => call.method === 'POST');
    assert.deepEqual(posts.map(call => call.url), ['/Assistant/Runs/run-1/steer', '/Assistant/Runs/chats/chat-1']);
    assert.deepEqual(posts[1].body, { idempotencyKey: 'key-1', request: input });
    assert.equal(h.runs.get('chat-1').id, 'run-2');
});

test('a stale follow-up reuses the new submission key after a lost start response', async () => {
    let starts = 0;
    const h = harness({
        'GET /Assistant/Runs/chats/chat-1': () => run(),
        'POST /Assistant/Runs/run-1/steer': () => { throw Object.assign(new Error('ended'), { status: 409 }); },
        'GET /Assistant/Runs/run-1': () => run({ status: 'completed', revision: 3 }),
        'POST /Assistant/Runs/chats/chat-1': () => {
            if (++starts === 1) throw new TypeError('Failed to fetch');
            return run({ id: 'run-2', status: 'queued' });
        },
    });
    await h.runs.discover('chat-1');
    const input = { message: 'Also food' };
    await assert.rejects(h.runs.send('chat-1', input), /Failed to fetch/);
    await h.runs.send('chat-1', input);
    assert.deepEqual(h.calls.filter(call => call.method === 'POST' && call.url.endsWith('/chat-1'))
        .map(call => call.body.idempotencyKey), ['key-1', 'key-1']);
});

test('an active-run conflict retries steering without starting another run', async () => {
    let attempts = 0;
    const h = harness({
        'GET /Assistant/Runs/chats/chat-1': () => run(),
        'POST /Assistant/Runs/run-1/steer': () => {
            if (++attempts === 1) throw Object.assign(new Error('changed'), { status: 409 });
            return run({ status: 'queued', revision: 4 });
        },
        'GET /Assistant/Runs/run-1': () => run({ revision: 3 }),
    });
    await h.runs.discover('chat-1');
    const next = await h.runs.send('chat-1', { message: 'Also food' });
    assert.equal(next.id, 'run-1');
    assert.deepEqual(h.calls.filter(call => call.method === 'POST').map(call => call.body.revision), [1, 3]);
});

test('a steering network failure does not start a duplicate run', async () => {
    const h = harness({
        'GET /Assistant/Runs/chats/chat-1': () => run(),
        'POST /Assistant/Runs/run-1/steer': () => { throw new TypeError('Failed to fetch'); },
    });
    await h.runs.discover('chat-1');
    await assert.rejects(h.runs.send('chat-1', { message: 'Also food' }), /Failed to fetch/);
    assert.equal(h.calls.filter(call => call.method === 'POST').length, 1);
    assert.equal(h.runs.get('chat-1').id, 'run-1');
});

test('without server-sent events the run is polled until it finishes', async () => {
    let polls = 0;
    const { runs, updates, scheduled } = harness({
        'POST /Assistant/Runs/chats/chat-1': () => run({ status: 'queued' }),
        'GET /Assistant/Runs/run-1': () => run({ status: ++polls < 2 ? 'running' : 'completed', revision: 1 + polls }),
    }, { noEvents: true });

    await runs.start('chat-1', { message: 'Hello' });
    while (scheduled.length) await scheduled.shift()();

    assert.equal(polls, 2);
    assert.equal(updates.at(-1).status, 'completed');
});

test('undo publishes the run it returns', async () => {
    const { runs, updates } = harness({
        'POST /Assistant/Runs/run-1/undo': () => ({ undone: 2, kept: 1, run: run({ status: 'completed', revision: 9, undone: true }) }),
    });

    const result = await runs.undo('run-1');

    assert.equal(result.kept, 1);
    assert.equal(updates.at(-1).undone, true);
});

test('saved changes notify other screens once and Undo from history also notifies', async () => {
    const changed = [];
    const streams = [];
    const runs = createAssistantRuns({
        request: async url => url.endsWith('/undo')
            ? { run: run({ status: 'completed', revision: 5, changeRevision: 'undone' }) }
            : run({ status: 'running', changeRevision: '' }),
        isCurrent: () => false,
        onUpdate: () => assert.fail('Unselected chat must not render'),
        onChanges: view => changed.push(view.changeRevision),
        openEvents: url => { const stream = new FakeEvents(url); streams.push(stream); return stream; },
    });
    await runs.discover('chat-1');
    streams[0].emit(run({ revision: 2, changeRevision: 'saved' }));
    streams[0].emit(run({ revision: 2, changeRevision: 'saved' }));
    streams[0].emit(run({ revision: 1, changeRevision: 'stale' }));
    assert.deepEqual(changed, ['saved']);
    await runs.undo('run-1');
    assert.deepEqual(changed, ['saved', 'undone']);
});

test('discovering an old completed run does not invalidate a freshly rendered study page', async () => {
    const changed = [];
    const runs = createAssistantRuns({
        request: async url => url.endsWith('/undo')
            ? { run: run({ status: 'completed', revision: 5, changeRevision: 'undone' }) }
            : run({ status: 'completed', revision: 4, changeRevision: 'saved' }),
        isCurrent: () => true, onUpdate() {}, onChanges: view => changed.push(view.changeRevision),
    });
    await runs.discover('chat-1');
    assert.deepEqual(changed, []);
    await runs.undo('run-1');
    assert.deepEqual(changed, ['undone']);
});

test('late events and historical Undo cannot replace the next run or close its stream', async () => {
    let started = 0;
    const { runs } = harness({
        'POST /Assistant/Runs/chats/chat-1': () => run({ id: `run-${++started}` }),
        'POST /Assistant/Runs/run-1/undo': () => ({ run: run({ status: 'completed', revision: 9, changeRevision: 'undone' }) }),
    });
    await runs.start('chat-1', { message: 'First' });
    const old = FakeEvents.opened[0];
    old.emit(run({ status: 'completed', revision: 2 }));
    await runs.start('chat-1', { message: 'Second' });
    const current = FakeEvents.opened[1];
    old.emit(run({ status: 'completed', revision: 3 }));
    assert.equal(runs.get('chat-1').id, 'run-2');
    assert.equal(current.closed, undefined);
    await runs.undo('run-1');
    assert.equal(runs.get('chat-1').id, 'run-2');
    assert.equal(current.closed, undefined);
    current.emit(run({ id: 'run-2', revision: 2 }));
    assert.equal(runs.get('chat-1').revision, 2);
});

test('delayed discovery cannot overwrite a run started while it was pending', async () => {
    let resolveDiscovery;
    const { runs } = harness({
        'GET /Assistant/Runs/chats/chat-1': () => new Promise(resolve => { resolveDiscovery = resolve; }),
        'POST /Assistant/Runs/chats/chat-1': () => run({ id: 'new' }),
    });
    const discovery = runs.discover('chat-1');
    await runs.start('chat-1', { message: 'Start' });
    resolveDiscovery(run({ id: 'old', status: 'completed' }));
    await discovery;
    assert.equal(runs.get('chat-1').id, 'new');
    assert.equal(FakeEvents.opened[0].closed, undefined);
});
