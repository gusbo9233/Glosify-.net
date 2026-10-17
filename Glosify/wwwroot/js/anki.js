(() => {
    const storageKey = 'glosify.anki.settingsOpen';

    const initialize = () => {
        const workspace = document.querySelector('[data-anki-workspace]');
        const settings = document.querySelector('[data-anki-settings]');
        const toggle = document.querySelector('[data-anki-settings-toggle]');
        if (!workspace || !settings || !toggle || workspace.dataset.ankiReady) return;
        workspace.dataset.ankiReady = 'true';

        const setOpen = (open, remember = true) => {
            settings.hidden = !open;
            workspace.classList.toggle('settings-closed', !open);
            toggle.setAttribute('aria-expanded', String(open));
            toggle.setAttribute('aria-label', `${open ? 'Close' : 'Open'} Anki settings`);
            toggle.title = `${open ? 'Close' : 'Open'} settings`;
            if (remember) {
                try { localStorage.setItem(storageKey, String(open)); } catch { /* Storage may be disabled. */ }
            }
        };

        // Open by default, and always when the collection is empty so the way to add
        // cards is visible.
        let open = true;
        try { open = localStorage.getItem(storageKey) !== 'false'; } catch { /* Optional local preference. */ }
        if (document.querySelector('[data-anki-open-settings]')) open = true;
        setOpen(open, false);

        toggle.addEventListener('click', () => setOpen(settings.hidden));
        settings.addEventListener('keydown', event => {
            if (event.key === 'Escape') {
                event.preventDefault();
                setOpen(false);
                toggle.focus();
            }
        });
        document.querySelectorAll('[data-anki-open-settings]').forEach(button => button.addEventListener('click', () => {
            setOpen(true);
            settings.querySelector('select, input, button')?.focus();
        }));

        document.querySelectorAll('[data-browser-timezone]').forEach(input => {
            try { input.value = Intl.DateTimeFormat().resolvedOptions().timeZone || input.value; } catch { /* Keep the collection zone. */ }
        });

        const list = settings.querySelector('[data-anki-item-list]');
        const search = settings.querySelector('[data-anki-item-search]');
        search?.addEventListener('input', () => {
            const query = search.value.trim().toLocaleLowerCase();
            list.querySelectorAll('[data-anki-search-item]').forEach(item => {
                item.hidden = query.length > 0 && !item.dataset.ankiSearchItem.toLocaleLowerCase().includes(query);
            });
            const group = list.querySelector('[data-anki-item-group]');
            if (group) {
                let next = group.nextElementSibling;
                let visible = false;
                while (next && !visible) { visible = !next.hidden; next = next.nextElementSibling; }
                group.hidden = !visible;
            }
        });

        // Only changed rows are posted, as add/remove keys, so large lists stay small.
        const form = settings.querySelector('[data-anki-items-form]');
        const save = form?.querySelector('[data-anki-items-save]');
        const limit = form?.querySelector('[data-anki-items-limit]');
        // The server rejects larger saves outright, so explain the limit before posting.
        const maxChanges = Number(form?.dataset.ankiMaxChanges) || Infinity;
        const boxes = () => [...(list?.querySelectorAll('[data-anki-item-key]') ?? [])];
        const changed = () => boxes().filter(box => box.checked !== (box.dataset.initial === 'true'));
        list?.addEventListener('change', () => {
            const count = changed().length;
            const tooMany = count > maxChanges;
            save.disabled = count === 0 || tooMany;
            save.textContent = count === 0 ? 'Save changes' : `Save ${count} ${count === 1 ? 'change' : 'changes'}`;
            if (limit) {
                limit.hidden = !tooMany;
                limit.textContent = tooMany ? `Save at most ${maxChanges.toLocaleString()} changes at a time.` : '';
            }
        });
        form?.addEventListener('submit', event => {
            const rows = changed();
            if (rows.length === 0 || rows.length > maxChanges) { event.preventDefault(); return; }
            form.querySelectorAll('input[data-anki-change]').forEach(input => input.remove());
            rows.forEach(box => {
                const input = document.createElement('input');
                input.type = 'hidden';
                input.name = box.checked ? 'Add' : 'Remove';
                input.value = box.dataset.ankiItemKey;
                input.dataset.ankiChange = 'true';
                form.append(input);
            });
            save.disabled = true;
        });
    };

    initialize();
    window.addEventListener('learning:updated', initialize);
})();
