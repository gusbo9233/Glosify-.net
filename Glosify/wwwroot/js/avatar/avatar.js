import { AvatarAudio } from './audio.js';
import { createAvatar } from './scene.min.js';

const el = name => document.getElementById('avatar-' + name);
const number = value => new Intl.NumberFormat(undefined, { maximumFractionDigits: 4 }).format(value);
let config, visual, socket, audio, sessionId, turnId, state = 'off', muted = false, holding = false, epoch = 0, speechFrames = 0;
let preRoll = [], awaitingCommit = false, flushing = false, ready = false, modelReady = false, currentReply = '', cancelledTurn = null;
const mode = () => document.querySelector('input[name="avatar-mode"]:checked').value;
const token = document.querySelector('#avatar-page input[name="__RequestVerificationToken"]').value;
function send(value) { if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(value)); }
function setState(value) {
    state = value;
    const labels = { off: 'Ready when you are', connecting: 'Connecting', listening: 'Listening', thinking: 'Thinking', speaking: 'Speaking', idle: muted ? 'Microphone muted' : 'Your turn', error: 'Try again' };
    el('status').textContent = labels[value] || value;
    visual?.setState(value);
    el('talk').disabled = !ready || muted;
    el('talk').textContent = mode() === 'hands-free' ? (muted ? 'Microphone muted' : 'Hands-free is on') : holding ? 'Release to send' : 'Hold to talk · Space';
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
function refreshStart() { el('start').disabled = !config?.available || !config?.language || !modelReady || !!sessionId || !!audio; }
async function api(path, body) {
    const response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', RequestVerificationToken: token }, body: JSON.stringify(body) });
    if (!response.ok) { const problem = await response.json().catch(() => ({})); throw new Error(problem.detail || problem.title || 'The conversation could not start.'); }
    return response.status === 204 ? null : response.json();
}
function listen() {
    if (!ready || muted) return;
    cancelledTurn = turnId; turnId = null; currentReply = ''; audio?.stop();
    setState('connecting'); send({ type: 'listen', mode: mode() });
}
function interrupt() {
    cancelledTurn = turnId; turnId = null; currentReply = ''; audio?.stop();
    awaitingCommit = false; preRoll = [];
    send({ type: 'interrupt' }); setState('idle');
}
function frame({ pcm, rms }) {
    if (!ready || muted) return;
    visual?.hear(rms);
    if (state === 'listening') {
        if (mode() !== 'push-to-talk' || holding || flushing) socket.send(pcm);
        preRoll = []; return;
    }
    if (mode() === 'push-to-talk' && state === 'connecting') {
        if (!holding && !flushing) return;
        // Preserve the entire onset (up to five seconds), leaving room in the
        // server's 32-frame queue for live frames and the commit marker.
        if (preRoll.length >= 25) {
            holding = false; interrupt(); setState('error');
            notice('Voice is taking too long to connect. Please retry the microphone.');
            return;
        }
        preRoll.push(pcm);
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
        ready = true; el('mute').disabled = false; setState('idle');
        if (mode() === 'hands-free') listen(); return;
    }
    if (message.type === 'usage') {
        el('balance').textContent = number(message.available);
        el('spending').textContent = `This conversation · ${number(message.total)} credits · Listening ${number(message.recognition)} / Replies ${number(message.replies)} / Speech ${number(message.speech)}`;
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
            for (const pcm of preRoll) socket.send(pcm); preRoll = [];
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
                transcript('Rain', text); send({ type: 'played', turnId: id });
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
            el('talk').textContent = 'Retry microphone'; break;
    }
}
async function start() {
    if (audio || sessionId) return;
    const generation = ++epoch; notice(); caption(''); el('start').disabled = true; setState('connecting');
    el('transcript').replaceChildren(); el('spending').textContent = 'This conversation · 0 credits';
    const nextAudio = new AvatarAudio(); audio = nextAudio;
    try {
        await nextAudio.open(frame);
        if (generation !== epoch) { await nextAudio.close(); return; }
        visual?.setAnalyser(nextAudio.analyser);
        const result = await api('/api/avatar/sessions', { quizId: el('quiz').value || null });
        if (generation !== epoch) { await api(`/api/avatar/sessions/${result.sessionId}/end`, {}); return; }
        sessionId = result.sessionId;
        el('voice').textContent = `${result.voiceName} · ${result.nativeVoice ? "Native" : "Multilingual"} voice`;
        const url = new URL(result.connectUrl, location.href); url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        socket = new WebSocket(url);
        socket.onmessage = event => {
            try { void onMessage(JSON.parse(event.data), generation).catch(error => { notice(error.message); void end(); }); }
            catch { notice('The voice connection returned an invalid message.'); void end(); }
        };
        socket.onclose = () => { if (generation === epoch) { notice('Conversation ended. Start again whenever you like.'); void end(); } };
        socket.onerror = () => { if (generation === epoch) notice('Voice connection failed. Please start again.'); };
        el('quiz').disabled = true; el('end').disabled = false;
        el('hint').textContent = mode() === 'hands-free' ? 'Microphone is on. You can interrupt Rain.' : 'Hold Space or the talk button. Release to send.';
    } catch (error) {
        notice(error.name === 'NotAllowedError' ? 'Allow microphone access to start a conversation.' : error.message);
        await end();
    }
}
async function end() {
    ++epoch; ready = false; holding = false; awaitingCommit = false; preRoll = []; speechFrames = 0; muted = false;
    const oldSession = sessionId, oldAudio = audio; sessionId = null; audio = null;
    if (socket) { send({ type: 'end' }); socket.onclose = null; socket.close(); socket = null; }
    visual?.setAnalyser(null); await oldAudio?.close();
    if (oldSession) {
        await api(`/api/avatar/sessions/${oldSession}/end`, {}).catch(() => {});
        for (let attempt = 0; attempt < 5; attempt++) {
            const response = await fetch(`/api/avatar/sessions/${oldSession}/usage`, { credentials: 'same-origin' }).catch(() => null);
            if (!response?.ok) break;
            const usage = await response.json();
            await onMessage(usage, epoch);
            if (!usage.pending) break;
            await new Promise(resolve => setTimeout(resolve, 500));
        }
    }
    el('quiz').disabled = false;
    el('end').disabled = true; el('mute').disabled = true; el('mute').textContent = 'Mute'; el('mute').setAttribute('aria-pressed', 'false');
    el('hint').textContent = 'Your microphone is off.';
    setState('off'); refreshStart();
}
function press(event) {
    if (!ready || muted) return;
    event?.preventDefault(); notice();
    if (mode() === 'hands-free') { if (state === 'error' || state === 'idle') listen(); return; }
    if (holding) return;
    holding = true; awaitingCommit = false; preRoll = []; listen();
}
function commit() {
    awaitingCommit = false; send({ type: 'commit' }); setState('thinking');
}
async function release() {
    if (!holding) return;
    holding = false; awaitingCommit = true; flushing = true;
    const generation = epoch;
    try { await audio?.flush(); }
    finally { flushing = false; }
    // A release during setup waits for Ready so the entire buffered utterance
    // reaches recognition, followed by its commit, even for a short button press.
    if (generation === epoch && ready && awaitingCommit && state === 'listening') commit();
}
el('start').addEventListener('click', start); el('end').addEventListener('click', end);
el('talk').addEventListener('pointerdown', event => { el('talk').setPointerCapture(event.pointerId); press(event); });
el('talk').addEventListener('pointerup', release); el('talk').addEventListener('pointercancel', release);
document.addEventListener('keydown', event => {
    if (event.code === 'Space' && !event.repeat && !event.target.closest('input,select,textarea,button,a,summary,[contenteditable]')) press(event);
});
document.addEventListener('keyup', event => { if (event.code === 'Space') release(); });
el('talk').addEventListener('keydown', event => { if ((event.code === 'Space' || event.code === 'Enter') && !event.repeat) press(event); });
el('talk').addEventListener('keyup', event => { if (event.code === 'Space' || event.code === 'Enter') { event.preventDefault(); release(); } });
el('mute').addEventListener('click', () => {
    muted = !muted; audio?.mute(muted); preRoll = []; holding = false;
    el('mute').textContent = muted ? 'Unmute' : 'Mute'; el('mute').setAttribute('aria-pressed', String(muted));
    if (muted && ['listening', 'connecting'].includes(state)) interrupt();
    if (!muted && mode() === 'hands-free' && !['speaking', 'thinking'].includes(state)) listen();
    else setState(state);
});
document.querySelectorAll('input[name="avatar-mode"]').forEach(input => input.addEventListener('change', () => {
    if (ready) { interrupt(); if (mode() === 'hands-free' && !muted) listen(); }
    el('hint').textContent = ready ? mode() === 'hands-free' ? 'Microphone is on. You can interrupt Rain.' : 'Hold Space or the talk button. Release to send.' : 'Your microphone stays off until you start.';
    setState(state);
}));
window.addEventListener('pagehide', () => { void end(); visual?.dispose(); });
document.addEventListener('visibilitychange', () => { if (document.hidden && audio) { notice('Conversation ended while the page was away.'); void end(); } });

async function initialize() {
    try {
        const response = await fetch('/api/avatar/config', { credentials: 'same-origin' });
        if (!response.ok) throw new Error('Conversation settings could not load. Refresh the page to retry.');
        config = await response.json();
        el('language').textContent = config.languageName ? `Practicing ${config.languageName}` : 'Choose a learning language in the sidebar.';
        for (const quiz of config.quizzes) el('quiz').add(new Option(quiz.name, quiz.id));
        const r = config.rates;
        el('rates').textContent = `Listening ${number(r.recognitionPerMinute)} / min · Replies ${number(r.replyPerThousandTokens)} / 1k tokens · Speech ${number(r.speechPerThousandCharacters)} / 1k characters`;
        el('balance').textContent = number(config.balance);
        if (!config.language) notice('Choose a learning language in the sidebar to start a conversation.');
        else if (!config.available) notice('The admin preview needs voice provider configuration before conversations can start.');
        refreshStart();
    } catch (error) { notice(error.message); }
}
void initialize();
createAvatar(el('canvas')).then(value => {
    visual = value; modelReady = true; el('render-status').hidden = true; setState('off'); refreshStart();
}).catch(() => { el('render-status').textContent = 'Rain could not load. Refresh or try a browser with 3D support.'; });
