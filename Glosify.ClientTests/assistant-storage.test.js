import test from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { loadBrowserModule } from './browser-module.js';

const element = () => ({
    dataset: {}, children: [], listeners: {}, textContent: '',
    classList: { toggle() {}, add() {}, remove() {} },
    addEventListener(name, callback) { this.listeners[name] = callback; },
    setAttribute() {}, focus() {},
    append(...children) { this.children.push(...children); },
    appendChild(child) { this.children.push(child); },
    set innerHTML(value) { this.children = []; },
});

for (const failure of ['getter', 'read', 'write', 'none']) {
    test(`assistant opens and switches chats with ${failure} storage failure`, async () => {
        const controls = new Map(['transcript', 'status', 'form', 'textarea', 'submit', 'chat-list']
            .map(name => [`[data-assistant-${name}]`, element()]));
        const panel = { ...element(), querySelector: selector => controls.get(selector) || null, querySelectorAll: () => [] };
        const history = [];
        const saved = new Map([['glosify.assistant.activeChatId', 'second']]);
        const storage = {
            getItem(key) { if (failure === 'read') throw new Error('Storage blocked'); return saved.get(key) ?? null; },
            setItem(key, value) { if (failure === 'write') throw new Error('Storage full'); saved.set(key, value); },
        };
        let initialize;
        const context = vm.createContext({
            document: { body: { dataset: {} }, querySelector: selector => selector === '[data-assistant-panel]' ? panel : null,
                createElement: element, addEventListener() {} },
            window: { addEventListener() {}, requestAnimationFrame() {}, get sessionStorage() {
                if (failure === 'getter') throw new Error('Storage blocked');
                return storage;
            } },
            sessionStorage: storage,
        });
        // Exercise unavailable global storage as well as throwing read/write methods.
        if (failure === 'getter') vm.runInContext('Object.defineProperty(globalThis, "sessionStorage", { get() { throw new Error("Storage blocked"); } });', context);
        await loadBrowserModule(new URL('./build/assistant/assistant.js', import.meta.url), context, {
            './shell.js': { createAssistantShell(options) {
                initialize = options.ensureInitialChat;
                return { open() {}, close() {}, canFocus: () => false };
            } },
            './api.js': { createAssistantApi: () => ({ json: async () => ({ chats: [
                { id: 'first', title: 'First', messageCount: 0 }, { id: 'second', title: 'Second', messageCount: 0 },
            ] }) }) },
            './transcript-view.js': { createTranscriptView: () => ({
                resetTranscript() {}, renderMessage() {}, renderRun() {}, renderQuizCreatedMessage() {}, onUpdate() {},
                loadHistory: async selection => { history.push(selection.threadId); return true; },
            }) },
            './runs.js': { createAssistantRuns: () => ({ get: () => null }), isTerminal: () => true },
        });
        await initialize();
        assert.equal(panel.dataset.assistantInitialized, 'true');
        assert.equal(controls.get('[data-assistant-submit]').disabled, false);
        const initialId = failure === 'getter' || failure === 'read' ? 'first' : 'second';
        assert.deepEqual(history, [initialId]);
        const nextId = initialId === 'first' ? 'second' : 'first';
        const nextChat = controls.get('[data-assistant-chat-list]').children[nextId === 'first' ? 0 : 1];
        await nextChat.children[0].listeners.click();
        assert.deepEqual(history, [initialId, nextId]);
        assert.equal(controls.get('[data-assistant-submit]').disabled, false);
        if (failure === 'none') assert.equal(saved.get('glosify.assistant.activeChatId'), nextId);
    });
}
