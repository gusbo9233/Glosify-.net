import { AvatarAudio } from './audio.js';
import { createConversationMemory } from './memory.js';
import { VoiceConnection } from './connection.js';
import { createAvatar } from './scene.min.js';

const el = name => document.getElementById('avatar-' + name);
let sessionStore;
try { sessionStore = globalThis.sessionStorage; } catch { /* Storage may be disabled. */ }
const memory = createConversationMemory(document.body?.dataset.speechUser, sessionStore);
const preferences = memory.preferences();
const characters = { rain: { name: 'Rain', gender: 'female' }, rowan: { name: 'Rowan', gender: 'male' } };
let characterId = Object.hasOwn(characters, preferences?.characterId) ? preferences.characterId : 'rain', characterLoading = false, pageLeaving = false, modelEpoch = 0, configEpoch = 0, pageCleanup = Promise.resolve();
const character = () => characters[characterId];
const number = value => new Intl.NumberFormat(undefined, { maximumFractionDigits: 4 }).format(value);
let ending = null, changingQuiz = false, captureEpoch = 0;
let config, visual, socket, audio, sessionId, turnId, state = 'off', muted = false, holding = false, epoch = 0, speechFrames = 0;
let preRoll = [], awaitingCommit = false, flushing = false, ready = false, modelReady = false, currentReply = '', cancelledTurn = null;
const mode = () => document.querySelector('input[name="avatar-mode"]:checked').value;
const selection = () => ({ language: config?.language, characterId, quizId: el('quiz').value || null });
const canStart = () => !!config?.available && !!config?.language && modelReady && !sessionId && !audio && !ending && !changingQuiz && !pageLeaving;
const token = document.querySelector('#avatar-page input[name="__RequestVerificationToken"]').value;
function send(value) { return socket ? socket.send(JSON.stringify(value)) !== false : false; }
function setState(value) {
    state = value;
    const labels = { off: 'Ready when you are', connecting: 'Connecting', reconnecting: 'Reconnecting', listening: 'Listening', thinking: 'Thinking', speaking: 'Speaking', idle: muted ? 'Microphone muted' : 'Your turn', error: 'Try again' };
    el('status').textContent = labels[value] || value;
    visual?.setState(value);
    refreshStart();
}
function toggleSettings(open) {
    const toggle = el('settings-toggle');
    el('settings').hidden = !open;
    el('workspace').classList.toggle('settings-closed', !open);
    toggle.setAttribute('aria-expanded', String(open));
    toggle.setAttribute('aria-label', `${open ? 'Close' : 'Open'} conversation settings`);
    toggle.title = `${open ? 'Close' : 'Open'} settings`;
}
el('settings-toggle').addEventListener('click', () => toggleSettings(el('settings').hidden));
el('settings').addEventListener('keydown', event => {
    if (event.key === 'Escape') {
        event.preventDefault(); toggleSettings(false); el('settings-toggle').focus();
    }
});
function notice(text = '') {
    el('notice').textContent = text;
    if (text) toggleSettings(true);
}
function caption(text, speaker = 'assistant') {
    el('caption').textContent = text; el('caption').dataset.speaker = speaker;
    el('caption').hidden = !text.trim();
}
function transcript(speaker, text) {
    const item = document.createElement('li'), who = document.createElement('strong');
    who.textContent = speaker + ' '; item.append(who, document.createTextNode(text));
    el('transcript').append(item);
    while (el('transcript').children.length > 48) el('transcript').firstChild.remove();
}
function refreshStart() {
    const handsFree = mode() === 'hands-free', reconnecting = state === 'reconnecting';
    // A hands-free error stops listening until the explicit retry.
    const recording = holding || (handsFree && !!audio && !muted && state !== 'error');
    // The name stays fixed so assistive technology reads recording from aria-pressed.
    const label = handsFree ? 'Microphone' : 'Hold to record';
    // An active hold keeps the button enabled, even while starting over, so its release arrives.
    el('talk').disabled = !holding && (!!ending || changingQuiz || pageLeaving || !(ready || canStart() || reconnecting));
    el('talk').setAttribute('aria-label', label);
    el('talk').setAttribute('aria-pressed', String(recording));
    el('talk').title = reconnecting ? 'Start over' : holding ? 'Release to send'
        : handsFree ? (recording ? 'Pause microphone' : 'Start microphone') : label;
    el('quiz').disabled = !!ending || changingQuiz || characterLoading || (!!audio && !ready) || pageLeaving;
    el('character').disabled = characterLoading || !!ending || changingQuiz || (!!audio && !ready) || pageLeaving;
}
async function chooseCharacter() {
    if (pageLeaving || characterLoading || el('character').disabled) {
        el('character').value = characterId; return;
    }
    const selected = el('character').value;
    characterId = Object.hasOwn(characters, selected) ? selected : 'rain';
    el('character').value = characterId;
    const generation = ++modelEpoch;
    characterLoading = true; modelReady = false; refreshStart();
    if (audio || sessionId) await end();
    if (pageLeaving || generation !== modelEpoch) return;
    if (config) selectMemory();
    visual?.dispose(); visual = null;
    caption('');
    el('name').textContent = character().name;
    el('canvas').setAttribute('aria-label', `${character().name}, your animated conversation partner`);
    el('voice').textContent = `A ${character().gender} voice matched to your learning language.`;
    el('render-status').hidden = false;
    el('render-status').textContent = `Getting ${character().name} ready…`;
    el('status').textContent = 'Getting ready';
    try {
        const next = await createAvatar(el('canvas'), characterId);
        if (pageLeaving || generation !== modelEpoch) { next.dispose(); return; }
        visual = next; modelReady = true; el('render-status').hidden = true; setState('off');
    } catch {
        if (generation !== modelEpoch) return;
        el('render-status').textContent = `${character().name} could not load. Choose another partner or refresh to retry.`;
        el('status').textContent = 'Could not load';
    } finally {
        if (generation === modelEpoch) { characterLoading = false; refreshStart(); }
    }
}
el('character').addEventListener('change', () => { void chooseCharacter(); });
async function requestJson(path, options = {}, timeout = 10000) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeout);
    try {
        const response = await fetch(path, { credentials: 'same-origin', ...options, signal: controller.signal });
        if (!response.ok) {
            const problem = await response.json().catch(() => ({}));
            throw new Error(problem.detail || problem.title || 'The voice request failed. Please retry.');
        }
        return response.status === 204 ? null : await response.json();
    } catch (error) {
        if (controller.signal.aborted) throw new Error('Voice connection timed out. Please try again.', { cause: error });
        throw error;
    } finally { clearTimeout(timer); }
}
function api(path, body, timeout) {
    return requestJson(path, { method: 'POST', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify(body) }, timeout);
}
function selectMemory() {
    const discarded = memory.select(selection());
    if (!discarded) return;
    el('transcript').replaceChildren(); caption('');
    // Clear the reference immediately, including offline; an old selection must
    // never recover its previous context. Server memory also expires on its own.
    void api(`/api/avatar/memories/${discarded}/forget`, {}).catch(() => {});
}
function connectionLost() {
    ++captureEpoch;
    ready = false; holding = false; awaitingCommit = false; flushing = false;
    preRoll = []; speechFrames = 0; cancelledTurn = turnId; turnId = null; currentReply = '';
    audio?.stop(); audio?.mute(true); muted = true;
    caption(''); setState('reconnecting');
    notice('Connection lost. Reconnecting… Use the microphone to start over instead. Interrupted audio will not be replayed.');
}
function listen(preserveCapture = false) {
    if (!ready || muted) return;
    if (!preserveCapture) { ++captureEpoch; flushing = false; }
    cancelledTurn = turnId; turnId = null; currentReply = ''; audio?.stop();
    setState('connecting'); send({ type: 'listen', mode: mode() });
}
function interrupt() {
    ++captureEpoch; flushing = false;
    cancelledTurn = turnId; turnId = null; currentReply = ''; audio?.stop();
    awaitingCommit = false; preRoll = [];
    if (send({ type: 'interrupt' })) setState('idle');
}
function bufferFrame(pcm) {
    if (preRoll.length >= 25) {
        holding = false;
        if (ready) interrupt();
        else { ++captureEpoch; awaitingCommit = false; flushing = false; preRoll = []; }
        setState('error'); notice('Voice is taking too long to connect. Please retry the microphone.');
        return;
    }
    preRoll.push(pcm);
}
function frame({ pcm, rms }) {
    if (muted) return;
    if (!ready) {
        if (audio && mode() === 'push-to-talk' && (holding || flushing)) bufferFrame(pcm);
        return;
    }
    visual?.hear(rms);
    if (state === 'listening') {
        if (mode() !== 'push-to-talk' || holding || flushing) socket.send(pcm);
        preRoll = []; return;
    }
    if (mode() === 'push-to-talk' && state === 'connecting') {
        if (!holding && !flushing) return;
        // Preserve the entire onset (up to five seconds), leaving room in the
        // server's 32-frame queue for live frames and the commit marker.
        bufferFrame(pcm);
    } else {
        preRoll.push(pcm); if (preRoll.length > 3) preRoll.shift();
    }
    if (mode() === 'hands-free' && (state === 'speaking' || state === 'thinking')) {
        // Browser echo cancellation plus two voiced frames prevents most playback
        // leakage from interrupting Rain. The retained onset is sent after Ready.
        speechFrames = rms > .045 ? speechFrames + 1 : 0;
        if (speechFrames >= 2) { speechFrames = 0; listen(); }
    }
}
async function onMessage(message, generation) {
    if (generation !== epoch) return;
    if (message.type === 'connected') {
        ready = true; setState('idle');
        if (message.resumed) {
            notice('Connection restored. Use the microphone to continue; please repeat the interrupted turn.');
        }
        if (mode() === 'hands-free' && !muted) listen();
        // A pending flush may still deliver the only audio of a short first press.
        else if (holding || (awaitingCommit && (preRoll.length || flushing))) listen(true);
        else { awaitingCommit = false; refreshStart(); }
        return;
    }
    if (message.type === 'usage') {
        el('spending').textContent = `Current session · ${number(message.total)} credits · Listening ${number(message.recognition)} / Replies ${number(message.replies)} / Speech ${number(message.speech)}`;
        document.querySelectorAll('[data-credit-balance]').forEach(span => {
            span.textContent = span.dataset.creditLabel.replace('__COUNT__', number(Math.ceil(message.available)));
        });
        return;
    }
    if (message.type === 'connecting') { turnId = message.turnId; setState('connecting'); return; }
    if (message.turnId && (message.turnId !== turnId || message.turnId === cancelledTurn)) return;
    switch (message.type) {
        case 'listening':
            setState('listening');
            if (mode() === 'push-to-talk' && !holding && !awaitingCommit) { interrupt(); break; }
            for (const pcm of preRoll) { if (socket.send(pcm) === false) break; } preRoll = [];
            if (awaitingCommit && !flushing) commit();
            break;
        case 'partial': caption(message.text, 'user'); break;
        case 'transcript': caption(message.text, 'user'); transcript('You', message.text); break;
        case 'thinking': setState('thinking'); break;
        case 'reply': currentReply = message.text; caption(message.text); visual?.setReply(message.text); setState('speaking'); break;
        case 'audio': audio?.play(message.pcm); break;
        case 'audio-end': {
            const id = turnId, text = currentReply;
            if (await audio.finished() && generation === epoch && id === turnId) {
                transcript(character().name, text); send({ type: 'played', turnId: id });
            }
            break;
        }
        case 'idle':
            // An interrupt acknowledgement can arrive before the replacement turn.
            if (state === 'connecting') break;
            setState('idle'); if (mode() === 'hands-free' && !muted) listen(); break;
        case 'error':
            audio?.stop(); setState('error'); notice(message.detail);
            // Explicit retry avoids an unattended loop of chargeable failed requests.
            break;
    }
}
async function start() {
    if (!canStart()) return;
    const generation = ++epoch; ready = false; notice(); caption(''); setState('connecting');
    el('spending').textContent = 'Current session · 0 credits';
    const context = selection();
    const nextAudio = new AvatarAudio(); audio = nextAudio; refreshStart();
    try {
        await nextAudio.open(frame);
        if (generation !== epoch) { await nextAudio.close(); return; }
        visual?.setAnalyser(nextAudio.analyser);
        const result = await api('/api/avatar/sessions', { quizId: context.quizId, characterId, memoryId: memory.get(context) || null });
        if (generation !== epoch) { await api(`/api/avatar/sessions/${result.sessionId}/end`, {}); return; }
        sessionId = result.sessionId;
        memory.save(context, result.memoryId);
        el('voice').textContent = `${result.voiceName} · ${result.nativeVoice ? "Native" : "Multilingual"} voice`;
        const url = new URL(result.connectUrl, location.href); url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        socket = new VoiceConnection(url, {
            onMessage: message => generation === epoch ? onMessage(message, generation) : undefined,
            onLost: () => { if (generation === epoch) connectionLost(); },
            onEnded: detail => { if (generation === epoch) { notice(detail); void end(); } }
        });
        refreshStart();
    } catch (error) {
        if (generation !== epoch) return;
        notice(error.name === 'NotAllowedError' ? 'Allow microphone access to start a conversation.' : error.message);
        await end();
    }
}
function end() {
    if (ending) return ending;
    const generation = ++epoch;
    ++captureEpoch;
    ready = false; holding = false; awaitingCommit = false; flushing = false;
    preRoll = []; speechFrames = 0; muted = false; turnId = null;
    const oldSession = sessionId, oldAudio = audio; sessionId = null; audio = null;
    socket?.close(); socket = null;
    visual?.setAnalyser(null); setState('off');
    // Cleanup has a deadline; a dead network must not leave the talk control disabled forever.
    ending = (async () => {
        try { await oldAudio?.close(); } catch { /* Tracks stop before AudioContext closes. */ }
        if (oldSession) await api(`/api/avatar/sessions/${oldSession}/end`, {}, 5000).catch(() => {});
    })().finally(() => {
        ending = null; refreshStart();
        if (oldSession) void refreshUsage(oldSession, generation);
    });
    refreshStart(); return ending;
}
async function refreshUsage(id, generation) {
    try {
        for (let attempt = 0; attempt < 5 && generation === epoch; attempt++) {
            const usage = await requestJson(`/api/avatar/sessions/${id}/usage`, {}, 5000);
            if (generation !== epoch) return;
            await onMessage(usage, generation);
            if (!usage.pending) break;
            await new Promise(resolve => setTimeout(resolve, 500));
        }
    } catch { /* The durable ledger is reconciled server-side even while offline. */ }
}
function startOver() {
    // Recovery can keep waiting for a session the server already discarded, for
    // example after mobile suspension. Remembered context carries into a new one.
    const ended = end();
    // end() resets capture synchronously; the press that started over is still a
    // push-to-talk hold, buffered from the new microphone exactly like a first press.
    holding = mode() === 'push-to-talk'; refreshStart();
    return ended.then(() => start());
}
function press(event) {
    if (ending || changingQuiz || pageLeaving) return;
    if (state === 'reconnecting') { event?.preventDefault(); notice(); void startOver(); return; }
    if (!ready && !canStart()) return;
    event?.preventDefault(); notice();
    if (!ready) {
        holding = mode() === 'push-to-talk'; awaitingCommit = false; preRoll = []; ++captureEpoch;
        void start(); return;
    }
    if (mode() === 'hands-free') {
        const reply = state === 'speaking' || state === 'thinking';
        if (muted || state === 'error') {
            // During a reply, unpausing re-enables interruption; its idle starts listening.
            muted = false; audio?.mute(false);
            if (reply) setState(state); else listen();
        } else {
            // Pausing stops capture only; a reply that is already under way still plays.
            muted = true; audio?.mute(true);
            if (state === 'listening' || state === 'connecting') interrupt(); else setState(state);
        }
        return;
    }
    if (muted) { muted = false; audio?.mute(false); }
    if (holding) return;
    holding = true; awaitingCommit = false; preRoll = []; listen();
}
function commit() {
    awaitingCommit = false; if (send({ type: 'commit' })) setState('thinking');
}
async function release() {
    if (!holding) return;
    holding = false; awaitingCommit = true; flushing = true; refreshStart();
    const generation = epoch, capture = captureEpoch;
    const current = () => generation === epoch && capture === captureEpoch;
    try { await audio?.flush(); }
    catch {
        if (!current()) return;
        interrupt();
        if (ready) { setState('error'); notice('Microphone stopped responding. Please retry.'); }
    }
    finally { if (current()) flushing = false; }
    // A release during setup waits for Ready so the entire buffered utterance
    // reaches recognition, followed by its commit, even for a short button press.
    if (current() && ready && awaitingCommit && state === 'listening') commit();
}
async function chooseQuiz() {
    if (changingQuiz || pageLeaving || ending) return;
    changingQuiz = true; refreshStart();
    try {
        if (audio || sessionId) await end();
        if (!pageLeaving) selectMemory();
    } finally { changingQuiz = false; refreshStart(); }
}
el('quiz').addEventListener('change', () => { void chooseQuiz(); });
el('talk').addEventListener('pointerdown', event => {
    if (!event.isPrimary || event.button !== 0) return;
    el('talk').setPointerCapture(event.pointerId); press(event);
});
el('talk').addEventListener('pointerup', release); el('talk').addEventListener('pointercancel', release);
el('talk').addEventListener('lostpointercapture', release);
el('talk').addEventListener('blur', release);
el('talk').addEventListener('contextmenu', event => event.preventDefault());
el('talk').addEventListener('keydown', event => { if ((event.code === 'Space' || event.code === 'Enter') && !event.repeat) press(event); });
el('talk').addEventListener('keyup', event => { if (event.code === 'Space' || event.code === 'Enter') { event.preventDefault(); release(); } });
document.querySelectorAll('input[name="avatar-mode"]').forEach(input => input.addEventListener('change', () => {
    holding = false;
    // A mode selection pauses existing capture too; only the microphone starts it.
    if (audio) { muted = true; audio.mute(true); }
    if (ready) interrupt();
    else { ++captureEpoch; awaitingCommit = false; flushing = false; preRoll = []; }
    setState(state);
}));
window.addEventListener('pagehide', () => {
    pageLeaving = true; ++modelEpoch; ++configEpoch; modelReady = false; characterLoading = false; config = null;
    pageCleanup = end().catch(() => {});
    visual?.dispose(); visual = null; refreshStart();
});
window.addEventListener('pageshow', async event => {
    if (!event.persisted) return;
    const generation = modelEpoch;
    await pageCleanup;
    if (generation !== modelEpoch) return;
    pageLeaving = false; el('character').value = characterId; refreshStart();
    void initialize(); void chooseCharacter();
});
// A tab switch pauses only the renderer. Keep voice alive, but finish a held
// push-to-talk utterance because its key/pointer release may happen in another tab.
document.addEventListener('visibilitychange', () => { if (document.hidden) void release(); });

async function initialize() {
    const generation = ++configEpoch;
    try {
        const next = await requestJson('/api/avatar/config');
        if (pageLeaving || generation !== configEpoch) return;
        config = next;
        el('language').textContent = config.languageName ? `Practicing ${config.languageName}` : 'Choose a learning language in the sidebar.';
        const saved = memory.preferences();
        const selectedQuiz = saved?.language === config.language ? el('quiz').value || saved.quizId : '';
        el('quiz').replaceChildren(new Option('Just a conversation', ''));
        for (const quiz of config.quizzes) el('quiz').add(new Option(quiz.name, quiz.id));
        el('quiz').value = config.quizzes.some(quiz => quiz.id === selectedQuiz) ? selectedQuiz : '';
        selectMemory();
        if (!config.language) notice('Choose a learning language in the sidebar to start a conversation.');
        else if (!config.available) notice('The admin preview needs voice provider configuration before conversations can start.');
        refreshStart();
    } catch (error) { if (!pageLeaving && generation === configEpoch) notice(error.message); }
}
el('character').value = characterId;
void initialize();
void chooseCharacter();
