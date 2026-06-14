(() => {
    const panel = document.querySelector('[data-assistant-panel]');
    if (!panel) {
        return;
    }

    const pageQuizId = panel.dataset.quizId || null;
    const pageContextLabel = panel.dataset.contextLabel || null;
    let quizId = pageQuizId;
    let activeThreadId = null;
    let chats = [];
    let initialized = false;
    const chatsUrl = '/Assistant/Chats';
    const chatHistoryUrl = (threadId) => `/Assistant/Chats/${threadId}/History`;
    const chatSendUrl = (threadId) => `/Assistant/Chats/${threadId}/Send`;
    const chatUrl = (threadId) => `/Assistant/Chats/${threadId}`;
    const applyUrl = (messageId) => `/Assistant/Apply/${messageId}`;
    const rejectUrl = (messageId) => `/Assistant/Reject/${messageId}`;
    const activeChatStorageKey = 'glosify.assistant.activeChatId';
    const modelStorageKey = 'glosify.assistant.model';

    const focusedWordId = panel.dataset.focusedWordId || null;
    const toggle = panel.querySelector('[data-assistant-toggle]');
    const close = panel.querySelector('[data-assistant-close]');
    const reset = panel.querySelector('[data-assistant-reset]');
    const newChatButton = panel.querySelector('[data-assistant-new-chat]');
    const windowEl = panel.querySelector('[data-assistant-window]');
    const transcript = panel.querySelector('[data-assistant-transcript]');
    let empty = panel.querySelector('[data-assistant-empty]');
    const status = panel.querySelector('[data-assistant-status]');
    const form = panel.querySelector('[data-assistant-form]');
    const textarea = panel.querySelector('[data-assistant-textarea]');
    const submit = panel.querySelector('[data-assistant-submit]');
    const imageInput = panel.querySelector('[data-assistant-image-input]');
    const scanStatus = panel.querySelector('[data-assistant-scan-status]');
    const modelSelect = panel.querySelector('[data-assistant-model-select]');
    const quizSelector = panel.querySelector('[data-assistant-quiz-selector]');
    const contextLabel = panel.querySelector('[data-assistant-context-label]');
    const chatList = panel.querySelector('[data-assistant-chat-list]');
    const tabButtons = Array.from(panel.querySelectorAll('[data-assistant-tab]'));
    const panes = Array.from(panel.querySelectorAll('[data-assistant-pane]'));
    const tokenInput = panel.querySelector('input[name="__RequestVerificationToken"]')
        || document.querySelector('input[name="__RequestVerificationToken"]');
    const defaultEmptyText = empty?.textContent?.trim() || 'Ask for help anywhere in Glosify.';

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

    const requestHeaders = (json = false) => {
        const headers = {
            'Accept': 'application/json',
            'RequestVerificationToken': tokenInput?.value ?? '',
        };
        if (json) {
            headers['Content-Type'] = 'application/json';
        }
        return headers;
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

    const switchPane = (name) => {
        for (const tab of tabButtons) {
            tab.classList.toggle('is-active', tab.dataset.assistantTab === name);
        }
        for (const pane of panes) {
            const active = pane.dataset.assistantPane === name;
            pane.hidden = !active;
            pane.classList.toggle('is-active', active);
        }
        if (name === 'chat') {
            window.requestAnimationFrame(() => textarea?.focus());
        }
    };

    const renderEmptyState = (message) => {
        const node = document.createElement('div');
        node.className = 'assistant-empty';
        node.dataset.assistantEmpty = '';

        const icon = document.createElement('span');
        icon.className = 'material-symbols-outlined';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = 'auto_awesome';

        const title = document.createElement('strong');
        title.textContent = getActiveChat()?.title || contextLabel?.textContent?.trim() || 'Assistant';

        const copy = document.createElement('span');
        copy.textContent = message || defaultEmptyText;

        node.append(icon, title, copy);
        return node;
    };

    const resetTranscript = (message) => {
        if (!transcript) {
            return;
        }
        transcript.innerHTML = '';
        empty = renderEmptyState(message);
        transcript.appendChild(empty);
    };

    const getActiveChat = () => chats.find(chat => String(chat.id).toLowerCase() === String(activeThreadId).toLowerCase()) || null;

    const setQuizContext = (nextQuizId, label, persist = false) => {
        quizId = nextQuizId || null;
        panel.dataset.quizId = quizId || '';

        if (quizSelector) {
            if (quizId && !Array.from(quizSelector.options).some(option => option.value.toLowerCase() === quizId.toLowerCase())) {
                const option = document.createElement('option');
                option.value = quizId;
                option.dataset.contextLabel = label || 'Selected quiz';
                option.textContent = label || 'Selected quiz';
                quizSelector.appendChild(option);
            }
            quizSelector.value = quizId || '';
        }

        const selectedOption = quizSelector?.selectedOptions?.[0] || null;
        const selectedLabel = label || selectedOption?.dataset.contextLabel || 'Glosify';
        if (contextLabel) {
            contextLabel.textContent = selectedLabel;
        }

        if (persist && activeThreadId) {
            updateChat(activeThreadId, { contextQuizId: quizId, updateContext: true }).catch(() => {
                setStatus('Could not save chat context.', true);
            });
        }
    };

    const loadChats = async () => {
        const response = await fetch(chatsUrl, { headers: { 'Accept': 'application/json' } });
        if (!response.ok) {
            return [];
        }

        const data = await response.json();
        chats = data.chats ?? [];
        renderChatList();
        return chats;
    };

    const createChat = async (contextQuizId = quizId) => {
        const response = await fetch(chatsUrl, {
            method: 'POST',
            headers: requestHeaders(true),
            body: JSON.stringify({ contextQuizId: contextQuizId || null, updateContext: true }),
        });
        if (!response.ok) {
            const data = await response.json().catch(() => null);
            throw new Error(data?.error || 'Could not create chat.');
        }

        const chat = await response.json();
        chats = [chat, ...chats.filter(existing => existing.id !== chat.id)];
        renderChatList();
        return chat;
    };

    const updateChat = async (threadId, payload) => {
        const response = await fetch(chatUrl(threadId), {
            method: 'PATCH',
            headers: requestHeaders(true),
            body: JSON.stringify(payload),
        });
        if (!response.ok) {
            const data = await response.json().catch(() => null);
            throw new Error(data?.error || 'Could not update chat.');
        }

        const updated = await response.json();
        chats = chats.map(chat => chat.id === updated.id ? updated : chat);
        renderChatList();
        return updated;
    };

    const deleteChat = async (threadId) => {
        const response = await fetch(chatUrl(threadId), {
            method: 'DELETE',
            headers: requestHeaders(),
        });
        if (!response.ok) {
            const data = await response.json().catch(() => null);
            throw new Error(data?.error || 'Could not delete chat.');
        }

        chats = chats.filter(chat => chat.id !== threadId);
        if (activeThreadId === threadId) {
            const next = chats[0] || await createChat(quizId);
            await selectChat(next.id);
        } else {
            renderChatList();
        }
    };

    const ensureInitialChat = async () => {
        if (initialized) {
            return;
        }

        await loadChats();
        const storedChatId = localStorage.getItem(activeChatStorageKey);
        const storedChat = storedChatId
            ? chats.find(chat => String(chat.id).toLowerCase() === storedChatId.toLowerCase())
            : null;
        const chat = storedChat || chats[0] || await createChat(quizId);
        await selectChat(chat.id);
        initialized = true;
    };

    const selectChat = async (threadId) => {
        activeThreadId = threadId;
        localStorage.setItem(activeChatStorageKey, threadId);
        const chat = getActiveChat();
        const contextQuizId = pageQuizId || chat?.contextQuizId || null;
        const contextQuizName = pageQuizId
            ? pageContextLabel || chat?.contextQuizName || null
            : chat?.contextQuizName || null;
        setQuizContext(contextQuizId, contextQuizName, false);
        if (pageQuizId && chat?.contextQuizId !== pageQuizId) {
            await updateChat(threadId, { contextQuizId: pageQuizId, updateContext: true });
        }
        renderChatList();
        await loadHistory(threadId);
        switchPane('chat');
        setStatus('');
    };

    const renderChatList = () => {
        if (!chatList) {
            return;
        }

        chatList.innerHTML = '';
        if (chats.length === 0) {
            const emptyList = document.createElement('div');
            emptyList.className = 'assistant-chat-list-empty';
            emptyList.textContent = 'No saved chats yet.';
            chatList.appendChild(emptyList);
            return;
        }

        for (const chat of chats) {
            const item = document.createElement('article');
            item.className = 'assistant-chat-item';
            item.classList.toggle('is-active', chat.id === activeThreadId);

            const main = document.createElement('button');
            main.type = 'button';
            main.className = 'assistant-chat-main';
            main.addEventListener('click', () => selectChat(chat.id));

            const title = document.createElement('strong');
            title.textContent = chat.title || 'New chat';

            const meta = document.createElement('span');
            meta.textContent = [formatChatDate(chat.updatedAt), chat.contextQuizName].filter(Boolean).join(' · ');

            const preview = document.createElement('span');
            preview.textContent = chat.preview || 'Empty chat';

            main.append(title, meta, preview);

            const actions = document.createElement('div');
            actions.className = 'assistant-chat-actions';

            const rename = document.createElement('button');
            rename.type = 'button';
            rename.className = 'assistant-list-action';
            rename.title = 'Rename chat';
            rename.setAttribute('aria-label', 'Rename chat');
            rename.innerHTML = '<span class="material-symbols-outlined" aria-hidden="true">edit</span>';
            rename.addEventListener('click', async () => {
                const nextTitle = window.prompt('Rename chat', chat.title || 'New chat');
                if (nextTitle == null) return;
                try {
                    await updateChat(chat.id, { title: nextTitle });
                } catch (err) {
                    setStatus(err.message, true);
                }
            });

            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'assistant-list-action';
            remove.title = 'Delete chat';
            remove.setAttribute('aria-label', 'Delete chat');
            remove.innerHTML = '<span class="material-symbols-outlined" aria-hidden="true">delete</span>';
            remove.addEventListener('click', async () => {
                if (!window.confirm('Delete this chat?')) return;
                try {
                    await deleteChat(chat.id);
                } catch (err) {
                    setStatus(err.message, true);
                }
            });

            actions.append(rename, remove);
            item.append(main, actions);
            chatList.appendChild(item);
        }
    };

    const formatChatDate = (value) => {
        if (!value) return '';
        const date = new Date(value);
        if (Number.isNaN(date.getTime())) return '';
        return new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).format(date);
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
        const isSentenceFix = message.pendingChanges.every(change => change.kind === 'repair_sentence');
        const isLibraryChange = message.pendingChanges.every(change => change.kind === 'create_quiz' || change.kind === 'create_collection');
        const headingText = isSentenceFix
            ? 'Review sentence fixes'
            : isLibraryChange
                ? 'Review library changes'
                : 'Review proposed changes';

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

            actions.append(apply, reject);
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
        setStatus('Applying changes...');
        try {
            const response = await fetch(applyUrl(messageId), {
                method: 'POST',
                headers: requestHeaders(),
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
            if (data.createdQuizId) {
                await refreshQuizSelector(data.createdQuizId);
                setStatus('Quiz created. Opening it...');
                window.setTimeout(() => {
                    window.location.href = `/Quizzes/Details/${data.createdQuizId}`;
                }, 300);
            } else {
                setStatus('Changes applied. Reloading...');
                window.setTimeout(() => window.location.reload(), 600);
            }
            await loadChats();
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
            const response = await fetch(rejectUrl(messageId), {
                method: 'POST',
                headers: requestHeaders(),
            });
            if (!response.ok) {
                setStatus('Could not reject changes.', true);
                applyBtn.disabled = false;
                rejectBtn.disabled = false;
                return;
            }
            replaceActionsWithTag(card, 'Rejected', 'muted');
            setStatus('');
            await loadChats();
        } catch (err) {
            setStatus('Network error rejecting changes.', true);
            applyBtn.disabled = false;
            rejectBtn.disabled = false;
        }
    };

    const loadHistory = async (threadId) => {
        resetTranscript(defaultEmptyText);
        try {
            const response = await fetch(chatHistoryUrl(threadId), {
                headers: { 'Accept': 'application/json' },
            });
            if (!response.ok) return;
            const data = await response.json();
            resetTranscript(defaultEmptyText);
            for (const message of data.messages ?? []) {
                renderMessage(message);
            }
        } catch (err) {
            // History is best-effort.
        }
    };

    const refreshQuizSelector = async (createdQuizId) => {
        if (!quizSelector) {
            return;
        }

        const response = await fetch('/api/quizzes', {
            headers: { 'Accept': 'application/json' },
        });
        if (!response.ok) {
            return;
        }

        const quizzes = await response.json();
        quizSelector.innerHTML = '<option value="" data-context-label="Glosify">No quiz selected</option>';
        let selectedLabel = null;
        for (const quiz of quizzes ?? []) {
            const option = document.createElement('option');
            option.value = quiz.id;
            option.dataset.contextLabel = quiz.name;
            option.textContent = `${quiz.name} (${quiz.sourceLanguage} -> ${quiz.targetLanguage})`;
            option.selected = String(quiz.id).toLowerCase() === String(createdQuizId).toLowerCase();
            if (option.selected) {
                selectedLabel = quiz.name;
            }
            quizSelector.appendChild(option);
        }

        setQuizContext(createdQuizId, selectedLabel, true);
    };

    tabButtons.forEach(tab => {
        tab.addEventListener('click', () => switchPane(tab.dataset.assistantTab));
    });

    toggle?.addEventListener('click', async () => {
        if (windowEl?.hidden) {
            openAssistant();
        } else {
            closeAssistant();
        }
    });

    const openAssistant = async () => {
        if (!windowEl || !toggle) {
            return;
        }

        windowEl.hidden = false;
        panel.classList.add('is-open');
        toggle.setAttribute('aria-expanded', 'true');
        try {
            await ensureInitialChat();
        } catch (err) {
            setStatus(err.message || 'Could not load chats.', true);
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

    close?.addEventListener('click', closeAssistant);

    const startNewChat = async () => {
        try {
            const chat = await createChat(quizId);
            await selectChat(chat.id);
            setStatus('');
        } catch (err) {
            setStatus(err.message || 'Could not create chat.', true);
        }
    };

    reset?.addEventListener('click', startNewChat);
    newChatButton?.addEventListener('click', startNewChat);

    quizSelector?.addEventListener('change', async () => {
        const selectedOption = quizSelector.selectedOptions?.[0] || null;
        setQuizContext(quizSelector.value || null, selectedOption?.dataset.contextLabel || 'Glosify', true);
        setStatus(quizId ? `Context set to ${contextLabel?.textContent?.trim()}.` : '');
    });

    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape' && !windowEl?.hidden) {
            closeAssistant();
        }
    });

    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        const message = textarea.value.trim();
        if (!message) return;

        if (!activeThreadId) {
            try {
                await ensureInitialChat();
            } catch (err) {
                setStatus(err.message || 'Could not create chat.', true);
                return;
            }
        }

        const documentId = panel.dataset.documentId || null;
        const currentPage = Number(panel.dataset.currentPage || 1);
        const documentContext = documentId
            ? { documentId, pageNumber: Number.isFinite(currentPage) ? currentPage : 1 }
            : null;

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
        setStatus('Thinking...');

        try {
            const response = await fetch(chatSendUrl(activeThreadId), {
                method: 'POST',
                headers: requestHeaders(true),
                body: JSON.stringify({
                    message,
                    contextQuizId: quizId,
                    focusedWordId,
                    model: modelSelect?.value || null,
                    documentContext,
                }),
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
            await loadChats();
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
        if (quizId) {
            body.append('quizId', quizId);
        }
        body.append('image', image);

        imageInput.disabled = true;
        setScanStatus('Reading picture...');
        try {
            const response = await fetch(imageInput.dataset.extractUrl || '/Quiz/ExtractTextFromImage', {
                method: 'POST',
                headers: requestHeaders(),
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
