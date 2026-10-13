import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../Glosify/wwwroot/js/assistant.js', import.meta.url), 'utf8');
const section = (start, end) => source.slice(source.indexOf(start), source.indexOf(end));

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
            setStatus() {}, status: { textContent: '' }, matchesOwnedStatus: () => false,
            visibleAssistantLoadError: null,
        });
        vm.runInContext(section('    const canFocusAssistant =', '    const requestHeaders =')
            + section('    const openAssistant =', '    const closeAssistant =')
            + 'globalThis.open = openAssistant;', context);
        const opened = context.open();
        // The collection dialog opens and closes while the assistant HTTP request is pending.
        if (movedOutside) document.activeElement = trigger;
        finishLoading();
        await opened;
        frames.forEach(callback => callback());
        assert.equal(document.activeElement, movedOutside ? trigger : textarea);
    });
}
