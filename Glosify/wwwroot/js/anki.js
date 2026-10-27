(() => {
    const storageKey = 'glosify.anki.settingsOpen';
    let dispose = () => {};

    const initialize = (state = {}) => {
        const workspace = document.querySelector('[data-anki-workspace]');
        const settings = document.querySelector('[data-anki-settings]');
        const toggle = document.querySelector('[data-anki-settings-toggle]');
        if (!workspace || !settings || !toggle || workspace.dataset.ankiReady) return;
        dispose();
        workspace.dataset.ankiReady = 'true';

        const setOpen = open => {
            settings.hidden = !open;
            workspace.classList.toggle('settings-closed', !open);
            toggle.setAttribute('aria-expanded', String(open));
            toggle.setAttribute('aria-label', `${open ? 'Close' : 'Open'} Anki settings`);
            toggle.title = `${open ? 'Close' : 'Open'} settings`;
            try { localStorage.setItem(storageKey, String(open)); } catch { /* Storage may be disabled. */ }
        };

        // Open by default, and always when the collection is empty so the way to add
        // cards is visible. Remember that open state so adding the first cards
        // does not restore an older closed preference after the redirect.
        let open = true;
        try { open = localStorage.getItem(storageKey) !== 'false'; } catch { /* Optional local preference. */ }
        if (document.querySelector('[data-anki-open-settings]')) open = true;
        if (typeof state.open === 'boolean') open = state.open;
        setOpen(open);

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

        const quizPicker = settings.querySelector('[data-anki-quiz-picker]');
        quizPicker?.addEventListener('change', async () => {
            if (!quizPicker.dataset.ankiWorkspaceUrl) return;
            if (!canLeaveEdits()) { quizPicker.value = selectedQuiz; return; }
            const url = new URL(quizPicker.dataset.ankiWorkspaceUrl, window.location.href);
            url.searchParams.set('quizId', quizPicker.value);
            if (!await updateWorkspace(url, { method: 'GET' }, quizPicker)) quizPicker.value = selectedQuiz;
        });
        const selectedQuiz = quizPicker?.value;

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
        const warnBeforeUnload = event => {
            if (!workspace.isConnected || changed().length === 0) return;
            event.preventDefault();
            event.returnValue = '';
        };
        window.addEventListener('beforeunload', warnBeforeUnload);
        dispose = () => window.removeEventListener('beforeunload', warnBeforeUnload);
        const canLeaveEdits = () => changed().length === 0
            || window.confirm('Discard your unsaved item changes?');

        const status = settings.querySelector('[data-anki-request-status]');
        const showStatus = message => {
            if (!status) return;
            status.textContent = message;
            status.hidden = !message;
        };
        let busy = false;
        const updateWorkspace = async (url, options, focusTarget) => {
            if (busy) return false;
            if (workspace.querySelector('[data-flashcard-session][aria-busy="true"]')) {
                showStatus('Wait for the current card to finish saving, then try again.');
                return false;
            }
            busy = true;
            workspace.setAttribute('aria-busy', 'true');
            showStatus(options.method === 'GET' ? 'Loading items…' : 'Saving changes…');
            const controls = [...workspace.querySelectorAll('button:not(:disabled), input:not(:disabled), select:not(:disabled)')]
                .filter(control => control !== toggle);
            controls.forEach(control => { control.disabled = true; });
            const focusId = focusTarget?.id;
            const includeSentences = settings.querySelector('[name="IncludeSentences"]')?.checked;
            try {
                const response = await fetch(url, {
                    ...options, headers: { 'X-Requested-With': 'XMLHttpRequest' }, cache: 'no-store',
                });
                if (response.redirected || response.status === 401) {
                    showStatus('Your session or language changed. Reload Anki to continue.');
                    return false;
                }
                if (!response.ok) {
                    const problem = await response.json().catch(() => null);
                    showStatus(problem?.detail || problem?.title || problem?.error || 'Could not save changes. Please try again.');
                    return false;
                }
                const parsed = new DOMParser().parseFromString(await response.text(), 'text/html');
                const next = parsed.querySelector('[data-anki-workspace]');
                if (!next || !workspace.isConnected) throw new Error('Workspace unavailable');
                const scrollTop = settings.scrollTop;
                const listScrollTop = list?.scrollTop;
                const settingsOpen = !settings.hidden;
                // Browsing settings must not turn over the card being studied.
                const study = workspace.querySelector('[data-flashcard-session]');
                if (options.method === 'GET' && study) {
                    next.querySelector('[data-flashcard-session]')?.replaceWith(study);
                }
                workspace.replaceWith(next);
                window.history.replaceState(null, '', next.dataset.ankiUrl);
                initialize({ open: settingsOpen });
                window.dispatchEvent(new CustomEvent('learning:updated'));
                const nextSettings = next.querySelector('[data-anki-settings]');
                nextSettings.scrollTop = scrollTop;
                const nextList = next.querySelector('[data-anki-item-list]');
                if (nextList) nextList.scrollTop = listScrollTop || 0;
                const nextInclude = nextSettings.querySelector('[name="IncludeSentences"]');
                if (nextInclude) nextInclude.checked = includeSentences;
                if (settingsOpen) {
                    const focus = (focusId && document.getElementById(focusId))
                        || nextSettings.querySelector(options.method === 'GET'
                            ? '[data-anki-quiz-picker]' : '[data-anki-request-status]');
                    focus?.focus({ preventScroll: true });
                }
                return true;
            } catch {
                showStatus('Could not update Anki. Your changes may have been saved. Please try again.');
                return false;
            } finally {
                busy = false;
                workspace.setAttribute('aria-busy', 'false');
                controls.forEach(control => { control.disabled = false; });
            }
        };

        settings.addEventListener('submit', async event => {
            if (event.defaultPrevented) return;
            const submittedForm = event.target.closest('form');
            if (!submittedForm || submittedForm.matches('[data-sample-account-form]')) return;
            event.preventDefault();
            if (busy || (submittedForm !== form && !canLeaveEdits())) return;
            const body = new FormData(submittedForm);
            if (event.submitter?.name) body.set(event.submitter.name, event.submitter.value);
            const method = submittedForm.method.toUpperCase();
            const url = new URL(submittedForm.action, window.location.href);
            if (method === 'GET') url.search = new URLSearchParams(body).toString();
            await updateWorkspace(url, { method, ...(method === 'GET' ? {} : { body }) },
                event.submitter || document.activeElement);
        });
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
            if (busy) { event.preventDefault(); return; }
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
        });
    };

    initialize();
    window.addEventListener('learning:updated', () => initialize());
})();
