(() => {
    const t = (key, fallback) => window.glosifyText?.(key, fallback) ?? fallback;
    const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
    const messageHost = document.querySelector('[data-ai-message-host]');

    const setMessage = (text, kind = 'success') => {
        if (!messageHost) return;
        messageHost.innerHTML = '';
        const message = document.createElement('div');
        message.className = `panel-message ${kind}`;
        message.textContent = text;
        messageHost.appendChild(message);
    };

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

    document.querySelectorAll('[data-delete-custom-quiz]').forEach(button => {
        button.addEventListener('click', async () => {
            if (!window.confirm(t('Client.DeleteQuizConfirm', 'Delete this custom quiz?'))) return;
            button.disabled = true;
            try {
                const response = await fetch(button.dataset.deleteUrl, {
                    method: 'DELETE',
                    headers: { 'RequestVerificationToken': tokenInput?.value ?? '' }
                });
                if (!response.ok) throw new Error();
                button.closest('[data-custom-quiz-card]')?.remove();
            } catch {
                setMessage(t('Client.DeleteQuizFailed', 'Could not delete the custom quiz. Try again.'), 'error');
                button.disabled = false;
            }
        });
    });
})();
