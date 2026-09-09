import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const script = readFileSync(new URL('../Glosify/wwwroot/js/tts.js', import.meta.url), 'utf8');
const tick = () => new Promise(resolve => setImmediate(resolve));

class Control {
    constructor(value = '') { this.value = value; this.options = []; this.listeners = {}; this.hidden = false; }
    addEventListener(name, listener) { (this.listeners[name] ||= []).push(listener); }
    fire(name) { for (const listener of this.listeners[name] || []) listener({}); }
    replaceChildren() { this.options = []; this.value = ''; }
    add(option) { this.options.push(option); if (this.options.length === 1) this.value = option.value; }
}
function setup(fetchResponse = async () => ({ ok: true, blob: async () => ({}) }), initial = {}) {
    const requests = [], requestOptions = [], spoken = [], audio = [], stored = new Map([['glosify.speech.preferences', JSON.stringify(initial)]]);
    const controls = Object.fromEntries(['provider', 'language', 'voice', 'status', 'save', 'credits', 'price', 'cancel'].map(key => [key, new Control()]));
    controls.language.options = ['en-GB', 'sv-SE', 'pl-PL'].map(value => ({ value }));
    controls.price.dataset = { priceTemplate: '{0} credits total; {1} per segment' };
    const dialog = new Control();
    dialog.dataset = { loading: 'Loading', azureUnavailable: 'Azure unavailable', noAzureVoices: 'No Azure voices', noBrowserVoices: 'No browser voices', rate: '{0} credits per segment', reviewRate: 'Review rate in settings', voiceUnavailable: 'Saved voice unavailable; open settings', azureLabel: 'Azure · Uses AI credits', browserLabel: 'Browser (free)', estimate: 'Estimated total: {0} AI credits' };
    dialog.querySelector = selector => controls[selector.match(/data-speech-(\w+)/)[1]];
    dialog.showModal = () => { dialog.open = true; };
    dialog.close = () => { dialog.open = false; queueMicrotask(() => dialog.fire('close')); };
    const error = new Control();
    error.querySelector = () => error;
    const browserVoices = [{ name: 'English', voiceURI: 'en-local', lang: 'en-US' }, { name: 'Swedish', voiceURI: 'sv-local', lang: 'sv-SE' }];
    const speechSynthesis = { getVoices: () => browserVoices, addEventListener() {}, removeEventListener() {}, cancel() {}, speak(utterance) { spoken.push(utterance); queueMicrotask(() => utterance.onend?.()); } };
    class Audio { constructor(url) { this.url = url; audio.push(this); } play() { queueMicrotask(() => this.onended?.()); return Promise.resolve(); } pause() {} removeAttribute() {} }
    const context = { document: { body: { dataset: { ttsLocales: JSON.stringify({ english: 'en-GB', swedish: 'sv-SE', polish: 'pl-PL' }) } }, querySelector: selector => selector === '[data-speech-dialog]' ? dialog : error, addEventListener() {}, dispatchEvent() {} }, window: { speechSynthesis }, localStorage: { getItem: key => stored.get(key), setItem: (key, value) => stored.set(key, value) }, CustomEvent: function(name) { this.type = name; }, Option: function(label, value) { this.label = label; this.value = value; }, Audio, AbortController, setTimeout, console, URL: { createObjectURL: () => 'blob:test', revokeObjectURL() {} }, fetch: async (url, options) => { requests.push(url); requestOptions.push(options); return fetchResponse(url, options); }, SpeechSynthesisUtterance: function(text) { this.text = text; } };
    context.window.SpeechSynthesisUtterance = context.SpeechSynthesisUtterance;
    vm.runInNewContext(script, context);
    return { api: context.window.GlosifyTts, requests, requestOptions, spoken, audio, controls, dialog, error, stored, browserVoices };
}

test('browser provider never calls Azure and honors the selected browser voice', async () => {
    const h = setup();
    const result = await h.api.playQueue([{ text: 'Hej', lang: 'Swedish', provider: 'browser', voice: 'sv-local' }]);
    assert.equal(result.state, 'completed');
    assert.equal(h.requests.length, 0);
    assert.equal(h.spoken[0].voice.voiceURI, 'sv-local');
});

for (const status of [401, 402, 429, 501, 502, 503]) {
    test(`Azure ${status} stops playback without browser fallback`, async () => {
        const h = setup(async () => ({ ok: false, status, json: async () => ({ detail: 'Azure error' }) }));
        const result = await h.api.playQueue([{ text: 'Hej', lang: 'Swedish', provider: 'azure' }, { text: 'Again', lang: 'Swedish', provider: 'azure' }]);
        assert.equal(result.state, 'error');
        assert.equal(result.error.message, 'Azure error');
        assert.equal(h.requests.length, 1);
        assert.equal(h.spoken.length, 0);
    });
}

const azureResponse = (rate = 1) => async url => url.includes('/voices?')
    ? { ok: true, json: async () => ({ configured: true, creditsPerRequest: rate, voices: [
        { shortName: 'sv-SE-SofieNeural', displayName: 'Sofie', locale: 'sv-SE' },
        { shortName: 'sv-SE-MattiasNeural', displayName: 'Mattias', locale: 'sv-SE' },
        { shortName: 'pl-PL-ZofiaNeural', displayName: 'Zofia', locale: 'pl-PL' },
    ] }) } : { ok: true, blob: async () => ({}) };
const prefs = h => JSON.parse(h.stored.get('glosify.speech.preferences'));
const paidRequests = h => h.requestOptions.filter(options => options.method === 'POST');
async function saveAzure(h, lang = 'Swedish', bookId) {
    h.api.openSettings({ lang, bookId });
    h.controls.provider.value = 'azure'; h.controls.provider.fire('change');
    await tick();
    h.controls.save.fire('click');
    await tick();
}

test('Read defaults to browser and repeated reads never open settings', async () => {
    const h = setup();
    for (let i = 0; i < 3; i++) assert.equal((await h.api.playSavedQueue([{ text: 'Hello', lang: 'English' }])).state, 'completed');
    assert.equal(h.spoken.length, 3);
    assert.equal(h.spoken[0].lang, 'en-US');
    assert.equal(h.dialog.open, undefined);
    assert.equal(h.requests.length, 0);
});

test('Save accepts rate and remembers voice across reload without audio or charges', async () => {
    const h = setup(azureResponse());
    h.api.openSettings({ lang: 'Swedish' });
    h.controls.provider.value = 'azure'; h.controls.provider.fire('change');
    await tick();
    assert.equal(h.controls.price.textContent, '1 credits per segment');
    h.controls.voice.value = 'sv-SE-MattiasNeural';
    h.controls.save.fire('click');
    assert.equal(h.audio.length, 0);
    assert.equal(h.spoken.length, 0);
    assert.equal(paidRequests(h).length, 0);
    assert.equal(prefs(h).acceptedAzureRate, 1);
    const reloaded = setup(azureResponse(), prefs(h));
    for (let i = 0; i < 2; i++) assert.equal((await reloaded.api.playSavedQueue([{ text: 'Hej', lang: 'Swedish' }])).state, 'completed');
    assert.equal(reloaded.dialog.open, undefined);
    const payload = JSON.parse(paidRequests(reloaded)[0].body);
    assert.equal(payload.voice, 'sv-SE-MattiasNeural');
    assert.equal(payload.lang, 'sv-SE');
    assert.equal(payload.maxCredits, 1);
    assert.equal(payload.quality, '');
    assert.equal(reloaded.audio.length, 2);
    assert.equal(reloaded.spoken.length, 0);
});

test('Cancel discards provider, voice, rate and book-language edits', async () => {
    const h = setup(azureResponse());
    h.api.openSettings({ lang: 'Swedish', bookId: 'one' });
    h.controls.provider.value = 'azure'; h.controls.provider.fire('change');
    await tick();
    h.controls.cancel.fire('click');
    assert.deepEqual(prefs(h), {});
    assert.equal(h.api.getBookLanguage('one'), '');
    assert.equal(h.api.getProvider(), 'browser');
    assert.equal(paidRequests(h).length, 0);
    assert.equal(h.audio.length + h.spoken.length, 0);
});

test('voices are scoped to language, provider is global, and book overrides stay in their book', async () => {
    const h = setup(azureResponse(), { provider: 'azure', 'azure:pl-PL': 'pl-PL-ZofiaNeural', acceptedAzureRate: 1 });
    assert.equal((await h.api.playSavedQueue([{ text: 'Hej', lang: 'Swedish' }])).state, 'completed');
    assert.equal(JSON.parse(paidRequests(h)[0].body).voice, 'sv-SE-SofieNeural');
    await saveAzure(h, 'Swedish', 'one');
    assert.equal(h.api.getBookLanguage('one'), 'sv-SE');
    assert.equal(h.api.getBookLanguage('two'), '');
    assert.equal(prefs(h)['azure:pl-PL'], 'pl-PL-ZofiaNeural');
    assert.equal((await h.api.playSavedQueue([{ text: 'Cześć', lang: 'Polish' }])).state, 'completed');
    assert.equal(JSON.parse(paidRequests(h)[1].body).voice, 'pl-PL-ZofiaNeural');
});

for (const acceptedAzureRate of [undefined, 1]) {
    test(`missing/increased accepted rate (${acceptedAzureRate}) stops inline without dialog or charge`, async () => {
        const h = setup(azureResponse(2), { provider: 'azure', acceptedAzureRate });
        const result = await h.api.playSavedQueue([{ text: 'Hej', lang: 'Swedish' }]);
        assert.equal(result.state, 'error');
        assert.equal(h.error.textContent, 'Review rate in settings');
        assert.equal(h.error.hidden, false);
        assert.equal(h.dialog.open, undefined);
        assert.equal(paidRequests(h).length, 0);
        await saveAzure(h);
        assert.equal((await h.api.playSavedQueue([{ text: 'Hej', lang: 'Swedish' }])).state, 'completed');
        assert.equal(JSON.parse(paidRequests(h)[0].body).maxCredits, 2);
    });
}

test('unavailable saved voice reports inline instead of silently changing voice', async () => {
    const h = setup(azureResponse(), { provider: 'azure', acceptedAzureRate: 1, 'azure:sv-SE': 'removed' });
    assert.equal((await h.api.playSavedQueue([{ text: 'Hej', lang: 'Swedish' }])).state, 'error');
    assert.equal(h.error.textContent, 'Saved voice unavailable; open settings');
    assert.equal(paidRequests(h).length, 0);
    assert.equal(h.dialog.open, undefined);
});

test('unconfigured browser language cannot use an unrelated voice', async () => {
    const h = setup();
    assert.equal((await h.api.playSavedQueue([{ text: 'Cześć', lang: 'Polish' }])).state, 'error');
    assert.equal(h.error.textContent, 'No browser voices');
    assert.equal(h.spoken.length, 0);
    assert.equal(h.requests.length, 0);
});

test('stop while metadata loads prevents late playback and charges', async () => {
    let complete;
    const states = [];
    const h = setup(() => new Promise(resolve => { complete = resolve; }), { provider: 'azure', acceptedAzureRate: 1 });
    const result = h.api.playSavedQueue([{ text: 'Hej', lang: 'Swedish' }], { onStateChange: state => states.push(state) });
    h.api.stop();
    complete(await azureResponse()('/api/tts/voices?lang=sv-SE'));
    assert.equal((await result).state, 'stopped');
    assert.deepEqual(states, ['playing', 'stopped']);
    assert.equal(paidRequests(h).length, 0);
});

test('late Azure results cannot replace browser settings', async () => {
    let complete;
    const h = setup(() => new Promise(resolve => { complete = resolve; }));
    h.api.openSettings({ lang: 'Swedish' });
    h.controls.provider.value = 'azure'; h.controls.provider.fire('change');
    h.controls.provider.value = 'browser'; h.controls.provider.fire('change');
    complete(await azureResponse()('/api/tts/voices?lang=sv-SE'));
    await tick();
    assert.equal(h.controls.voice.value, 'sv-local');
    assert.equal(h.controls.credits.hidden, true);
});

test('estimate matches bounded requests and does not reserve credits', async () => {
    const h = setup(azureResponse(2), { provider: 'azure', acceptedAzureRate: 2 });
    const items = [{ text: 'a'.repeat(179) + '😀' + 'b'.repeat(30), lang: 'Swedish' }];
    assert.equal(await h.api.estimateQueue(items), 'Azure · Uses AI credits · Estimated total: 4 AI credits');
    assert.equal(paidRequests(h).length, 0);
    assert.equal((await h.api.playSavedQueue(items)).state, 'completed');
    const payloads = paidRequests(h).map(request => JSON.parse(request.body));
    assert.equal(payloads.length, 2);
    assert.equal(payloads[0].text, 'a'.repeat(179));
    assert.equal(payloads[1].text, '😀' + 'b'.repeat(30));
    assert.ok(payloads.every(payload => payload.maxCredits === 2));
});

test('insufficient credits are shown inline with no fallback', async () => {
    const h = setup(async url => url.includes('/voices?') ? azureResponse()(url)
        : { ok: false, status: 402, json: async () => ({ detail: 'Insufficient AI credits' }) }, { provider: 'azure', acceptedAzureRate: 1 });
    assert.equal((await h.api.playSavedQueue([{ text: 'Hej', lang: 'Swedish' }])).state, 'error');
    assert.equal(h.error.textContent, 'Insufficient AI credits');
    assert.equal(h.dialog.open, undefined);
    assert.equal(h.spoken.length, 0);
});
