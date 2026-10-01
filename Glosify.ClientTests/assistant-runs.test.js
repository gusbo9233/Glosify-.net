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
