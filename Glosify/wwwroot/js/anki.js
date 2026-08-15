(() => {
    const createButton = document.querySelector('[data-open-create-anki]');
    const createDialog = document.querySelector('[data-create-anki-dialog]');
    const closeButton = document.querySelector('[data-close-create-anki]');
    createButton?.addEventListener('click', () => createDialog?.showModal());
    closeButton?.addEventListener('click', () => { createDialog?.close(); createButton?.focus(); });
    createDialog?.addEventListener('click', event => {
        if (event.target === createDialog) { createDialog.close(); createButton?.focus(); }
    });

    document.querySelectorAll('[data-browser-timezone]').forEach(input => {
        input.value = Intl.DateTimeFormat().resolvedOptions().timeZone || input.value || 'UTC';
    });
    document.querySelectorAll('[data-confirm]').forEach(form => {
        form.addEventListener('submit', event => {
            if (!window.confirm(form.dataset.confirm)) event.preventDefault();
        });
    });

    const search = document.querySelector('[data-anki-item-search]');
    search?.addEventListener('input', () => {
        const query = search.value.trim().toLocaleLowerCase();
        document.querySelectorAll('[data-anki-search-item]').forEach(item => {
            item.hidden = query.length > 0 && !item.dataset.ankiSearchItem.toLocaleLowerCase().includes(query);
        });
    });

    const startedAt = performance.now();
    const ratingForm = document.querySelector('[data-anki-rating-form]');
    ratingForm?.addEventListener('submit', () => {
        const duration = ratingForm.querySelector('[data-study-duration]');
        if (duration) duration.value = Math.round(performance.now() - startedAt);
        ratingForm.querySelectorAll('button').forEach(button => button.disabled = true);
    });
    document.addEventListener('keydown', event => {
        if (!ratingForm || event.metaKey || event.ctrlKey || event.altKey) return;
        const button = ratingForm.querySelector(`[data-rating-key="${event.key}"]`);
        if (button) { event.preventDefault(); button.click(); }
    });
})();
