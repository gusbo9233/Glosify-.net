(() => {
    let bindings;
    const initialize = () => {
        bindings?.abort();
        bindings = new AbortController();
        const listenDocument = (type, callback, options = {}) => document.addEventListener(type, callback, { ...(typeof options === 'boolean' ? { capture: options } : options), signal: bindings.signal });
        const menus = [...document.querySelectorAll('[data-item-menu]')];
        menus.forEach(menu => menu.addEventListener('toggle', () => {
            if (menu.open) menus.filter(other => other !== menu).forEach(other => other.removeAttribute('open'));
        }, { signal: bindings.signal }));
        listenDocument('click', event => {
            menus.filter(menu => menu.open && !menu.contains(event.target)).forEach(menu => menu.removeAttribute('open'));
        });
        listenDocument('keydown', event => {
            if (event.key !== 'Escape') return;
            menus.filter(menu => menu.open).forEach(menu => { menu.removeAttribute('open'); menu.querySelector('summary')?.focus(); });
        });

        const ankiDialog = document.querySelector('[data-anki-item-dialog]');
        let ankiTrigger = null;
        document.querySelectorAll('[data-open-anki-item]').forEach(button => button.addEventListener('click', () => {
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
        }, { signal: bindings.signal }));
        ankiDialog?.querySelector('[data-close-anki-item]')?.addEventListener('click', () => ankiDialog.close(), { signal: bindings.signal });
        ankiDialog?.addEventListener('close', () => ankiTrigger?.focus(), { signal: bindings.signal });
        ankiDialog?.addEventListener('click', event => { if (event.target === ankiDialog) ankiDialog.close(); }, { signal: bindings.signal });
    };
    initialize();
    window.addEventListener('learning:updated', initialize);
})();
