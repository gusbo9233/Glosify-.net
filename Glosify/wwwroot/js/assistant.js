(() => {
    const panel = document.querySelector('[data-assistant-panel]');
    if (!panel) {
        return;
    }

    const quizId = panel.dataset.quizId;
    const transcript = panel.querySelector('[data-assistant-transcript]');
    const empty = panel.querySelector('[data-assistant-empty]');
    const status = panel.querySelector('[data-assistant-status]');
    const form = panel.querySelector('[data-assistant-form]');
    const textarea = panel.querySelector('[data-assistant-textarea]');
    const submit = panel.querySelector('[data-assistant-submit]');
    const imageInput = panel.querySelector('[data-assistant-image-input]');
    const scanStatus = panel.querySelector('[data-assistant-scan-status]');
    const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');

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

    const renderMessage = (message) => {
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

        if (Array.isArray(message.toolEvents) && message.toolEvents.length > 0) {
            const list = document.createElement('ul');
            list.className = 'assistant-tool-list';
            for (const ev of message.toolEvents) {
                const li = document.createElement('li');
                li.className = 'assistant-tool';
                li.innerHTML = `<span class="material-symbols-outlined" aria-hidden="true">build</span><span class="assistant-tool-name">${escapeHtml(ev.name)}</span>`;
                list.appendChild(li);
            }
            row.appendChild(list);
        }

        if (Array.isArray(message.pendingChanges) && message.pendingChanges.length > 0) {
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

        const heading = document.createElement('div');
        heading.className = 'assistant-pending-heading';
        heading.innerHTML = `<span class="material-symbols-outlined" aria-hidden="true">edit_note</span>Proposed changes (${message.pendingChanges.length})`;
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
            apply.textContent = 'Apply';
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
                body: JSON.stringify({ message }),
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

    loadHistory();
})();
