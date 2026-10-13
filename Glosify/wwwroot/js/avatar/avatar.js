import { normalizeQuality } from './render-quality.js';
import { transcriptPresentation } from './transcript.js';
import { preserveConversation } from './retention.js';
import { setupFloatingChat } from './chat-panel.js';
import { AvatarAudio } from './audio.js';
import { createConversationMemory } from './memory.js';
import { VoiceConnection } from './connection.js';
import { createAvatar } from './scene.min.js';

const el = name => document.getElementById('avatar-' + name);
const chat = setupFloatingChat({ panel: el('dialogue'), stage: el('stage'), move: el('chat-move'), resize: el('chat-resize'), toggle: el('chat-toggle'), controls: el('controls'), unread: el('chat-unread') });
let transcriptStatus = null, savingTranscript = false, transcriptStatusRequest = 0;
let lastTranscript = null, clearedChatTurn, chatRevision = 0;
let sessionStore;
try { sessionStore = globalThis.sessionStorage; } catch { /* Storage may be disabled. */ }
const memory = createConversationMemory(document.body?.dataset.speechUser, sessionStore);
const preferences = memory.preferences();
let practiceContext = preferences?.practiceContext || '';
el('practice-context').value = practiceContext;
let qualityMode = 'auto';
try { qualityMode = normalizeQuality(localStorage.getItem('glosify.avatar.quality')); } catch { /* Optional local preference. */ }
el('quality').value = qualityMode;
el('quality').addEventListener('change', () => {
    qualityMode = normalizeQuality(el('quality').value);
    try { localStorage.setItem('glosify.avatar.quality', qualityMode); } catch { /* Storage may be disabled. */ }
    visual?.setQuality(qualityMode);
});
const helpKey = document.body?.dataset.speechUser ? `glosify.avatar.help.${document.body.dataset.speechUser}` : null;
const helpOptions = () => ({ translateReplies: !!el('translate').checked, suggestAnswers: !!el('suggest').checked });
let replyHelp = null, spaceHeld = false, spaceRecording = false;
try {
    const saved = JSON.parse(helpKey && sessionStore?.getItem(helpKey) || '{}');
    el('translate').checked = saved?.translateReplies === true;
    el('suggest').checked = saved?.suggestAnswers === true;
} catch { /* Disabled or invalid storage leaves both options off. */ }
function renderHelp() {
    const options = helpOptions();
    const translation = options.translateReplies ? replyHelp?.translation : '';
    const suggestion = options.suggestAnswers ? replyHelp?.suggestion : '';
    const suggestionTranslation = suggestion && options.translateReplies ? replyHelp?.suggestionTranslation : '';
    el('translation').textContent = translation || ''; el('translation').hidden = !translation;
    el('suggestion-text').textContent = suggestion || ''; el('suggestion').hidden = !suggestion;
    el('suggestion-translation').textContent = suggestionTranslation || ''; el('suggestion-translation').hidden = !suggestionTranslation;
    el('chat-current').hidden = el('caption').hidden && !translation && !suggestion;
    el('chat-empty').hidden = !!lastTranscript || !el('chat-current').hidden;
    if (translation || suggestion) chat?.markUnread();
}
for (const name of ['translate', 'suggest']) el(name).addEventListener('change', () => {
    try { if (helpKey) sessionStore?.setItem(helpKey, JSON.stringify(helpOptions())); } catch { /* Optional preference storage. */ }
    renderHelp();
});
const characters = { rain: { name: 'Rain', gender: 'female' }, rowan: { name: 'Rowan', gender: 'male' } };
let characterId = Object.hasOwn(characters, preferences?.characterId) ? preferences.characterId : 'rain', characterLoading = false, pageLeaving = false, modelEpoch = 0, configEpoch = 0, pageCleanup = Promise.resolve();
const character = () => characters[characterId];
const number = value => new Intl.NumberFormat(undefined, { maximumFractionDigits: 4 }).format(value);
let ending = null, changingQuiz = false, captureEpoch = 0;
let config, visual, socket, audio, sessionId, turnId, state = 'off', muted = false, holding = false, epoch = 0, speechFrames = 0;
let preRoll = [], awaitingCommit = false, flushing = false, ready = false, modelReady = false, currentReply = '', cancelledTurn = null;
const mode = () => document.querySelector('input[name="avatar-mode"]:checked').value;
const selection = () => ({ language: config?.language, characterId, quizId: el('quiz').value || null, practiceContext });
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
function positionSettingsToggle() {
    const rtl = document.documentElement?.dir === 'rtl';
    const style = el('settings-toggle').style;
    if (!el('settings').hidden) {
        const bounds = el('settings').getBoundingClientRect();
        style.left = `${bounds.left + 8}px`;
        style.right = 'auto';
        style.top = `${bounds.top + 8}px`;
        return;
    }
    const edge = rtl ? 'left' : 'right';
    style[edge === 'left' ? 'right' : 'left'] = 'auto';
    style[edge] = '0px';
    style.top = 'max(16px, env(safe-area-inset-top))';
}
const settingsGeometry = new ResizeObserver(positionSettingsToggle);
settingsGeometry.observe(el('workspace')); settingsGeometry.observe(el('settings'));
const appMain = document.querySelector('.app-main');
if (appMain) settingsGeometry.observe(appMain);
window.addEventListener('resize', positionSettingsToggle);
window.addEventListener('scroll', event => {
    // Scrolling chat messages or another independent panel cannot move settings.
    if (event.target === document || event.target?.contains?.(el('settings'))) positionSettingsToggle();
}, true);
function toggleSettings(open) {
    const toggle = el('settings-toggle');
    el('settings').hidden = !open;
    el('workspace').classList.toggle('settings-closed', !open);
    toggle.setAttribute('aria-expanded', String(open));
    toggle.setAttribute('aria-label', `${open ? 'Close' : 'Open'} conversation settings`);
    toggle.title = `${open ? 'Close' : 'Open'} settings`;
    positionSettingsToggle();
}
el('settings-toggle').addEventListener('click', () => {
    const open = el('settings').hidden;
    toggleSettings(open);
    if (open) {
        el('settings').scrollIntoView({ block: 'start', behavior: 'instant' });
        positionSettingsToggle();
    }
});
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
    const body = el('chat-body'), follow = body.scrollHeight - body.scrollTop - body.clientHeight < 48;
    el('chat-speaker').textContent = speaker === 'user' ? 'You' : character().name;
    el('chat-current').dataset.speaker = speaker;
    el('caption').textContent = text; el('caption').dataset.speaker = speaker;
    el('caption').hidden = !text.trim();
    if (!text.trim() || speaker === 'user') replyHelp = null;
    renderHelp();
    if (text.trim()) chat?.markUnread();
    if (follow) body.scrollTop = body.scrollHeight;
}
function transcript(speaker, text) {
    const body = el('chat-body'), follow = body.scrollHeight - body.scrollTop - body.clientHeight < 48;
    const item = document.createElement('li'), who = document.createElement('strong');
    item.dataset.speaker = speaker === 'You' ? 'user' : 'assistant';
    who.textContent = speaker + ' '; item.append(who, document.createTextNode(text));
    el('transcript').append(item);
    while (el('transcript').children.length > 48) el('transcript').firstChild.remove();
    lastTranscript = { speaker, text };
    chat?.markUnread();
    if (el('caption').textContent === text && el('chat-speaker').textContent === speaker) el('caption').hidden = true;
    renderHelp();
    if (follow) body.scrollTop = body.scrollHeight;
}
function clearChat() {
    clearedChatTurn = turnId ?? undefined; ++chatRevision;
    el('transcript').replaceChildren(); lastTranscript = null; caption('');
    chat?.clearUnread();
}
el('chat-clear').addEventListener('click', clearChat);
function refreshStart() {
    const handsFree = mode() === 'hands-free', reconnecting = state === 'reconnecting';
    // A hands-free error stops listening until the explicit retry.
    const recording = holding || (handsFree && !!audio && !muted && state !== 'error');
    const disabled = !holding && (!!ending || changingQuiz || pageLeaving || !(ready || canStart() || reconnecting));
    el('talk').hidden = handsFree;
    el('talk').disabled = handsFree || disabled;
    el('talk').setAttribute('aria-label', 'Hold to record');
    el('talk').setAttribute('aria-pressed', String(!handsFree && holding));
    el('talk').title = reconnecting ? 'Start over' : holding ? 'Release to send' : 'Hold to record';
    el('hands-free').setAttribute('aria-label', 'Hands-free conversation');
    el('hands-free').hidden = !handsFree;
    el('hands-free').disabled = !handsFree || disabled;
    el('hands-free').setAttribute('aria-pressed', String(handsFree && recording));
    el('hands-free').textContent = reconnecting ? 'Restart hands-free' : recording ? 'Pause hands-free' : 'Start hands-free';
    el('hands-free').title = el('hands-free').textContent;
    el('practice-context').disabled = !!ending || changingQuiz || characterLoading || (!!audio && !ready) || pageLeaving;
    el('quiz').disabled = !!ending || changingQuiz || characterLoading || (!!audio && !ready) || pageLeaving;
    el('character').disabled = characterLoading || !!ending || changingQuiz || (!!audio && !ready) || pageLeaving;
}
async function chooseCharacter() {
    if (pageLeaving || characterLoading || el('character').disabled) {
        el('character').value = characterId; return;
    }
    const selected = el('character').value;
    const previous = selection();
    // The select has already changed; the active character is still the old one.
    if (selected !== characterId && config && memory.get(previous)) {
        changingQuiz = true; refreshStart();
        try { if (!await prepareConversationChange(previous)) { el('character').value = characterId; return; } }
        finally { changingQuiz = false; refreshStart(); }
    }
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
        const next = await createAvatar(el('canvas'), characterId, { quality: qualityMode });
        if (pageLeaving || generation !== modelEpoch) { next.dispose(); return; }
        next.setQuality(qualityMode);
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
            const error = new Error(problem.detail || problem.title || 'The voice request failed. Please retry.');
            error.status = response.status; throw error;
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
function showTranscriptStatus(status) {
    if (status && status.conversationId !== memory.get(selection())) return;
    if (status && transcriptStatus?.conversationId === status.conversationId
        && (status.revision ?? 0) < (transcriptStatus.revision ?? 0)) return;
    if (status && transcriptStatus?.conversationId === status.conversationId)
        status = { ...status, turnCount: Math.max(status.turnCount, transcriptStatus.turnCount),
            savedTurns: status.transcriptId && status.transcriptId === transcriptStatus.transcriptId
                ? Math.max(status.savedTurns, transcriptStatus.savedTurns) : status.savedTurns };
    transcriptStatus = status;
    if (!status || status.savedTurns >= status.turnCount) showSaveError();
    const view = transcriptPresentation(status);
    el('ask').disabled = el('make-quiz').disabled = !view.enabled;
    el('save').disabled = !view.enabled || savingTranscript;
    el('save').textContent = view.label;
    el('save-status').textContent = view.message;
    el('saved-link').hidden = !view.href;
    if (view.href) el('saved-link').href = view.href;
}
function showSaveError(message = '') {
    el('save-error').textContent = message;
    el('save-error').hidden = !message;
}
async function refreshTranscriptStatus() {
    const id = memory.get(selection());
    if (!id) { showTranscriptStatus(null); return; }
    const request = ++transcriptStatusRequest;
    try {
        const status = await requestJson(`/api/avatar/conversations/${id}/transcript`);
        if (request === transcriptStatusRequest) showTranscriptStatus(status);
    }
    catch { /* Preserve known state; navigation resolves unknown state before leaving. */ }
}
async function saveTranscript() {
    const id = memory.get(selection());
    if (!id || savingTranscript) return;
    ++transcriptStatusRequest; // An earlier GET cannot overwrite the save result.
    savingTranscript = true; showTranscriptStatus(transcriptStatus);
    try { showTranscriptStatus(await api(`/api/avatar/conversations/${id}/transcript`, {})); }
    catch (error) {
        if (memory.get(selection()) === id) {
            el('save-status').textContent = error.message; showSaveError(error.message);
        }
    }
    finally { savingTranscript = false; el('save').disabled = !transcriptStatus?.turnCount; }
}
el('save').addEventListener('click', () => { void saveTranscript(); });
for (const name of ['ask', 'make-quiz']) el(name).addEventListener('click', () => {
    const conversationId = memory.get(selection());
    if (conversationId) window.dispatchEvent(new CustomEvent('assistant:avatar', { detail: { conversationId, quiz: name === 'make-quiz' } }));
});
window.addEventListener('assistant:opened', () => {
    muted = true; holding = false; spaceRecording = false; audio?.mute(true);
    ++captureEpoch; awaitingCommit = false; flushing = false; preRoll = [];
    if (state === 'listening' || state === 'connecting') interrupt();
    else setState(state);
});
el('autosave').addEventListener('change', async () => {
    const enabled = el('autosave').checked;
    el('autosave').disabled = true;
    try {
        await api('/api/avatar/preferences', { autoSaveConversations: enabled });
        if (enabled) await saveTranscript();
    } catch (error) { el('autosave').checked = !enabled; notice(error.message); }
    finally { el('autosave').disabled = false; }
});
el('new-conversation').addEventListener('click', async () => {
    if (pageLeaving || changingQuiz || ending) return;
    changingQuiz = true; el('new-conversation').disabled = true; refreshStart();
    try {
        if (!await prepareConversationChange(selection())) return;
        memory.save(selection(), '');
        clearChat(); showTranscriptStatus(null);
        notice('New conversation ready. Saved transcripts are kept.');
    } finally { changingQuiz = false; el('new-conversation').disabled = false; refreshStart(); }
});
function chooseUnsavedConversation() {
    const dialog = el('unsaved-dialog');
    return new Promise(resolve => {
        const complete = () => resolve(dialog.returnValue || 'cancel');
        dialog.addEventListener('close', complete, { once: true });
        dialog.returnValue = 'cancel'; dialog.showModal();
    });
}
async function prepareConversationChange(context) {
    const id = memory.get(context);
    if (!id) { if (audio || sessionId) await end(); return true; }
    const pendingTurn = ['connecting', 'listening', 'thinking', 'speaking'].includes(state);
    if (audio) {
        muted = true; holding = false; spaceRecording = false; audio.mute(true);
        if (state === 'listening' || state === 'connecting') interrupt();
        else setState(state);
    }
    try {
        const accepted = await preserveConversation(id, {
            status: value => requestJson(`/api/avatar/conversations/${value}/transcript`),
            choose: chooseUnsavedConversation,
            pendingTurn, stop: end,
            save: value => api(`/api/avatar/conversations/${value}/transcript`, {}),
            forget: value => api(`/api/avatar/memories/${value}/forget`, {})
        });
        if (!accepted) return false;
        memory.save(context, '');
        clearChat(); showTranscriptStatus(null);
        window.dispatchEvent(new CustomEvent('assistant:avatar-ended', { detail: { conversationId: id } }));
        return true;
    } catch (error) {
        notice(error.message);
        // A reference that expired or disappeared cannot block starting over.
        // Keep missing references for a possible language change in another tab.
        if (error.status === 410) memory.save(context, '');
        if (error.status === 404 || error.status === 410) { showTranscriptStatus(null); return true; }
        return false;
    }
}
// Normal navigation offers the same save decision as switching partner or quiz.
// Refresh/closing uses the browser's own warning; the reference remains resumable.
document.addEventListener('click', event => {
    const link = event.target?.closest?.('a[href]');
    if (!link || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey
        || link.target === '_blank' || link.hasAttribute('download') || link.getAttribute('href')?.startsWith('#')) return;
    if (!memory.get(selection())) return;
    if (transcriptStatus && transcriptStatus.turnCount <= transcriptStatus.savedTurns
        && !['connecting', 'listening', 'thinking', 'speaking'].includes(state)) return;
    event.preventDefault();
    if (changingQuiz) return;
    changingQuiz = true; refreshStart();
    void prepareConversationChange(selection()).then(accepted => { if (accepted) location.assign(link.href); })
        .finally(() => { changingQuiz = false; refreshStart(); });
}, { capture: true });
window.addEventListener('beforeunload', event => {
    if (memory.get(selection()) && (!transcriptStatus || transcriptStatus.turnCount > transcriptStatus.savedTurns)) {
        event.preventDefault(); event.returnValue = '';
    }
});
function selectMemory() {
    const discarded = memory.select(selection());
    if (discarded) showTranscriptStatus(null);
    void refreshTranscriptStatus();
    if (!discarded) return;
    el('transcript').replaceChildren(); lastTranscript = null; caption('');
    // Selection can also change in another tab. Keep its opaque reference until
    // an explicit Save/Discard decision; switching back can resume that context.
}
function connectionLost() {
    ++captureEpoch;
    ready = false; holding = false; spaceRecording = false; awaitingCommit = false; flushing = false;
    preRoll = []; speechFrames = 0; cancelledTurn = turnId; turnId = null; currentReply = '';
    audio?.stop(); audio?.mute(true); muted = true;
    caption(''); setState('reconnecting');
    notice('Connection lost. Reconnecting… Use the conversation control to start over instead. Interrupted audio will not be replayed.');
}
function listen(preserveCapture = false) {
    if (!ready || muted) return;
    if (!preserveCapture) { ++captureEpoch; flushing = false; }
    cancelledTurn = turnId; turnId = null; currentReply = ''; audio?.stop();
    setState('connecting'); send({ type: 'listen', mode: mode(), ...helpOptions() });
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
    if (message.type === 'transcript-status') { showTranscriptStatus(message.status); return; }
    if (message.type === 'transcript-save-error') {
        el('save-status').textContent = message.detail; showSaveError(message.detail);
        void refreshTranscriptStatus(); return;
    }
    if (message.type === 'connected') {
        ready = true; setState('idle');
        if (message.resumed) {
            notice('Connection restored. Use the conversation control to continue; please repeat the interrupted turn.');
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
    const showChat = !turnId || turnId !== clearedChatTurn;
    switch (message.type) {
        case 'listening':
            setState('listening');
            if (mode() === 'push-to-talk' && !holding && !awaitingCommit) { interrupt(); break; }
            for (const pcm of preRoll) { if (socket.send(pcm) === false) break; } preRoll = [];
            if (awaitingCommit && !flushing) commit();
            break;
        case 'partial': if (showChat) caption(message.text, 'user'); break;
        case 'transcript': if (showChat) { caption(message.text, 'user'); transcript('You', message.text); } break;
        case 'thinking': setState('thinking'); break;
        case 'reply':
            currentReply = message.text;
            if (showChat) { replyHelp = message; caption(message.text); }
            visual?.setReply(message.text); setState('speaking'); break;
        case 'audio': audio?.play(message.pcm); break;
        case 'audio-end': {
            const id = turnId, text = currentReply, revision = chatRevision;
            if (await audio.finished() && generation === epoch && id === turnId) {
                if (showChat && revision === chatRevision) transcript(character().name, text);
                send({ type: 'played', turnId: id });
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
    const generation = ++epoch; ready = false; muted = false; notice(); caption(''); setState('connecting');
    el('spending').textContent = 'Current session · 0 credits';
    const context = selection();
    const nextAudio = new AvatarAudio(); audio = nextAudio; refreshStart();
    try {
        await nextAudio.open(frame);
        if (generation !== epoch) { await nextAudio.close(); return; }
        visual?.setAnalyser(nextAudio.analyser);
        const result = await api('/api/avatar/sessions', { quizId: context.quizId, characterId, memoryId: memory.get(context) || null, practiceContext: context.practiceContext });
        if (generation !== epoch) { await api(`/api/avatar/sessions/${result.sessionId}/end`, {}); return; }
        sessionId = result.sessionId;
        memory.save(context, result.memoryId);
        void refreshTranscriptStatus();
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
    ready = false; holding = false; spaceRecording = false; awaitingCommit = false; flushing = false;
    preRoll = []; speechFrames = 0; muted = false; turnId = null;
    const oldSession = sessionId, oldAudio = audio; sessionId = null; audio = null;
    socket?.close(); socket = null;
    visual?.setAnalyser(null); caption(''); setState('off');
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
    if (mode() !== 'push-to-talk') return;
    activateConversation(event);
}
function toggleHandsFree(event) {
    if (mode() !== 'hands-free') return;
    activateConversation(event);
}
function activateConversation(event) {
    if (ending || changingQuiz || pageLeaving) return;
    const draft = el('practice-context').value.trim();
    if (draft !== practiceContext) {
        event?.preventDefault();
        if (audio || sessionId || memory.get(selection())) {
            // Resolve retention before opening a microphone. If a dialog is
            // needed, the learner starts recording with their next press.
            void chooseQuiz(draft); return;
        }
        practiceContext = draft; el('practice-context').value = draft; selectMemory();
    }
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
async function chooseQuiz(nextPracticeContext = el('practice-context').value.trim()) {
    // A blur caused by disabling the field must not overwrite a pending draft.
    if (changingQuiz || pageLeaving || ending) return;
    const previous = memory.preferences();
    if (!audio && !sessionId && (!previous || !memory.get(previous))) {
        practiceContext = nextPracticeContext; el('practice-context').value = practiceContext;
        selectMemory(); return;
    }
    changingQuiz = true; refreshStart();
    try {
        if (previous && !await prepareConversationChange(previous)) {
            el('quiz').value = previous.quizId || '';
            el('practice-context').value = practiceContext; return;
        }
        if (!pageLeaving) {
            practiceContext = nextPracticeContext;
            el('practice-context').value = practiceContext;
            selectMemory();
        }
    } finally { changingQuiz = false; refreshStart(); }
}
el('quiz').addEventListener('change', () => { void chooseQuiz(); });
el('practice-context').addEventListener('change', () => {
    const value = el('practice-context').value.trim();
    if (value !== practiceContext) void chooseQuiz(value);
});
el('hands-free').addEventListener('click', toggleHandsFree);
el('talk').addEventListener('pointerdown', event => {
    if (!event.isPrimary || event.button !== 0) return;
    el('talk').setPointerCapture(event.pointerId); press(event);
});
el('talk').addEventListener('pointerup', release); el('talk').addEventListener('pointercancel', release);
el('talk').addEventListener('lostpointercapture', release);
el('talk').addEventListener('blur', release);
el('talk').addEventListener('contextmenu', event => event.preventDefault());
el('talk').addEventListener('keydown', event => {
    if (event.code === 'Enter') { event.preventDefault(); if (!event.repeat) press(event); }
});
el('talk').addEventListener('keyup', event => { if (event.code === 'Enter') { event.preventDefault(); void release(); } });
// Space controls recording on the page and microphone. Controls, editors and
// dialogs keep their native keyboard behavior, including checkbox/radio toggles.
function isTypingSpace(event) {
    if (event.isComposing || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey
        || event.target?.isContentEditable) return true;
    if (event.target === el('talk')) return false;
    return !!event.target?.closest?.('button, input, textarea, select, a[href], summary, dialog, [role="dialog"], [data-assistant-panel], [contenteditable], [role="textbox"], [role="combobox"], [role="button"], [role="checkbox"], [role="radio"], [role="switch"], [role="slider"], [role="spinbutton"], [role="listbox"]');
}
document.addEventListener('keydown', event => {
    if (event.code !== 'Space' || pageLeaving) return;
    if (!spaceHeld && (event.defaultPrevented || isTypingSpace(event))) return;
    event.preventDefault(); event.stopPropagation();
    if (spaceHeld || event.repeat) return;
    spaceHeld = true;
    const handsFree = mode() === 'hands-free';
    if (el(handsFree ? 'hands-free' : 'talk').disabled) return;
    if (handsFree) toggleHandsFree();
    else {
        const wasHolding = holding;
        press(); spaceRecording = !wasHolding && holding;
    }
}, { capture: true });
document.addEventListener('keyup', event => {
    if (event.code !== 'Space' || (!spaceHeld && isTypingSpace(event))) return;
    event.preventDefault(); event.stopPropagation();
    const releaseHold = spaceHeld && spaceRecording;
    spaceHeld = false; spaceRecording = false;
    if (releaseHold) void release();
}, { capture: true });
function releaseKeyboard() { spaceHeld = false; spaceRecording = false; void release(); }
window.addEventListener('blur', releaseKeyboard);
document.querySelectorAll('input[name="avatar-mode"]').forEach(input => input.addEventListener('change', () => {
    spaceHeld = false; spaceRecording = false; holding = false;
    // A mode selection pauses capture; the matching button or Space starts it.
    if (audio) { muted = true; audio.mute(true); }
    if (ready) interrupt();
    else { ++captureEpoch; awaitingCommit = false; flushing = false; preRoll = []; }
    setState(state);
}));
window.addEventListener('pagehide', () => {
    spaceHeld = false; pageLeaving = true; ++modelEpoch; ++configEpoch; modelReady = false; characterLoading = false; config = null;
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
document.addEventListener('visibilitychange', () => { if (document.hidden) releaseKeyboard(); });

async function initialize() {
    const generation = ++configEpoch;
    try {
        const next = await requestJson('/api/avatar/config');
        if (pageLeaving || generation !== configEpoch) return;
        config = next;
        el('autosave').checked = !!config.autoSaveConversations;
        el('caption').lang = config.language || '';
        el('suggestion-text').lang = config.language || '';
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
