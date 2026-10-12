(() => {
    let bindings;
    const initialize = () => {
    bindings?.abort();
    bindings = new AbortController();
    const listenDocument = (type, callback, options = {}) => document.addEventListener(type, callback, { ...(typeof options === 'boolean' ? { capture: options } : options), signal: bindings.signal });
    const listenWindow = (type, callback, options = {}) => window.addEventListener(type, callback, { ...(typeof options === 'boolean' ? { capture: options } : options), signal: bindings.signal });
    const createButton = document.querySelector('[data-open-create-anki]');
    const createDialog = document.querySelector('[data-create-anki-dialog]');
    const closeButton = document.querySelector('[data-close-create-anki]');
    if (createDialog?.open) {
        // Server-rendered `open` is non-modal and participates in page layout.
        // Reopen it through the API so deep links match the button interaction.
        createDialog.close();
        createDialog.showModal();
    }
    createButton?.addEventListener('click', event => {
        event.preventDefault();
        if (createDialog && !createDialog.open) createDialog.showModal();
    });
    closeButton?.addEventListener('click', () => { createDialog?.close(); createButton?.focus(); });
    createDialog?.addEventListener('click', event => {
        if (event.target === createDialog) { createDialog.close(); createButton?.focus(); }
    });

    document.querySelectorAll('[data-browser-timezone]').forEach(input => {
        input.value = Intl.DateTimeFormat().resolvedOptions().timeZone || input.value || 'UTC';
    });

    const search = document.querySelector('[data-anki-item-search]');
    search?.addEventListener('input', () => {
        const query = search.value.trim().toLocaleLowerCase();
        document.querySelectorAll('[data-anki-search-item]').forEach(item => {
            item.hidden = query.length > 0 && !item.dataset.ankiSearchItem.toLocaleLowerCase().includes(query);
        });
    });

    };
    initialize();
    window.addEventListener('learning:updated', initialize);
})();
