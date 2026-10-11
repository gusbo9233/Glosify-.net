import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { createConversationMemory } from '../Glosify/wwwroot/js/avatar/memory.js';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../Glosify/wwwroot/js/avatar/avatar.js', import.meta.url), 'utf8');
function setup(mode = 'push-to-talk') {
    const elements = new Map(), sent = [], listeners = {}, windowListeners = {};
    const modeInput = { value: mode, listeners: {}, addEventListener(name, action) { this.listeners[name] = action; } };
    const element = id => {
        if (['avatar-new', 'avatar-memory', 'avatar-balance', 'avatar-rates', 'avatar-mute', 'avatar-end', 'avatar-hint'].includes(id)) return null;
        if (!elements.has(id)) elements.set(id, { value: '', dataset: {}, textContent: '', options: [], attributes: {}, listeners: {}, classList: { toggle() {} }, add(option) { this.options.push(option); }, replaceChildren(...children) { this.options = children; }, addEventListener(name, action) { this.listeners[name] = action; }, setAttribute(name, value) { this.attributes[name] = value; }, setPointerCapture() {} });
        return elements.get(id);
    };
    const context = vm.createContext({
        document: { getElementById: element, querySelector: selector => selector.includes('avatar-mode') ? modeInput : { value: 'csrf' },
            querySelectorAll: selector => selector.includes('avatar-mode') ? [modeInput] : [], addEventListener: (name, action) => { listeners[name] = action; } },
        window: { addEventListener(name, action) { windowListeners[name] = action; } }, WebSocket: { OPEN: 1 }, Intl, console, URL, AbortController, createConversationMemory,
        setTimeout: (fn, ms) => { const timer = setTimeout(fn, ms); timer.unref(); return timer; }, clearTimeout,
        location: { href: 'https://localhost/Avatar', protocol: 'https:' },
        Option: class { constructor(text, value) { this.text = text; this.value = value; } },
        createAvatar: () => new Promise(() => {}), fetch: () => new Promise(() => {}),
        wire: value => sent.push(value),
    });
    vm.runInContext(source.replace(/^import .*;\n/gm, ''), context);
    vm.runInContext(`VoiceConnection = class { constructor(url, handlers) {
        const transport = new WebSocket(url); globalThis.connectionHandlers = handlers; return transport;
    } };`, context);
    vm.runInContext("characterLoading = false; el('character').disabled = false; ready = true; socket = { readyState: 1, send: wire }; audio = { stop() {}, flush: async () => {} };", context);
    return { sent, elements, chooseMode: value => { modeInput.value = value; modeInput.listeners.change(); }, buttonEvent: (name, event) => element('avatar-talk').listeners[name](event), documentEvent: (name) => listeners[name]?.(), windowEvent: (name, event = {}) => windowListeners[name](event), run: code => vm.runInContext(code, context),
        frame: index => { context.pcm = Uint8Array.of(index).buffer; vm.runInContext('frame({ pcm, rms: 0 })', context); },
        binary: () => sent.filter(x => Object.prototype.toString.call(x) === '[object ArrayBuffer]').map(x => new Uint8Array(x)[0]),
        commands: () => sent.filter(x => typeof x === 'string').map(x => JSON.parse(x).type),
    };
}

test('push-to-talk preserves initial audio through a two-second provider setup', async () => {
    const h = setup(); h.run('press()');
    for (let i = 0; i < 10; i++) h.frame(i);
    await h.run("onMessage({ type: 'listening' }, epoch)");
    assert.deepEqual(h.binary(), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
    h.frame(10); await h.run('release()');
    assert.deepEqual(h.binary(), [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
    assert.equal(h.commands().at(-1), 'commit');
});

test('release before Ready sends the buffered utterance then commits without later microphone audio', async () => {
    const h = setup(); h.run('press()');
    h.frame(1); h.frame(2); await h.run('release()'); h.frame(3);
    assert.deepEqual(h.binary(), []);
    await h.run("onMessage({ type: 'listening' }, epoch)");
    assert.deepEqual(h.binary(), [1, 2]);
    assert.equal(h.commands().at(-1), 'commit');
    assert.equal(h.elements.get('avatar-status').textContent, 'Thinking');
});

test('excessive setup delay stops with an explicit retry instead of dropping the first words', () => {
    const h = setup(); h.run('press()');
    for (let i = 0; i < 26; i++) h.frame(i);
    assert.deepEqual(h.binary(), []);
    assert.equal(h.commands().at(-1), 'interrupt');
    assert.match(h.elements.get('avatar-notice').textContent, /too long to connect/);
});

test('hands-free interruption retains only the most recent three frames', async () => {
    const h = setup('hands-free'); h.run("setState('thinking')");
    for (let i = 0; i < 10; i++) h.frame(i);
    h.run('listen()'); await h.run("onMessage({ type: 'listening' }, epoch)");
    assert.deepEqual(h.binary(), [7, 8, 9]);
});

test('Ready during a pending microphone flush preserves its final frame before commit', async () => {
    const h = setup(); h.run('press()'); h.frame(1);
    h.run('let finishFlush; audio.flush = () => new Promise(resolve => { finishFlush = resolve; });');
    const released = h.run('release()');
    await h.run("onMessage({ type: 'listening' }, epoch)");
    assert.equal(h.commands().includes('commit'), false);
    h.frame(2); h.run('finishFlush()'); await released;
    assert.deepEqual(h.binary(), [1, 2]);
    assert.equal(h.commands().at(-1), 'commit');
});


test('current learning language is a label and starting submits the chosen quiz and default partner without a language override', async () => {
    const h = setup();
    h.run(`
        audio = null; ready = false; modelReady = true;
        fetch = async () => ({ ok: true, json: async () => ({
            available: true, language: 'pl', languageName: 'Polish', balance: 100,
            quizzes: [{ id: 'polish-quiz', name: 'Polish words' }],
            rates: { recognitionPerMinute: 5, replyPerThousandTokens: .08, speechPerThousandCharacters: 18.2258 }
        }) });
        AvatarAudio = class { async open() {} async close() {} };
        WebSocket = class { constructor(url) { wire({ socketUrl: String(url) }); } };
        api = async (path, body) => { wire({ path, body }); return { sessionId: 'session', connectUrl: '/api/avatar/sessions/session/voice' }; };
    `);
    await h.run('initialize()');
    assert.equal(h.elements.get('avatar-language').textContent, 'Practicing Polish');
    assert.equal(h.elements.get('avatar-language').options.length, 0);
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    h.elements.get('avatar-quiz').value = 'polish-quiz';
    await h.run('start()');
    const request = h.sent.find(x => x.path === '/api/avatar/sessions');
    assert.deepEqual(JSON.parse(JSON.stringify(request.body)), { quizId: 'polish-quiz', characterId: 'rain', memoryId: null });
    assert.equal(h.sent.at(-1).socketUrl, 'wss://localhost/api/avatar/sessions/session/voice');
    assert.equal(h.elements.get('avatar-quiz').disabled, true);
});

test('no current learning language keeps talking disabled and explains how to choose one', async () => {
    const h = setup();
    h.run(`
        audio = null; ready = false; modelReady = true;
        fetch = async () => ({ ok: true, json: async () => ({
            available: true, language: null, languageName: null, balance: 100, quizzes: [],
            rates: { recognitionPerMinute: 5, replyPerThousandTokens: .08, speechPerThousandCharacters: 18.2258 }
        }) });
    `);
    await h.run('initialize()');
    assert.equal(h.elements.get('avatar-talk').disabled, true);
    assert.match(h.elements.get('avatar-notice').textContent, /Choose a learning language in the sidebar/);
    assert.equal(h.elements.get('avatar-language').options.length, 0);
});


test('choosing Rowan disposes the previous model and submits the chosen partner', async () => {
    const h = setup();
    h.run(`
        audio = null; ready = false; config = { available: true, language: 'sv' };
        visual = { dispose() { wire({ disposed: 'rain' }); } };
        createAvatar = async (_, id) => { wire({ model: id }); return { setState() {}, setAnalyser() {}, dispose() {} }; };
        AvatarAudio = class { async open() {} async close() {} };
        WebSocket = class { constructor() {} };
        api = async (path, body) => { wire({ path, body }); return { sessionId: 'session', voiceName: 'Male Swedish', nativeVoice: true, connectUrl: '/voice' }; };
    `);
    h.elements.get('avatar-character').value = 'rowan';
    await h.run('chooseCharacter()');
    assert.equal(h.sent.filter(x => x.disposed === 'rain').length, 1);
    assert.equal(h.sent.filter(x => x.model === 'rowan').length, 1);
    assert.equal(h.elements.get('avatar-name').textContent, 'Rowan');
    assert.match(h.elements.get('avatar-voice').textContent, /male voice/);
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    await h.run('start()');
    assert.equal(h.sent.find(x => x.path === '/api/avatar/sessions').body.characterId, 'rowan');
    assert.equal(h.elements.get('avatar-character').disabled, true);
    h.elements.get('avatar-character').value = 'rain';
    await h.run('chooseCharacter()');
    assert.equal(h.elements.get('avatar-character').value, 'rowan');
    assert.equal(h.sent.filter(x => x.model).length, 1);
});

test('character loading blocks start and overlapping changes, then a failure allows another choice', async () => {
    const h = setup();
    h.run(`
        audio = null; ready = false; config = { available: true, language: 'sv' };
        let failModel;
        createAvatar = () => new Promise((_, reject) => { failModel = reject; });
        AvatarAudio = class { constructor() { throw new Error('Microphone must stay off while loading'); } };
    `);
    h.elements.get('avatar-character').value = 'rowan';
    const loading = h.run('chooseCharacter()');
    assert.equal(h.elements.get('avatar-talk').disabled, true);
    assert.equal(h.elements.get('avatar-character').disabled, true);
    await h.run('start()');
    h.elements.get('avatar-character').value = 'rain';
    await h.run('chooseCharacter()');
    assert.equal(h.elements.get('avatar-character').value, 'rowan');
    h.run("failModel(new Error('Failed model download'))"); await loading;
    assert.equal(h.elements.get('avatar-talk').disabled, true);
    assert.equal(h.elements.get('avatar-character').disabled, false);
    assert.match(h.elements.get('avatar-render-status').textContent, /Rowan could not load/);
    h.run('createAvatar = async () => ({ setState() {}, dispose() {} })');
    h.elements.get('avatar-character').value = 'rain';
    await h.run('chooseCharacter()');
    assert.equal(h.elements.get('avatar-name').textContent, 'Rain');
    assert.equal(h.elements.get('avatar-talk').disabled, false);
});


test('Back restoration rebuilds the selected partner and ignores an obsolete model load', async () => {
    const h = setup();
    h.run(`
        audio = null; ready = false; socket = null; config = { available: true, language: 'sv' };
        fetch = async () => ({ ok: true, json: async () => ({ available: true, language: 'sv',
            languageName: 'Swedish', balance: 100, quizzes: [],
            rates: { recognitionPerMinute: 5, replyPerThousandTokens: .08, speechPerThousandCharacters: 18 } }) });
        let finishOld, finishNew;
        createAvatar = () => new Promise(resolve => { finishOld = resolve; });
    `);
    h.elements.get('avatar-character').value = 'rowan';
    const oldLoad = h.run('chooseCharacter()');
    h.windowEvent('pagehide');
    assert.equal(h.elements.get('avatar-talk').disabled, true);
    h.run('createAvatar = () => new Promise(resolve => { finishNew = resolve; })');
    await h.windowEvent('pageshow', { persisted: true });
    h.run('finishOld({ dispose() { wire({ disposed: "obsolete" }); } })');
    await oldLoad;
    assert.equal(h.sent.filter(x => x.disposed === 'obsolete').length, 1);
    assert.equal(h.elements.get('avatar-character').disabled, true);
    assert.equal(h.elements.get('avatar-talk').disabled, true);
    h.run('finishNew({ setState() {}, dispose() {} })');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.elements.get('avatar-name').textContent, 'Rowan');
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    h.run('createAvatar = async () => ({ setState() {}, dispose() {} })');
    h.elements.get('avatar-character').value = 'rain';
    await h.run('chooseCharacter()');
    assert.equal(h.elements.get('avatar-name').textContent, 'Rain');
    assert.equal(h.elements.get('avatar-talk').disabled, false);
});


test('configuration refresh replaces quizzes and preserves only a still-available selection', async () => {
    const h = setup();
    h.run(`
        audio = null; ready = false; modelReady = true;
        let quizzes = [{ id: 'current', name: 'Current quiz' }, { id: 'deleted', name: 'Old quiz' }];
        fetch = async () => ({ ok: true, json: async () => ({ available: true, language: 'sv', languageName: 'Swedish',
            quizzes, balance: 100, rates: { recognitionPerMinute: 5, replyPerThousandTokens: .08, speechPerThousandCharacters: 18 } }) });
    `);
    await h.run('initialize()');
    h.elements.get('avatar-quiz').value = 'current';
    await h.run('initialize()');
    assert.deepEqual(h.elements.get('avatar-quiz').options.map(q => q.value), ['', 'current', 'deleted']);
    assert.equal(h.elements.get('avatar-quiz').value, 'current');
    h.run("quizzes = [{ id: 'new-language', name: 'New language quiz' }]");
    await h.run('initialize()');
    assert.deepEqual(h.elements.get('avatar-quiz').options.map(q => q.value), ['', 'new-language']);
    assert.equal(h.elements.get('avatar-quiz').value, '');
});

test('an older configuration response cannot overwrite the restored page language or quizzes', async () => {
    const h = setup();
    h.run(`
        let oldResponse;
        fetch = () => new Promise(resolve => { oldResponse = resolve; });
    `);
    const oldRequest = h.run('initialize()');
    h.run(`
        fetch = async () => ({ ok: true, json: async () => ({ available: true, language: 'sv', languageName: 'Swedish',
            quizzes: [{ id: 'swedish', name: 'Swedish quiz' }], balance: 100,
            rates: { recognitionPerMinute: 5, replyPerThousandTokens: .08, speechPerThousandCharacters: 18 } }) });
    `);
    await h.run('initialize()');
    h.run(`oldResponse({ ok: true, json: async () => ({ available: true, language: 'en', languageName: 'English',
        quizzes: [{ id: 'english', name: 'English quiz' }], balance: 100,
        rates: { recognitionPerMinute: 5, replyPerThousandTokens: .08, speechPerThousandCharacters: 18 } }) })`);
    await oldRequest;
    assert.equal(h.elements.get('avatar-language').textContent, 'Practicing Swedish');
    assert.deepEqual(h.elements.get('avatar-quiz').options.map(q => q.value), ['', 'swedish']);
});


test('lost connection pauses capture, clears pending audio and resumes muted without replay', async () => {
    const h = setup('hands-free');
    h.run(`audio.mute = value => wire({ muted: value }); turnId = 'old-turn';
        preRoll = [new ArrayBuffer(10)]; holding = true; awaitingCommit = true; currentReply = 'old reply';`);
    h.run('connectionLost()');
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.equal(h.elements.get('avatar-talk').title, 'Start over');
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'false');
    assert.equal(h.elements.get('avatar-status').textContent, 'Reconnecting');
    assert.equal(h.run('preRoll.length'), 0);
    assert.equal(h.run('turnId'), null);
    assert.equal(h.run('awaitingCommit'), false);
    await h.run("onMessage({ type: 'connected', resumed: true }, epoch)");
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.match(h.elements.get('avatar-notice').textContent, /Use the microphone to continue/);
    assert.deepEqual(h.commands(), []);
    h.frame(1); assert.deepEqual(h.binary(), []);
});

test('late usage from an ended session cannot overwrite a newer conversation', async () => {
    const h = setup();
    h.run(`let finishUsage; requestJson = () => new Promise(resolve => { finishUsage = resolve; });`);
    const pending = h.run("refreshUsage('old-session', epoch)");
    h.run("epoch++; el('spending').textContent = 'new usage';");
    h.run("finishUsage({ type: 'usage', available: 1, total: 99, pending: false })");
    await pending;
    assert.equal(h.elements.get('avatar-spending').textContent, 'new usage');
});

test('End is single-flight and remains usable after failed cleanup', async () => {
    const h = setup();
    h.run(`sessionId = 'ending-session'; modelReady = true; config = { available: true, language: 'sv' };
        audio.close = async () => { wire({ closed: true }); };
        socket.close = () => { wire({ closedSocket: true }); };
        let finishEnd; api = () => new Promise(resolve => { finishEnd = resolve; });
        requestJson = async () => { throw new Error('offline'); };`);
    const first = h.run('end()'), second = h.run('end()');
    assert.equal(first, second);
    assert.equal(h.elements.get('avatar-talk').disabled, true);
    await new Promise(resolve => setImmediate(resolve));
    h.run('finishEnd()'); await first;
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.equal(h.sent.filter(x => x.closed).length, 1);
    assert.equal(h.sent.filter(x => x.closedSocket).length, 1);
});


test('a failed interrupt keeps the reconnecting state and paused microphone', () => {
    const h = setup();
    h.run('audio.mute = () => {}; socket.send = () => { connectionLost(); return false; }; interrupt();');
    assert.equal(h.elements.get('avatar-status').textContent, 'Reconnecting');
    assert.equal(h.run('muted'), true);
    assert.equal(h.elements.get('avatar-talk').title, 'Start over');
});

test('stalled HTTP cleanup aborts at its deadline and releases the talk control', async () => {
    const h = setup();
    h.run(`sessionId = 'offline-session'; modelReady = true; config = { available: true, language: 'sv' };
        audio.close = async () => {}; socket.close = () => {};
        let expireRequest; setTimeout = (action, delay) => { expireRequest = action; wire({ deadline: delay }); return 1; };
        clearTimeout = () => {};
        fetch = (_, options) => new Promise((_, reject) => options.signal.addEventListener('abort', () => reject(new Error('aborted'))));`);
    const cleanup = h.run('end()');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.elements.get('avatar-talk').disabled, true);
    assert.equal(h.sent.find(x => x.deadline).deadline, 5000);
    h.run('expireRequest()'); await cleanup;
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.equal(h.run('audio'), null);
});

for (const outcome of ['resolve', 'reject']) {
    test(`a stale microphone flush (${outcome}) cannot overwrite recovery or commit a new turn`, async () => {
        const h = setup();
        h.run(`audio.mute = () => {}; let resolveOld, rejectOld;
            audio.flush = () => new Promise((resolve, reject) => { resolveOld = resolve; rejectOld = reject; }); press();`);
        const oldRelease = h.run('release()');
        h.run('connectionLost()');
        const sentBeforeRecovery = h.commands().length;
        if (outcome === 'reject') {
            h.run("rejectOld(new Error('flush timeout'))");
            await oldRelease;
            assert.equal(h.elements.get('avatar-status').textContent, 'Reconnecting');
            assert.match(h.elements.get('avatar-notice').textContent, /Connection lost/);
            assert.equal(h.commands().length, sentBeforeRecovery);
        }
        await h.run("onMessage({ type: 'connected', resumed: true }, epoch)");
        h.run(`muted = false; let finishNew;
            audio.flush = () => new Promise(resolve => { finishNew = resolve; }); press();`);
        await h.run("onMessage({ type: 'listening' }, epoch)");
        const newRelease = h.run('release()');
        if (outcome === 'resolve') { h.run('resolveOld()'); await oldRelease; }
        assert.equal(h.run('flushing'), true);
        assert.equal(h.elements.get('avatar-status').textContent, 'Listening');
        assert.equal(h.commands().includes('interrupt'), false);
        assert.equal(h.commands().includes('commit'), false);
        h.run('finishNew()'); await newRelease;
        assert.equal(h.commands().filter(command => command === 'commit').length, 1);
    });
}

test('a late failed flush after reconnection cannot interrupt the replacement turn', async () => {
    const h = setup();
    h.run(`audio.mute = () => {}; let rejectOld;
        audio.flush = () => new Promise((_, reject) => { rejectOld = reject; }); press();`);
    const oldRelease = h.run('release()');
    h.run('connectionLost()');
    await h.run("onMessage({ type: 'connected', resumed: true }, epoch)");
    h.run('muted = false; press();');
    await h.run("onMessage({ type: 'listening' }, epoch)");
    h.run("rejectOld(new Error('late timeout'))"); await oldRelease;
    assert.equal(h.elements.get('avatar-status').textContent, 'Listening');
    assert.equal(h.commands().includes('interrupt'), false);
    assert.equal(h.commands().includes('commit'), false);
});


for (const mode of ['push-to-talk', 'hands-free']) {
    test(`switching tabs preserves the ${mode} conversation, microphone and playback`, async () => {
        const h = setup(mode);
        h.run(`sessionId = 'ongoing-session'; setState('speaking');
            audio.close = () => wire({ closedAudio: true }); audio.mute = () => wire({ muted: true });
            audio.stop = () => wire({ stoppedAudio: true }); socket.close = () => wire({ closedSocket: true });
            api = async path => wire({ endedSession: path });`);
        const epoch = h.run('epoch');
        h.run('document.hidden = true'); h.documentEvent('visibilitychange');
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(h.run('sessionId'), 'ongoing-session');
        assert.equal(h.run('epoch'), epoch);
        assert.equal(h.run('ready'), true);
        assert.equal(h.run('muted'), false);
        assert.equal(h.elements.get('avatar-status').textContent, 'Speaking');
        assert.deepEqual(h.sent, []);
        h.run('document.hidden = false'); h.documentEvent('visibilitychange');
        assert.equal(h.run('sessionId'), 'ongoing-session');
        assert.deepEqual(h.sent, []);
    });
}

test('switching tabs releases held push-to-talk without ending the session', async () => {
    const h = setup();
    h.run("sessionId = 'ongoing-session'; press();");
    await h.run("onMessage({ type: 'listening' }, epoch)");
    h.frame(1);
    h.run('document.hidden = true'); h.documentEvent('visibilitychange');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(h.run('holding'), false);
    assert.equal(h.run('sessionId'), 'ongoing-session');
    assert.equal(h.run('ready'), true);
    assert.deepEqual(h.binary(), [1]);
    assert.deepEqual(h.commands(), ['listen', 'commit']);
    assert.equal(h.elements.get('avatar-status').textContent, 'Thinking');
});

function prepareFirstPress(h) {
    h.run(`audio = null; ready = false; socket = null; modelReady = true;
        config = { available: true, language: 'sv' };
        AvatarAudio = class {
            async open() { wire({ microphoneOpened: true }); }
            async close() { wire({ microphoneClosed: true }); }
            async flush() {} stop() {} mute() {}
        };
        WebSocket = class { send(value) { wire(value); } close() {} };
        let finishStart;
        api = (path, body) => {
            wire({ path, body });
            return new Promise(resolve => { finishStart = resolve; });
        };
        refreshStart();`);
}
const tick = () => new Promise(resolve => setImmediate(resolve));

test('the first talk press starts once and preserves a short utterance through HTTP and voice setup', async () => {
    const h = setup(); prepareFirstPress(h);
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.deepEqual(h.sent, []);
    h.run('press(); press();'); await tick();
    assert.equal(h.sent.filter(x => x.microphoneOpened).length, 1);
    assert.equal(h.sent.filter(x => x.path === '/api/avatar/sessions').length, 1);
    assert.equal(h.elements.get('avatar-quiz').disabled, true);
    h.frame(1); h.frame(2); await h.run('release()'); h.frame(3);
    h.run("finishStart({ sessionId: 'session', memoryId: 'memory', connectUrl: '/voice', rememberedTurns: 0 })");
    await tick();
    await h.run("connectionHandlers.onMessage({ type: 'connected' })");
    assert.deepEqual(h.commands(), ['listen']);
    await h.run("connectionHandlers.onMessage({ type: 'listening' })");
    assert.deepEqual(h.binary(), [1, 2]);
    assert.deepEqual(h.commands(), ['listen', 'commit']);
    assert.equal(h.run('memory.get(selection())'), 'memory');
});

test('the first connection waits for a pending microphone flush before committing', async () => {
    const h = setup(); prepareFirstPress(h);
    h.run('press()'); await tick(); h.frame(1);
    h.run('let finishFlush; audio.flush = () => new Promise(resolve => { finishFlush = resolve; });');
    const released = h.run('release()');
    h.run("finishStart({ sessionId: 'session', connectUrl: '/voice' })"); await tick();
    await h.run("connectionHandlers.onMessage({ type: 'connected' })");
    await h.run("connectionHandlers.onMessage({ type: 'listening' })");
    assert.equal(h.commands().includes('commit'), false);
    h.frame(2); h.run('finishFlush()'); await released;
    assert.deepEqual(h.binary(), [1, 2]);
    assert.deepEqual(h.commands(), ['listen', 'commit']);
});

test('denied microphone permission leaves the talk button available for an explicit retry', async () => {
    const h = setup(); prepareFirstPress(h);
    h.run("AvatarAudio.prototype.open = async () => { const error = new Error(); error.name = 'NotAllowedError'; throw error; }; press();");
    await tick();
    assert.match(h.elements.get('avatar-notice').textContent, /Allow microphone access/);
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.equal(h.elements.get('avatar-quiz').disabled, false);
    assert.equal(h.sent.filter(x => x.path).length, 0);
    assert.equal(h.sent.filter(x => x.microphoneClosed).length, 1);
});

test('returning to the same selection submits its memory and changing selection clears it', async () => {
    const h = setup(); prepareFirstPress(h);
    h.run("memory.save(selection(), 'prior-memory'); press();"); await tick();
    assert.equal(h.sent.find(x => x.path === '/api/avatar/sessions').body.memoryId, 'prior-memory');
    h.run("finishStart({ sessionId: 'session', memoryId: 'prior-memory', connectUrl: '/voice', rememberedTurns: 4 })"); await tick();
    h.run("api = async path => { wire({ path }); }; requestJson = async () => { throw Error('offline'); };");
    await h.run('end()');
    h.elements.get('avatar-quiz').value = 'new-quiz';
    h.run('selectMemory()');
    assert.equal(h.sent.filter(x => x.path === '/api/avatar/memories/prior-memory/forget').length, 1);
    h.elements.get('avatar-quiz').value = '';
    h.run('selectMemory()');
    assert.equal(h.run('memory.get(selection())'), undefined);
});

test('leaving the actual page stops capture but retains memory for the next visit', async () => {
    const h = setup(); prepareFirstPress(h);
    h.run(`memory.save(selection(), 'memory'); sessionId = 'session'; ready = true;
        audio = new AvatarAudio(); socket = { close() { wire({ socketClosed: true }); } };
        api = async path => wire({ path }); requestJson = async () => { throw Error('offline'); };`);
    h.windowEvent('pagehide'); await h.run('pageCleanup');
    assert.equal(h.sent.filter(x => x.microphoneClosed).length, 1);
    assert.equal(h.sent.filter(x => x.socketClosed).length, 1);
    assert.equal(h.sent.filter(x => x.path === '/api/avatar/sessions/session/end').length, 1);
    assert.equal(h.run("memory.get({ language: 'sv', characterId: 'rain', quizId: null })"), 'memory');
});


for (const speakingMode of ['push-to-talk', 'hands-free']) {
    test(`the microphone resumes ${speakingMode} after reconnect without a separate Unmute control`, async () => {
        const h = setup(speakingMode);
        h.run('audio.mute = value => wire({ muted: value }); connectionLost();');
        await h.run("onMessage({ type: 'connected', resumed: true }, epoch)");
        assert.equal(h.run('muted'), true);
        assert.deepEqual(h.commands(), []);
        h.run('press()');
        assert.equal(h.run('muted'), false);
        assert.equal(h.sent.at(-2).muted, false);
        assert.deepEqual(h.commands(), ['listen']);
        assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'true');
    });
}

test('pointer hold records and release sends once, including lost capture after release', async () => {
    const h = setup();
    h.buttonEvent('pointerdown', { isPrimary: true, button: 0, pointerId: 1, preventDefault() {} });
    assert.equal(h.elements.get('avatar-talk').attributes['aria-label'], 'Hold to record');
    assert.equal(h.elements.get('avatar-talk').title, 'Release to send');
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'true');
    await h.run("onMessage({ type: 'listening' }, epoch)"); h.frame(1);
    await h.buttonEvent('pointerup'); await h.buttonEvent('lostpointercapture');
    h.frame(2);
    assert.deepEqual(h.binary(), [1]);
    assert.deepEqual(h.commands(), ['listen', 'commit']);
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'false');
    assert.equal(h.elements.get('avatar-talk').attributes['aria-label'], 'Hold to record');
    assert.equal(h.elements.get('avatar-talk').title, 'Hold to record');
});

test('secondary pointers cannot start recording and keyboard focus loss releases a hold', async () => {
    const h = setup();
    h.buttonEvent('pointerdown', { isPrimary: false, button: 0 });
    h.buttonEvent('pointerdown', { isPrimary: true, button: 2 });
    assert.deepEqual(h.commands(), []);
    h.buttonEvent('keydown', { code: 'Space', repeat: false, preventDefault() {} });
    await h.run("onMessage({ type: 'listening' }, epoch)"); h.frame(1);
    await h.buttonEvent('blur');
    assert.deepEqual(h.commands(), ['listen', 'commit']);
    assert.equal(h.run('holding'), false);
});

test('the single microphone can pause and resume explicitly selected hands-free capture', () => {
    const h = setup('hands-free');
    h.run("audio.mute = value => wire({ muted: value }); setState('listening'); press();");
    assert.equal(h.run('muted'), true);
    assert.equal(h.elements.get('avatar-talk').attributes['aria-label'], 'Microphone');
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'false');
    assert.equal(h.elements.get('avatar-talk').title, 'Start microphone');
    h.frame(1); assert.deepEqual(h.binary(), []);
    h.run('press()');
    assert.equal(h.run('muted'), false);
    assert.equal(h.elements.get('avatar-talk').attributes['aria-label'], 'Microphone');
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'true');
    assert.equal(h.elements.get('avatar-talk').title, 'Pause microphone');
    assert.deepEqual(h.commands(), ['interrupt', 'listen']);
});

test('changing partner closes active capture and loads the new partner without End', async () => {
    const h = setup();
    h.run(`config = { available: true, language: 'sv' }; sessionId = 'session'; modelReady = true;
        memory.save(selection(), 'old-memory');
        audio.close = async () => wire({ microphoneClosed: true }); socket.close = () => wire({ socketClosed: true });
        api = async path => wire({ path }); requestJson = async () => { throw Error('offline'); };
        createAvatar = async (_, id) => { wire({ model: id }); return { setState() {}, dispose() {} }; };
        refreshStart();`);
    assert.equal(h.elements.get('avatar-character').disabled, false);
    h.elements.get('avatar-character').value = 'rowan';
    await h.run('chooseCharacter()');
    assert.equal(h.sent.filter(x => x.microphoneClosed).length, 1);
    assert.equal(h.sent.filter(x => x.path === '/api/avatar/sessions/session/end').length, 1);
    assert.equal(h.sent.filter(x => x.path === '/api/avatar/memories/old-memory/forget').length, 1);
    assert.equal(h.elements.get('avatar-name').textContent, 'Rowan');
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.equal(h.run('sessionId'), null);
});

test('changing quiz closes active capture and blocks recording until cleanup completes', async () => {
    const h = setup();
    h.run(`config = { available: true, language: 'sv' }; sessionId = 'session'; modelReady = true;
        memory.save(selection(), 'old-memory');
        audio.close = async () => {}; socket.close = () => {};
        let finishCleanup;
        api = (path) => { wire({ path }); return path.endsWith('/end') ? new Promise(resolve => { finishCleanup = resolve; }) : Promise.resolve(); };
        requestJson = async () => { throw Error('offline'); }; refreshStart();`);
    h.elements.get('avatar-quiz').value = 'quiz';
    const change = h.run('chooseQuiz()'); await tick();
    assert.equal(h.elements.get('avatar-talk').disabled, true);
    h.run('press()'); assert.deepEqual(h.commands(), []);
    h.run('finishCleanup()'); await change;
    assert.equal(h.sent.filter(x => x.path === '/api/avatar/memories/old-memory/forget').length, 1);
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.equal(h.run('sessionId'), null);
});

for (const replyState of ['thinking', 'speaking']) {
    test(`pausing hands-free while ${replyState} keeps the reply and waits for the microphone`, async () => {
        const h = setup('hands-free');
        h.run(`audio.mute = value => wire({ muted: value }); audio.stop = () => wire({ stoppedAudio: true });
            turnId = 'turn'; setState('${replyState}'); press();`);
        assert.equal(h.run('muted'), true);
        assert.equal(h.run('turnId'), 'turn');
        assert.deepEqual(h.commands(), []);
        assert.equal(h.sent.filter(x => x.stoppedAudio).length, 0);
        assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'false');
        await h.run("onMessage({ type: 'idle' }, epoch)");
        assert.equal(h.elements.get('avatar-status').textContent, 'Microphone muted');
        assert.deepEqual(h.commands(), []);
        h.run('press()');
        assert.deepEqual(h.commands(), ['listen']);
    });
}

test('unpausing hands-free during a reply re-enables interruption without cutting it off', async () => {
    const h = setup('hands-free');
    h.run(`audio.mute = () => {}; audio.stop = () => wire({ stoppedAudio: true });
        turnId = 'turn'; setState('speaking'); press(); press();`);
    assert.equal(h.run('muted'), false);
    assert.equal(h.elements.get('avatar-status').textContent, 'Speaking');
    assert.deepEqual(h.commands(), []);
    assert.equal(h.sent.filter(x => x.stoppedAudio).length, 0);
    await h.run("onMessage({ type: 'idle' }, epoch)");
    assert.deepEqual(h.commands(), ['listen']);
});

test('pausing hands-free when its interrupt cannot be sent keeps the reconnecting state', () => {
    const h = setup('hands-free');
    h.run("audio.mute = () => {}; setState('listening'); socket.send = () => { connectionLost(); return false; }; press();");
    assert.equal(h.elements.get('avatar-status').textContent, 'Reconnecting');
    assert.equal(h.run('ready'), false);
});

test('a hands-free listening error shows the microphone as off until it is retried', async () => {
    const h = setup('hands-free');
    h.run("audio.mute = () => {}; turnId = 'turn'; setState('listening');");
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'true');
    await h.run("onMessage({ type: 'error', turnId: 'turn', detail: 'Listening paused after 45 seconds.' }, epoch)");
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'false');
    assert.equal(h.elements.get('avatar-talk').title, 'Start microphone');
    h.run('press()');
    assert.deepEqual(h.commands(), ['listen']);
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'true');
});

test('choosing Hands-free in settings does not open the microphone before the button is used', async () => {
    const h = setup(); prepareFirstPress(h);
    h.chooseMode('hands-free'); await tick();
    assert.equal(h.sent.filter(x => x.microphoneOpened).length, 0);
    assert.equal(h.sent.filter(x => x.path).length, 0);
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'false');
    h.run('press()'); await tick();
    assert.equal(h.sent.filter(x => x.microphoneOpened).length, 1);
    assert.equal(h.sent.filter(x => x.path === '/api/avatar/sessions').length, 1);
});

function loseConnection(h) {
    prepareFirstPress(h);
    h.run(`memory.save(selection(), 'memory'); sessionId = 'stale'; ready = true; audio = new AvatarAudio();
        socket = { send: () => false, close() { wire({ socketClosed: true }); } };
        api = (path, body) => { wire({ path, body }); return path.endsWith('/end') ? Promise.resolve() : new Promise(resolve => { finishStart = resolve; }); };
        requestJson = async () => { throw Error('offline'); };
        connectionLost();`);
}
const hold = { isPrimary: true, button: 0, pointerId: 1, preventDefault() {} };

test('holding the microphone while reconnecting starts over with remembered context and records that hold', async () => {
    const h = setup(); loseConnection(h);
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.match(h.elements.get('avatar-notice').textContent, /start over/);
    h.buttonEvent('pointerdown', hold);
    // Cleanup of the stale session must not disable the button under the held pointer.
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'true');
    h.run('press()'); await tick(); await tick();
    assert.equal(h.sent.filter(x => x.socketClosed).length, 1);
    assert.equal(h.sent.filter(x => x.microphoneClosed).length, 1);
    assert.equal(h.sent.filter(x => x.path === '/api/avatar/sessions/stale/end').length, 1);
    const starts = h.sent.filter(x => x.path === '/api/avatar/sessions');
    assert.equal(starts.length, 1);
    assert.equal(starts[0].body.memoryId, 'memory');
    h.frame(1); h.frame(2);
    h.run("finishStart({ sessionId: 'fresh', memoryId: 'memory', connectUrl: '/voice' })"); await tick();
    await h.run("connectionHandlers.onMessage({ type: 'connected' })");
    assert.equal(h.run('sessionId'), 'fresh');
    assert.deepEqual(h.commands(), ['listen']);
    h.frame(3); await h.buttonEvent('pointerup');
    await h.run("connectionHandlers.onMessage({ type: 'listening' })");
    assert.deepEqual(h.binary(), [1, 2, 3]);
    assert.deepEqual(h.commands(), ['listen', 'commit']);
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'false');
});

test('releasing before the restarted microphone opens records nothing and leaves the new session ready', async () => {
    const h = setup(); loseConnection(h);
    h.buttonEvent('pointerdown', hold); await h.buttonEvent('pointerup');
    assert.equal(h.run('holding'), false);
    await tick(); await tick();
    h.frame(1);
    h.run("finishStart({ sessionId: 'fresh', memoryId: 'memory', connectUrl: '/voice' })"); await tick();
    await h.run("connectionHandlers.onMessage({ type: 'connected' })");
    assert.deepEqual(h.binary(), []);
    assert.deepEqual(h.commands(), []);
    assert.equal(h.elements.get('avatar-status').textContent, 'Your turn');
    assert.equal(h.elements.get('avatar-talk').disabled, false);
    assert.equal(h.elements.get('avatar-talk').attributes['aria-pressed'], 'false');
});

test('starting over in hands-free listens on the new session without a second press', async () => {
    const h = setup('hands-free'); loseConnection(h);
    h.run('press()'); await tick(); await tick();
    assert.equal(h.run('holding'), false);
    h.run("finishStart({ sessionId: 'fresh', memoryId: 'memory', connectUrl: '/voice' })"); await tick();
    await h.run("connectionHandlers.onMessage({ type: 'connected' })");
    assert.equal(h.run('muted'), false);
    assert.deepEqual(h.commands(), ['listen']);
});

test('the first connection keeps a short press whose only audio is still being flushed', async () => {
    const h = setup(); prepareFirstPress(h);
    h.run('press()'); await tick();
    h.run('let finishFlush; audio.flush = () => new Promise(resolve => { finishFlush = resolve; });');
    const released = h.run('release()');
    h.run("finishStart({ sessionId: 'session', connectUrl: '/voice' })"); await tick();
    await h.run("connectionHandlers.onMessage({ type: 'connected' })");
    assert.deepEqual(h.commands(), ['listen']);
    h.frame(1); h.run('finishFlush()'); await released;
    await h.run("connectionHandlers.onMessage({ type: 'listening' })");
    assert.deepEqual(h.binary(), [1]);
    assert.deepEqual(h.commands(), ['listen', 'commit']);
});
