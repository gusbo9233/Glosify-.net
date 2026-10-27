import test from 'node:test';
import assert from 'node:assert/strict';
import { loadBrowserModule } from './browser-module.js';
import vm from 'node:vm';

for (const movedOutside of [false, true]) {
    test(`delayed assistant loading ${movedOutside ? 'preserves focus restored by a closed dialog' : 'focuses the composer when focus remains inside'}`, async () => {
        let finishLoading;
        const loading = new Promise(resolve => { finishLoading = resolve; });
        const toggle = { setAttribute() {} };
        const trigger = {};
        const document = { body: {}, activeElement: toggle, querySelector: () => null };
        const textarea = { focus() { document.activeElement = textarea; } };
        const frames = [];
        const context = vm.createContext({
            document, textarea, toggle, windowEl: { hidden: true },
            panel: { classList: { add() {} }, contains: element => element === toggle || element === textarea },
            window: { dispatchEvent() {}, requestAnimationFrame: callback => frames.push(callback) },
            CustomEvent: class {}, ensureInitialChat: () => loading, ensureContextOptions: async () => {},
            setStatus() {}, status: { textContent: '' },
        });
        const { createAssistantShell } = await loadBrowserModule(new URL('./build/assistant/shell.js', import.meta.url), context);
        const shell = createAssistantShell(context);
        const opened = shell.open();
        // The collection dialog opens and closes while the assistant HTTP request is pending.
        if (movedOutside) document.activeElement = trigger;
        finishLoading();
        await opened;
        frames.forEach(callback => callback());
        assert.equal(document.activeElement, movedOutside ? trigger : textarea);
    });
}
