(() => {
    const panel = document.querySelector('[data-assistant-panel]');
    if (!panel) {
        return;
    }

    const quizId = panel.dataset.quizId;
    const focusedWordId = panel.dataset.focusedWordId || null;
    const toggle = panel.querySelector('[data-assistant-toggle]');
    const close = panel.querySelector('[data-assistant-close]');
    const windowEl = panel.querySelector('[data-assistant-window]');
    const transcript = panel.querySelector('[data-assistant-transcript]');
    const empty = panel.querySelector('[data-assistant-empty]');
    const status = panel.querySelector('[data-assistant-status]');
    const form = panel.querySelector('[data-assistant-form]');
    const textarea = panel.querySelector('[data-assistant-textarea]');
    const submit = panel.querySelector('[data-assistant-submit]');
    const imageInput = panel.querySelector('[data-assistant-image-input]');
    const scanStatus = panel.querySelector('[data-assistant-scan-status]');
    const modelSelect = panel.querySelector('[data-assistant-model-select]');
    const tokenInput = panel.querySelector('input[name="__RequestVerificationToken"]')
        || document.querySelector('input[name="__RequestVerificationToken"]');
    let historyLoaded = false;
    const modelStorageKey = 'glosify.assistant.model';

    if (modelSelect) {
        const storedModel = localStorage.getItem(modelStorageKey);
        if (storedModel && Array.from(modelSelect.options).some(option => option.value === storedModel)) {
            modelSelect.value = storedModel;
        }
        modelSelect.addEventListener('change', () => {
            localStorage.setItem(modelStorageKey, modelSelect.value);
        });
    }

    const escapeHtml = (text) => {
        const div = document.createElement('div');
        div.textContent = text ?? '';
        return div.innerHTML;
    };

    const setStatus = (message, isError = false) => {
        if (!message) {
            status.hidden = true;
            status.textContent = '';
            status.classList.remove('is-error');
            return;
        }
        status.hidden = false;
        status.textContent = message;
        status.classList.toggle('is-error', isError);
    };

    const setScanStatus = (message, isError = false) => {
        if (!scanStatus) {
            return;
        }
        if (!message) {
            scanStatus.hidden = true;
            scanStatus.textContent = '';
            scanStatus.classList.remove('is-error');
            return;
        }
        scanStatus.hidden = false;
        scanStatus.textContent = message;
        scanStatus.classList.toggle('is-error', isError);
    };

    const openAssistant = () => {
        if (!windowEl || !toggle) {
            return;
        }

        windowEl.hidden = false;
        panel.classList.add('is-open');
        toggle.setAttribute('aria-expanded', 'true');
        if (!historyLoaded) {
            loadHistory();
            historyLoaded = true;
        }
        window.requestAnimationFrame(() => textarea?.focus());
    };

    const closeAssistant = () => {
        if (!windowEl || !toggle) {
            return;
        }

        windowEl.hidden = true;
        panel.classList.remove('is-open');
        toggle.setAttribute('aria-expanded', 'false');
        toggle.focus();
    };

    const renderMessage = (message) => {
        const hasPendingChanges = Array.isArray(message.pendingChanges) && message.pendingChanges.length > 0;
        if (!message.text && !hasPendingChanges) {
            return;
        }

        if (empty) empty.remove();

        const row = document.createElement('article');
        row.className = `assistant-message assistant-message-${message.role}`;
        row.dataset.messageId = message.id;

        if (message.text) {
            const body = document.createElement('div');
            body.className = 'assistant-bubble';
            body.innerHTML = escapeHtml(message.text).replace(/\n/g, '<br />');
            row.appendChild(body);
        }

        if (hasPendingChanges) {
            const card = renderPendingChanges(message);
            row.appendChild(card);
        }

        transcript.appendChild(row);
        transcript.scrollTop = transcript.scrollHeight;
    };

    const renderPendingChanges = (message) => {
        const card = document.createElement('div');
        card.className = 'assistant-pending-card';
        card.dataset.messageId = message.id;
        const isSentenceFix = message.pendingChanges.every(change =>
            change.kind === 'set_word_detail' || change.kind === 'repair_sentence');
        const headingText = isSentenceFix ? 'Review sentence fixes' : 'Review proposed changes';

        const heading = document.createElement('div');
        heading.className = 'assistant-pending-heading';
        heading.innerHTML = `<span class="material-symbols-outlined" aria-hidden="true">edit_note</span>${headingText} (${message.pendingChanges.length})`;
        card.appendChild(heading);

        const list = document.createElement('ul');
        list.className = 'assistant-pending-list';
        for (const change of message.pendingChanges) {
            const li = document.createElement('li');
            li.textContent = change.summary;
            list.appendChild(li);
        }
        card.appendChild(list);

        if (message.status === 'applied') {
            const tag = document.createElement('div');
            tag.className = 'assistant-pending-tag success';
            tag.textContent = 'Applied';
            card.appendChild(tag);
        } else if (message.status === 'rejected') {
            const tag = document.createElement('div');
            tag.className = 'assistant-pending-tag muted';
            tag.textContent = 'Rejected';
            card.appendChild(tag);
        } else {
            const actions = document.createElement('div');
            actions.className = 'assistant-pending-actions';

            const apply = document.createElement('button');
            apply.type = 'button';
            apply.className = 'btn-submit';
            apply.textContent = isSentenceFix ? 'Apply fixes' : 'Apply';
            apply.addEventListener('click', () => applyChanges(message.id, card, apply, reject));

            const reject = document.createElement('button');
            reject.type = 'button';
            reject.className = 'btn-secondary';
            reject.textContent = 'Reject';
            reject.addEventListener('click', () => rejectChanges(message.id, card, apply, reject));

            actions.appendChild(apply);
            actions.appendChild(reject);
            card.appendChild(actions);
        }

        return card;
    };

    const replaceActionsWithTag = (card, text, kind) => {
        const actions = card.querySelector('.assistant-pending-actions');
        if (actions) actions.remove();
        const tag = document.createElement('div');
        tag.className = `assistant-pending-tag ${kind}`;
        tag.textContent = text;
        card.appendChild(tag);
    };

    const applyChanges = async (messageId, card, applyBtn, rejectBtn) => {
        applyBtn.disabled = true;
        rejectBtn.disabled = true;
        setStatus('Applying changes…');
        try {
            const response = await fetch(`/Quiz/${quizId}/Assistant/Apply/${messageId}`, {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'RequestVerificationToken': tokenInput?.value ?? '',
                },
            });
            if (!response.ok) {
                const data = await response.json().catch(() => null);
                setStatus(data?.error || 'Could not apply changes.', true);
                applyBtn.disabled = false;
                rejectBtn.disabled = false;
                return;
            }
            const data = await response.json();
            replaceActionsWithTag(card, `Applied (${data.applied})`, 'success');
            setStatus('Changes applied. Reloading…');
            window.setTimeout(() => window.location.reload(), 600);
        } catch (err) {
            setStatus('Network error applying changes.', true);
            applyBtn.disabled = false;
            rejectBtn.disabled = false;
        }
    };

    const rejectChanges = async (messageId, card, applyBtn, rejectBtn) => {
        applyBtn.disabled = true;
        rejectBtn.disabled = true;
        try {
            const response = await fetch(`/Quiz/${quizId}/Assistant/Reject/${messageId}`, {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'RequestVerificationToken': tokenInput?.value ?? '',
                },
            });
            if (!response.ok) {
                setStatus('Could not reject changes.', true);
                applyBtn.disabled = false;
                rejectBtn.disabled = false;
                return;
            }
            replaceActionsWithTag(card, 'Rejected', 'muted');
            setStatus('');
        } catch (err) {
            setStatus('Network error rejecting changes.', true);
            applyBtn.disabled = false;
            rejectBtn.disabled = false;
        }
    };

    const loadHistory = async () => {
        try {
            const response = await fetch(`/Quiz/${quizId}/Assistant/History`, {
                headers: { 'Accept': 'application/json' },
            });
            if (!response.ok) return;
            const data = await response.json();
            for (const message of data.messages ?? []) {
                renderMessage(message);
            }
        } catch (err) {
            // History is best-effort.
        }
    };

    toggle?.addEventListener('click', () => {
        if (windowEl?.hidden) {
            openAssistant();
        } else {
            closeAssistant();
        }
    });

    close?.addEventListener('click', closeAssistant);

    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape' && !windowEl?.hidden) {
            closeAssistant();
        }
    });

    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        const message = textarea.value.trim();
        if (!message) return;

        renderMessage({
            id: `local-${Date.now()}`,
            role: 'user',
            text: message,
            toolEvents: [],
            pendingChanges: [],
            status: 'active',
        });
        textarea.value = '';
        submit.disabled = true;
        setStatus('Thinking…');

        try {
            const response = await fetch(`/Quiz/${quizId}/Assistant/Send`, {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': tokenInput?.value ?? '',
                },
                body: JSON.stringify({ message, focusedWordId, model: modelSelect?.value || null }),
            });
            const data = await response.json().catch(() => null);
            if (!response.ok) {
                setStatus(data?.error || 'The assistant could not respond.', true);
                submit.disabled = false;
                return;
            }
            renderMessage({
                id: data.assistantMessageId,
                role: 'model',
                text: data.assistantText,
                toolEvents: data.toolEvents,
                pendingChanges: data.pendingChanges,
                status: data.status,
            });
            setStatus('');
        } catch (err) {
            setStatus('Network error talking to the assistant.', true);
        } finally {
            submit.disabled = false;
            textarea.focus();
        }
    });

    imageInput?.addEventListener('change', async () => {
        const image = imageInput.files?.[0];
        if (!image) {
            return;
        }

        const body = new FormData();
        body.append('quizId', quizId);
        body.append('image', image);

        imageInput.disabled = true;
        setScanStatus('Reading picture…');
        try {
            const response = await fetch(imageInput.dataset.extractUrl || '/Quiz/ExtractTextFromImage', {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'RequestVerificationToken': tokenInput?.value ?? '',
                },
                body,
            });
            const data = await response.json().catch(() => null);
            if (!response.ok || !data?.text) {
                setScanStatus(data?.error || 'Could not read text from that picture.', true);
                return;
            }

            const prompt = `Add useful vocabulary from this text:\n\n${data.text.trim()}`;
            textarea.value = textarea.value.trim()
                ? `${textarea.value.trim()}\n\n${prompt}`
                : prompt;
            textarea.focus();
            setScanStatus('Text added.');
        } catch (err) {
            setScanStatus('Network error reading picture.', true);
        } finally {
            imageInput.value = '';
            imageInput.disabled = false;
        }
    });
})();
