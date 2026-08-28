(() => {
    const menus = [...document.querySelectorAll('[data-item-menu]')];
    menus.forEach(menu => menu.addEventListener('toggle', () => {
        if (menu.open) menus.filter(other => other !== menu).forEach(other => other.removeAttribute('open'));
    }));
    document.addEventListener('click', event => {
        menus.filter(menu => menu.open && !menu.contains(event.target)).forEach(menu => menu.removeAttribute('open'));
    });
    document.addEventListener('keydown', event => {
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
        button.closest('[data-item-menu]')?.removeAttribute('open');
        ankiDialog?.showModal();
    }));
    ankiDialog?.querySelector('[data-close-anki-item]')?.addEventListener('click', () => ankiDialog.close());
    ankiDialog?.addEventListener('close', () => ankiTrigger?.focus());
    ankiDialog?.addEventListener('click', event => { if (event.target === ankiDialog) ankiDialog.close(); });

    document.querySelectorAll('[data-confirm]').forEach(form => form.addEventListener('submit', event => {
        if (!window.confirm(form.dataset.confirm)) event.preventDefault();
    }));

})();
