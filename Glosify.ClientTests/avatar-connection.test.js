import test from 'node:test';
import assert from 'node:assert/strict';
import { VoiceConnection } from '../Glosify/wwwroot/js/avatar/connection.js';

function setup(callbacks = {}) {
    let now = 0, nextId = 0;
    const timers = new Map(), sockets = [], messages = [], ended = [], lost = [];
    const connection = new VoiceConnection('/same-session/voice', {
        onMessage: message => { messages.push(message); return callbacks.onMessage?.(message); },
        onLost: () => lost.push(now), onEnded: detail => ended.push(detail)
    }, {
        now: () => now, schedule: (action, delay) => { const id = ++nextId; timers.set(id, { at: now + delay, action }); return id; },
        cancel: id => timers.delete(id),
        createSocket: url => {
            const socket = { url, readyState: 0, bufferedAmount: 0, sent: [], closes: 0,
                send(data) { this.sent.push(data); }, close() { this.closes++; this.readyState = 3; },
                message(value) { this.onmessage?.({ data: JSON.stringify(value) }); },
                connected() { this.readyState = 1; this.message({ type: 'connected' }); },
                drop(code = 1006) { this.readyState = 3; this.onclose?.({ code }); }
            };
            sockets.push(socket); return socket;
        }
    });
    return { connection, sockets, messages, ended, lost, timers,
        advance(ms) {
            const end = now + ms;
            while (true) {
                const next = [...timers].sort((a, b) => a[1].at - b[1].at)[0];
                if (!next || next[1].at > end) break;
                now = next[1].at; timers.delete(next[0]); next[1].action();
            }
            now = end;
        }
    };
}

test('heartbeats keep a silent conversation healthy without surfacing pong as UI messages', () => {
    const h = setup(), socket = h.sockets[0]; socket.connected();
    for (let i = 0; i < 120; i++) {
        h.advance(5000);
        assert.deepEqual(JSON.parse(socket.sent.at(-1)), { type: 'ping' });
        socket.message({ type: 'pong' });
    }
    assert.equal(h.sockets.length, 1); assert.deepEqual(h.lost, []);
    assert.deepEqual(h.messages, [{ type: 'connected', resumed: false }]);
    h.connection.close(); assert.equal(h.timers.size, 0);
});

test('lost transport reconnects to the same session without replaying microphone data', () => {
    const h = setup(), first = h.sockets[0]; first.connected();
    const pcm = new ArrayBuffer(6400); assert.equal(h.connection.send(pcm), true);
    first.drop(); h.advance(1000);
    const next = h.sockets[1]; assert.equal(next.url, first.url); next.connected();
    assert.equal(h.messages.at(-1).resumed, true);
    assert.deepEqual(next.sent, []); assert.equal(h.lost.length, 1);
    assert.equal(h.connection.send('new command'), true); assert.deepEqual(next.sent, ['new command']);
    h.connection.close();
});

test('a half-open socket is detected within 20 seconds and handshake retries have a deadline', () => {
    const h = setup(); h.sockets[0].connected(); h.advance(19000);
    assert.deepEqual(h.lost, []); h.advance(1000); assert.deepEqual(h.lost, [20000]);
    h.advance(1000); assert.equal(h.sockets.length, 2);
    h.advance(10000); assert.equal(h.sockets[1].closes, 1);
    h.advance(34000);
    assert.equal(h.ended.length, 1); assert.match(h.ended[0], /Could not reconnect/);
    assert.equal(h.connection.closed, true); assert.equal(h.timers.size, 0);
    const count = h.sockets.length; h.advance(60000); assert.equal(h.sockets.length, count);
});

test('an initial socket that never opens times out and eventually releases the UI', () => {
    const h = setup(); h.advance(10000); assert.equal(h.lost.length, 1);
    h.advance(45000); assert.equal(h.ended.length, 1); assert.equal(h.timers.size, 0);
});

test('backpressure and failed sends discard the interrupted turn', () => {
    for (const failure of ['buffer', 'throw']) {
        const h = setup(), socket = h.sockets[0]; socket.connected();
        if (failure === 'buffer') socket.bufferedAmount = 192001;
        else socket.send = () => { throw new Error('Connection gone'); };
        assert.equal(h.connection.send(new ArrayBuffer(6400)), false);
        assert.equal(h.lost.length, 1); assert.equal(socket.closes, 1);
        h.advance(1000); h.sockets[1].connected(); assert.deepEqual(h.sockets[1].sent, []);
        h.connection.close();
    }
});

test('End cancels reconnection and normal or policy closes do not retry', () => {
    for (const code of [1000, 1008]) {
        const h = setup(); h.sockets[0].connected(); h.sockets[0].drop(code); h.advance(60000);
        assert.equal(h.ended.length, 1); assert.equal(h.sockets.length, 1);
    }
    const h = setup(); h.sockets[0].connected(); h.sockets[0].drop(); h.connection.close(); h.advance(60000);
    assert.equal(h.sockets.length, 1); assert.deepEqual(h.ended, []);
    assert.equal(h.connection.send('late audio'), false);
});

test('late events and rejected playback on the old socket cannot end the restored connection', async () => {
    let reject;
    const h = setup({ onMessage: message => message.type === 'audio-end' ? new Promise((_, no) => { reject = no; }) : undefined });
    const old = h.sockets[0]; old.connected(); const stale = old.onmessage;
    old.message({ type: 'audio-end' }); old.drop(); h.advance(1000); h.sockets[1].connected();
    stale({ data: '{invalid' }); reject(new Error('old playback')); await Promise.resolve();
    assert.equal(h.connection.closed, false); assert.deepEqual(h.ended, []);
    h.connection.close();
});


test('the complete five-second setup buffer fits without causing a false reconnect', () => {
    const h = setup(), socket = h.sockets[0]; socket.connected();
    socket.send = data => { socket.bufferedAmount += data.byteLength; socket.sent.push(data); };
    for (let i = 0; i < 25; i++) assert.equal(h.connection.send(new ArrayBuffer(6400)), true);
    assert.equal(socket.sent.length, 25); assert.deepEqual(h.lost, []);
    h.connection.close();
});

test('terminal idle expiry stops transport retries even if the following close is abnormal', () => {
    const h = setup(), socket = h.sockets[0]; socket.connected();
    socket.message({ type: 'ended', reason: 'idle' }); socket.drop(1006);
    h.advance(60000);
    assert.equal(h.ended.length, 1);
    assert.match(h.ended[0], /ended after inactivity/);
    assert.equal(h.connection.closed, true);
    assert.equal(h.sockets.length, 1);
    assert.deepEqual(h.lost, []);
    assert.equal(h.timers.size, 0);
});

test('a conversation paused for another learner explains itself and stops retrying', () => {
    const h = setup(), socket = h.sockets[0]; socket.connected();
    socket.message({ type: 'ended', reason: 'busy' }); socket.drop(1006);
    h.advance(60000);
    assert.equal(h.ended.length, 1);
    assert.match(h.ended[0], /made room for another learner/);
    assert.equal(h.sockets.length, 1);
    assert.deepEqual(h.lost, []);
});
