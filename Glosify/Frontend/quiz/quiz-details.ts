export {};

(() => {
    let bindings: AbortController | undefined;
    const initialize = () => {
        bindings?.abort();
        const controller = new AbortController();
        bindings = controller;
        const listenDocument = <K extends keyof DocumentEventMap>(type: K, callback: (event: DocumentEventMap[K]) => void) => document.addEventListener(type, callback, { signal: controller.signal });
        const menus = [...document.querySelectorAll<HTMLDetailsElement>('[data-item-menu]')];
        menus.forEach(menu => menu.addEventListener('toggle', () => {
            if (menu.open) menus.filter(other => other !== menu).forEach(other => other.removeAttribute('open'));
        }, { signal: controller.signal }));
        listenDocument('click', event => {
            menus.filter(menu => menu.open && !menu.contains(event.target as Node | null)).forEach(menu => menu.removeAttribute('open'));
        });
        listenDocument('keydown', event => {
            if (event.key !== 'Escape') return;
            menus.filter(menu => menu.open).forEach(menu => { menu.removeAttribute('open'); menu.querySelector('summary')?.focus(); });
        });

        const ankiDialog = document.querySelector<HTMLDialogElement>('[data-anki-item-dialog]');
        let ankiTrigger: HTMLElement | null = null;
        document.querySelectorAll<HTMLElement>('[data-open-anki-item]').forEach(button => button.addEventListener('click', () => {
            ankiTrigger = button;
            const itemType = ankiDialog?.querySelector('[data-anki-item-type]');
            const itemId = ankiDialog?.querySelector('[data-anki-item-id]');
            if (itemType instanceof HTMLInputElement) itemType.value = button.dataset.itemType || '';
            if (itemId instanceof HTMLInputElement) itemId.value = button.dataset.itemId || '';
            const label = ankiDialog?.querySelector('[data-anki-item-label]');
            if (label) label.textContent = button.dataset.itemLabel || 'card';
            const timeZone = ankiDialog?.querySelector('[data-browser-timezone]');
            if (timeZone instanceof HTMLInputElement) {
                try { timeZone.value = Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch { /* Keep the collection's zone. */ }
            }
            button.closest('[data-item-menu]')?.removeAttribute('open');
            ankiDialog?.showModal();
        }, { signal: controller.signal }));
        ankiDialog?.querySelector('[data-close-anki-item]')?.addEventListener('click', () => ankiDialog.close(), { signal: controller.signal });
        ankiDialog?.addEventListener('close', () => ankiTrigger?.focus(), { signal: controller.signal });
        ankiDialog?.addEventListener('click', event => { if (event.target === ankiDialog) ankiDialog.close(); }, { signal: controller.signal });
    };
    initialize();
    window.addEventListener('learning:updated', initialize);
})();
