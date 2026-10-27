import { requiredElement } from '../shared/dom.js';
import { errorMessage, errorStatus } from '../shared/errors.js';
import { text as t } from '../shared/localization.js';

interface AssistantProblem { code?: string; detail?: string; }
interface ChatUpdate extends ChatContext { title?: string; updateContext?: boolean; }
interface ContextWrite { threadId: string; payload: ChatUpdate; }
declare global {
    interface WindowEventMap {
        'assistant:avatar': CustomEvent<{ conversationId: string; quiz?: boolean }>;
        'assistant:avatar-ended': CustomEvent<{ conversationId: string }>;
    }
}

import { createAssistantShell } from './shell.js';
import { createTranscriptView, type ChatSelection, type AssistantMessage } from './transcript-view.js';
import { runStatusText } from './run-controls.js';
import { avatarAssociations } from './avatar-context.js';
import { createAssistantRuns, isTerminal } from './runs.js';
import { createAssistantApi } from './api.js';
import { chatContext, chooseInitialChat, createRecoverablePromiseQueue, createRecoverableSingleFlight, materialPayload as buildMaterialPayload, removeChat, replaceChat, upsertChat, type Chat, type ChatContext } from './state.js';
import { formatChatDate } from './presentation.js';
import { createFeedbackView } from './feedback-view.js';
import { currentBookPageContext, populateContextOptions, type ContextOptions, type QuizOption } from './context-options.js';
import {
    validClientDuration, type Feedback,
} from './feedback.js';

(() => {
    const panel = document.querySelector<HTMLElement>('[data-assistant-panel]');
    if (!panel) {
        return;
    }

    const localizedProblem = (data: AssistantProblem | null, response: Response | null, fallbackKey: string, fallback: string) => {
        const code = data?.code;
        if (code === 'unauthorized' || response?.status === 401) {
            return t('Client.SessionExpired', 'Your session expired. Sign in and try again.');
        }
        if (code === 'rate_limited' || response?.status === 429) {
            return t('Client.TryAgain', 'Please try again.');
        }
        if (code === 'payment_required' || code === 'paid_services_budget_exhausted') {
            return t('Books.PaidUnavailable', '{0} Paid features reopen at {1}.',
                t('Books.PaidReason', 'The monthly application budget has been reached.'),
                t('Common.NextMonth', 'the start of next month'));
        }
        return t(fallbackKey, fallback);
    };

    const pageQuizId = panel.dataset.quizId || null;
    const pageContextLabel = panel.dataset.contextLabel || null;
    const pageTranscriptId = panel.dataset.transcriptId || null;
    const pageDocumentId = panel.dataset.documentId || null;
    // "Material" is the book or transcript the chat reads from, as opposed to the quiz,
    // which is where proposed changes land. The page the panel opened on only seeds it;
    // from then on the picker owns it, so navigating around never replaces the choice.
    const pageMaterialKind = pageTranscriptId ? 'transcript' : pageDocumentId ? 'book' : null;
    const pageMaterialId = pageTranscriptId || pageDocumentId || null;
    // Only the transcript reader sets these, and only for the transcript it is showing.
    const pageTranscriptPage = Number(panel.dataset.transcriptPage || 0);
    const pageTranscriptStream = panel.dataset.transcriptStream || null;
    let avatarStorage;
    try { avatarStorage = window.sessionStorage; } catch { /* Browser storage may be blocked. */ }
    const avatarLinks = avatarAssociations(document.body?.dataset.speechUser, avatarStorage);
    const avatarContexts = avatarLinks.contexts;
    const avatarThreads = avatarLinks.threads;
    let avatarOpening = false;
    let materialKind: string | null = pageMaterialKind;
    let materialId = pageMaterialId;
    let quizId = pageQuizId;
    let activeThreadId: string | null = null;
    let chatSelection: ChatSelection | null = null;
    let activeSelectionPromise = Promise.resolve();
    const pendingSends = new Set<string>();
    const sendErrors = new Map<string, string>();
    const pendingContextWrites = new Map<string, Promise<boolean>>();
    const ownsSelection = (selection: ChatSelection | null) => selection === chatSelection;
    const canConfirmContext = (selection: ChatSelection | null) => ownsSelection(selection)
        && selection?.historyLoaded
        && !pendingContextWrites.has(selection.threadId)
        && !pendingSends.has(selection.threadId)
        && !sendErrors.has(selection.threadId);
    let chats: Chat[] = [];
    let chatCatalogRevision = 0;
    let initialized = false;
    const chatsUrl = '/Assistant/Chats';
    const chatUrl = (threadId: string) => `/Assistant/Chats/${threadId}`;
    const applyUrl = (messageId: string) => `/Assistant/Apply/${messageId}`;
    const rejectUrl = (messageId: string) => `/Assistant/Reject/${messageId}`;
    const feedbackUrl = (turnId: string) => `/Assistant/Turns/${turnId}/Feedback`;
    const clientMetricsUrl = (turnId: string) => `/Assistant/Turns/${turnId}/ClientMetrics`;
    const contextOptionsUrl = panel.dataset.contextOptionsUrl || '/Assistant/ContextOptions';
    // Session-scoped on purpose: a new browser session starts on a fresh chat
    // instead of resuming whatever was open days ago.
    const activeChatStorageKey = 'glosify.assistant.activeChatId';

    const focusedWordId = panel.dataset.focusedWordId || null;
    const toggle = panel.querySelector<HTMLElement>('[data-assistant-toggle]');
    const close = panel.querySelector<HTMLElement>('[data-assistant-close]');
    const reset = panel.querySelector<HTMLButtonElement>('[data-assistant-reset]');
    const newChatButton = panel.querySelector<HTMLButtonElement>('[data-assistant-new-chat]');
    const newChatControls = [reset, newChatButton].filter((button): button is HTMLButtonElement => !!button);
    newChatControls.forEach(button => { button.disabled = true; });
    const windowEl = panel.querySelector<HTMLElement>('[data-assistant-window]');
    const transcript = requiredElement<HTMLElement>(panel, '[data-assistant-transcript]');
    const empty = panel.querySelector<HTMLElement>('[data-assistant-empty]');
    const status = requiredElement<HTMLElement>(panel, '[data-assistant-status]');
    const form = requiredElement<HTMLFormElement>(panel, '[data-assistant-form]');
    const textarea = requiredElement<HTMLTextAreaElement>(panel, '[data-assistant-textarea]');
    const submit = requiredElement<HTMLButtonElement>(panel, '[data-assistant-submit]');
    submit.disabled = true;
    const imageInput = panel.querySelector<HTMLInputElement>('[data-assistant-image-input]');
    const scanStatus = panel.querySelector<HTMLElement>('[data-assistant-scan-status]');
    const quizSelector = panel.querySelector<HTMLSelectElement>('[data-assistant-quiz-selector]');
    const materialSelector = panel.querySelector<HTMLSelectElement>('[data-assistant-material-selector]');
    const chatList = panel.querySelector<HTMLElement>('[data-assistant-chat-list]');
    const tabButtons = Array.from(panel.querySelectorAll<HTMLElement>('[data-assistant-tab]'));
    const panes = Array.from(panel.querySelectorAll<HTMLElement>('[data-assistant-pane]'));
    const tokenInput = panel.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]')
        || document.querySelector<HTMLInputElement>('input[name="__RequestVerificationToken"]');
    const defaultEmptyText = empty?.textContent?.trim() || t('Assistant.Empty', 'Ask for help anywhere in GlobeGlotter.');
    const { open: openAssistant, close: closeAssistant, canFocus: canFocusAssistant } = createAssistantShell({
        panel, windowEl, toggle, textarea, status,
        ensureInitialChat: () => ensureInitialChat(),
        ensureContextOptions: () => ensureContextOptions(),
        setStatus: (message, isError) => setStatus(message, isError),
    });

    const requestHeaders = (json = false) => {
        const headers: Record<string, string> = {
            'Accept': 'application/json',
            'RequestVerificationToken': tokenInput?.value ?? '',
        };
        if (json) {
            headers['Content-Type'] = 'application/json';
        }
        return headers;
    };
    const api = createAssistantApi(() => tokenInput?.value);
    let contextOptionsBusy = false;

    const updateContextControls = () => {
        const contextReady = !!chatSelection?.contextReady;
        newChatControls.forEach(button => { button.disabled = !contextReady; });
        const busy = contextOptionsBusy || !contextReady;
        for (const selector of [quizSelector, materialSelector]) {
            if (!selector) {
                continue;
            }
            selector.disabled = busy;
            selector.setAttribute('aria-busy', String(busy));
        }
    };

    const setContextSelectorsBusy = (busy: boolean) => {
        contextOptionsBusy = busy;
        updateContextControls();
    };
    updateContextControls();

    const loadContextOptions = async () => {
        if (!quizSelector || !materialSelector) {
            return;
        }

        setContextSelectorsBusy(true);
        try {
            const options = await api.json<ContextOptions>(
                contextOptionsUrl,
                {},
                t('Client.GenericError', 'Something went wrong. Please try again.'));
            populateContextOptions({
                quizSelector,
                materialSelector,
                options: options || {},
                selectedQuizId: quizId,
                selectedMaterialKind: materialKind,
                selectedMaterialId: materialId,
                fallbackContextLabel: pageContextLabel,
            });
        } catch (error) {
            populateContextOptions({
                quizSelector,
                materialSelector,
                options: {},
                selectedQuizId: quizId,
                selectedMaterialKind: materialKind,
                selectedMaterialId: materialId,
                fallbackContextLabel: pageContextLabel,
            });
            throw error;
        } finally {
            setContextSelectorsBusy(false);
        }
    };

    const ensureContextOptions = createRecoverableSingleFlight(loadContextOptions);

    const setStatus = (message: string | undefined, isError = false) => {
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

    const setScanStatus = (message: string, isError = false) => {
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

    const switchPane = (name: string | undefined) => {
        for (const tab of tabButtons) {
            tab.classList.toggle('is-active', tab.dataset.assistantTab === name);
        }
        for (const pane of panes) {
            const active = pane.dataset.assistantPane === name;
            pane.hidden = !active;
            pane.classList.toggle('is-active', active);
        }
        if (name === 'chat') {
            window.requestAnimationFrame(() => {
                if (canFocusAssistant()) {
                    textarea?.focus();
                }
            });
        }
    };

    const getActiveChat = () => chats.find(chat => String(chat.id).toLowerCase() === String(activeThreadId).toLowerCase()) || null;

    // The server treats updateContext as all-or-nothing, so every context write has to
    // carry the complete picture or the fields it omits are cleared.
    const materialPayload = () => buildMaterialPayload(materialKind, materialId);

    const materialLabel = () => materialSelector?.selectedOptions?.[0]?.dataset.contextLabel || null;

    const contextWrites = createRecoverablePromiseQueue(({ threadId, payload }: ContextWrite) => updateChat(threadId, payload));

    const enqueueContextWrite = (snapshot: ContextWrite) => {
        const selection = chatSelection;
        const pending = contextWrites(snapshot).then(
            () => true,
            () => {
                if (ownsSelection(selection)) setStatus('Could not save chat context.', true);
                return false;
            }).finally(() => {
                if (pendingContextWrites.get(snapshot.threadId) === pending) {
                    pendingContextWrites.delete(snapshot.threadId);
                }
            });
        pendingContextWrites.set(snapshot.threadId, pending);
        return pending;
    };

    const persistContext = () => {
        if (!activeThreadId) {
            return Promise.resolve(true);
        }
        // Capture the whole context at invocation time. Later picker changes must not
        // alter this queued request before it reaches the server.
        return enqueueContextWrite({
            threadId: activeThreadId,
            payload: {
                contextQuizId: quizId,
                ...materialPayload(),
                updateContext: true,
            },
        });
    };

    const setMaterialContext = (nextKind: string | null, nextId: string | null, persist = false) => {
        materialId = nextId || null;
        materialKind = materialId ? nextKind : null;
        if (activeThreadId) {
            if (materialKind === 'avatar' && materialId) avatarContexts.set(activeThreadId, materialId);
            else avatarContexts.delete(activeThreadId);
            avatarLinks.persist();
        }

        if (materialSelector) {
            materialSelector.value = materialId ? `${materialKind}:${materialId}` : '';
            // A book or transcript from another language is not in the list, so the
            // select would silently fall back to "No material selected" and the next
            // context write would clear a choice the user never touched.
            if (materialId && !materialSelector.value) {
                const option = document.createElement('option');
                option.value = `${materialKind}:${materialId}`;
                option.dataset.contextLabel = materialKind === 'avatar' ? 'Current Speaking conversation' : getActiveChat()?.contextBookTitle
                    || getActiveChat()?.contextTranscriptTitle
                    || 'Selected material';
                option.textContent = option.dataset.contextLabel;
                materialSelector.appendChild(option);
                materialSelector.value = option.value;
            }
        }

        if (persist) {
            return persistContext();
        }
        return Promise.resolve(true);
    };

    const setQuizContext = (nextQuizId: string | null, label: string | null, persist = false) => {
        quizId = nextQuizId || null;
        panel.dataset.quizId = quizId || '';

        if (quizSelector) {
            if (quizId && !Array.from(quizSelector.options).some(option => option.value.toLowerCase() === quizId!.toLowerCase())) {
                const option = document.createElement('option');
                option.value = quizId;
                option.dataset.contextLabel = label || 'Selected quiz';
                option.textContent = label || 'Selected quiz';
                quizSelector.appendChild(option);
            }
            quizSelector.value = quizId || '';
        }

        if (persist) {
            return persistContext();
        }
        return Promise.resolve(true);
    };

    const loadChats = async () => {
        const revision = ++chatCatalogRevision;
        try {
            const data = await api.json<{ chats: Chat[] }>(chatsUrl);
            if (revision !== chatCatalogRevision) return chats;
            chats = data?.chats ?? [];
            renderChatList();
            return chats;
        } catch {
            return [];
        }
    };

    const createChat = async (context: ChatContext = chatContext(null)) => {
        const response = await fetch(chatsUrl, {
            method: 'POST',
            headers: requestHeaders(true),
            body: JSON.stringify({
                ...context,
                updateContext: true,
            }),
        });
        if (!response.ok) {
            const data = await response.json().catch(() => null);
            throw new Error(localizedProblem(data, response, 'Client.GenericError', 'Could not create chat.'));
        }

        const chat: Chat = await response.json();
        chatCatalogRevision++;
        chats = upsertChat(chats, chat);
        renderChatList();
        return chat;
    };

    const updateChat = async (threadId: string, payload: ChatUpdate) => {
        const response = await fetch(chatUrl(threadId), {
            method: 'PATCH',
            headers: requestHeaders(true),
            body: JSON.stringify(payload),
        });
        if (!response.ok) {
            const data = await response.json().catch(() => null);
            throw new Error(localizedProblem(data, response, 'Client.GenericError', 'Could not update chat.'));
        }

        const updated: Chat = await response.json();
        chatCatalogRevision++;
        chats = replaceChat(chats, updated);
        renderChatList();
        return updated;
    };

    const deleteChat = async (threadId: string) => {
        const response = await fetch(chatUrl(threadId), {
            method: 'DELETE',
            headers: requestHeaders(),
        });
        if (!response.ok) {
            const data = await response.json().catch(() => null);
            throw new Error(localizedProblem(data, response, 'Client.GenericError', 'Could not delete chat.'));
        }
        await response.text();

        chatCatalogRevision++;
        chats = removeChat(chats, threadId);
        sendErrors.delete(threadId);
        if (activeThreadId === threadId) {
            const next = chats[0] || await createChat();
            await selectChat(next.id);
        } else {
            renderChatList();
        }
    };

    const ensureInitialChat = createRecoverableSingleFlight(async () => {
        if (initialized) {
            return;
        }

        await loadChats();
        const storedChatId = sessionStorage.getItem(activeChatStorageKey);
        const storedChat = chooseInitialChat(chats, storedChatId);
        // First open in this session: start empty. Reuse the newest chat when it is
        // already blank so repeat sessions don't pile up empty threads in the list.
        const chat = storedChat
            || await createChat({
                contextQuizId: pageQuizId,
                ...buildMaterialPayload(pageMaterialKind, pageMaterialId),
            });
        await selectChat(chat.id);
        initialized = true;
        panel.dataset.assistantInitialized = 'true';
    });

    const selectChat = (threadId: string) => {
        const selection = { threadId };
        chatSelection = selection;
        activeThreadId = threadId;
        updateContextControls();
        resetTranscript(defaultEmptyText);
        submit.disabled = true;
        setStatus(t('Client.Loading', 'Loading…'));
        activeSelectionPromise = loadSelectedChat(selection);
        return activeSelectionPromise;
    };

    const loadSelectedChat = async (selection: ChatSelection) => {
        const { threadId } = selection;
        sessionStorage.setItem(activeChatStorageKey, threadId);
        let contextPersisted = true;
        while (pendingContextWrites.has(threadId)) {
            contextPersisted = await pendingContextWrites.get(threadId)!;
            if (!ownsSelection(selection)) return;
        }
        const chat = getActiveChat();
        const selectedContext = chatContext(chat);
        const contextQuizId = selectedContext.contextQuizId;
        const contextQuizName = chat?.contextQuizName || null;

        // Each chat owns its context. The page only seeds a chat when it is first created.
        const storedKind = selectedContext.contextTranscriptId
            ? 'transcript'
            : selectedContext.contextBookDocumentId ? 'book' : null;
        const storedId = selectedContext.contextTranscriptId || selectedContext.contextBookDocumentId;
        const avatarId = avatarContexts.get(threadId);
        setMaterialContext(avatarId ? 'avatar' : storedKind, avatarId || storedId, false);

        setQuizContext(contextQuizId, contextQuizName, false);
        selection.contextReady = true;
        updateContextControls();
        if (!ownsSelection(selection)) return;
        renderChatList();
        const historyLoaded = await loadHistory(selection);
        if (!ownsSelection(selection)) return;
        switchPane('chat');
        if (!historyLoaded) {
            setStatus(t('Client.GenericError', 'Something went wrong. Please try again.'), true);
            return;
        }
        selection.historyLoaded = true;
        // Streams may advance while reopening a chat. Their updates stay cached until
        // its initial history is installed, so they cannot invalidate that initial load.
        const currentRun = runs.get(threadId);
        if (currentRun && !isTerminal(currentRun.status)) renderRun(currentRun);
        else if (currentRun) void loadHistory(selection, false);
        submit.disabled = pendingSends.has(threadId);
        if (!contextPersisted) setStatus('Could not save chat context.', true);
        else if (pendingSends.has(threadId)) setStatus(t('Client.AssistantRunWorking', 'Working…'));
        else if (sendErrors.has(threadId)) setStatus(sendErrors.get(threadId), true);
        else setStatus('');
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
            item.dataset.assistantChatItem = '';
            item.classList.toggle('is-active', chat.id === activeThreadId);

            const main = document.createElement('button');
            main.type = 'button';
            main.className = 'assistant-chat-main';
            main.addEventListener('click', () => selectChat(chat.id));

            const title = document.createElement('strong');
            title.textContent = chat.title || 'New chat';

            const meta = document.createElement('span');
            meta.textContent = [
                formatChatDate(chat.updatedAt || ''),
                chat.contextQuizName,
                chat.contextBookTitle || chat.contextTranscriptTitle,
            ].filter(Boolean).join(' · ');

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
                    setStatus(errorMessage(err, 'Assistant request failed.'), true);
                }
            });

            const remove = document.createElement('button');
            remove.type = 'button';
            remove.className = 'assistant-list-action';
            remove.title = t('Client.DeleteChat', 'Delete chat');
            remove.setAttribute('aria-label', t('Client.DeleteChat', 'Delete chat'));
            remove.innerHTML = '<span class="material-symbols-outlined" aria-hidden="true">delete</span>';
            remove.addEventListener('click', async () => {
                if (!window.confirm(t('Client.DeleteChatConfirm', 'Delete this chat?'))) return;
                try {
                    await deleteChat(chat.id);
                } catch (err) {
                    setStatus(errorMessage(err, 'Assistant request failed.'), true);
                }
            });

            actions.append(rename, remove);
            item.append(main, actions);
            chatList.appendChild(item);
        }
    };

    const renderFeedback = (turnId: string, initialFeedback?: Feedback | null) => createFeedbackView(initialFeedback, {
        put: next => api.json<Feedback>(feedbackUrl(turnId), { method: 'PUT', body: JSON.stringify(next) }, 'Could not save feedback.'),
        remove: () => api.json(feedbackUrl(turnId), { method: 'DELETE' }, 'Could not clear feedback.'),
        onError: error => setStatus(errorMessage(error, 'Assistant request failed.'), true),
    });

    const renderPendingChanges = (message: AssistantMessage) => {
        const card = document.createElement('div');
        card.className = 'assistant-pending-card';
        card.dataset.assistantPendingCard = '';
        card.dataset.messageId = message.id;
        const isLibraryChange = (message.pendingChanges || []).every(change => change.kind === 'create_quiz' || change.kind === 'create_collection');
        const headingText = isLibraryChange
                ? 'Review library changes'
                : 'Review proposed changes';

        const heading = document.createElement('div');
        heading.className = 'assistant-pending-heading';
        heading.innerHTML = `<span class="material-symbols-outlined" aria-hidden="true">edit_note</span>${headingText} (${(message.pendingChanges || []).length})`;
        card.appendChild(heading);

        const list = document.createElement('ul');
        list.className = 'assistant-pending-list';
        for (const change of message.pendingChanges || []) {
            const li = document.createElement('li');
            li.dir = 'auto';
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

            actions.append(apply, reject);
            card.appendChild(actions);
        }

        return card;
    };

    const replaceActionsWithTag = (card: HTMLElement, text: string, kind: string) => {
        const actions = card.querySelector<HTMLElement>('.assistant-pending-actions');
        if (actions) actions.remove();
        const tag = document.createElement('div');
        tag.className = `assistant-pending-tag ${kind}`;
        tag.textContent = text;
        card.appendChild(tag);
    };

    const applyChanges = async (messageId: string, card: HTMLElement, applyBtn: HTMLButtonElement, rejectBtn: HTMLButtonElement) => {
        applyBtn.disabled = true;
        rejectBtn.disabled = true;
        setStatus(t('Client.Loading', 'Loading…'));
        try {
            const response = await fetch(applyUrl(messageId), {
                method: 'POST',
                headers: requestHeaders(),
            });
            if (!response.ok) {
                const data = await response.json().catch(() => null);
                setStatus(localizedProblem(data, response, 'Client.ApplyFailed', 'Could not apply changes.'), true);
                applyBtn.disabled = false;
                rejectBtn.disabled = false;
                return;
            }
            const data = await response.json();
            replaceActionsWithTag(card, `${t('Client.Correct', 'Done')} (${data.applied})`, 'success');
            if (data.applied > 0) {
                window.dispatchEvent(new CustomEvent('assistant:changes', { detail: { id: messageId, changeRevision: String(data.applied) } }));
                void loadContextOptions();
            }
            if (data.createdQuizId) {
                const contextSaved = await selectCreatedQuiz(data.createdQuiz || {
                    id: data.createdQuizId,
                    name: t('Settings.Quiz', 'Quiz'),
                    sourceLanguage: '',
                    targetLanguage: '',
                });
                renderQuizCreatedMessage(data.createdQuizId);
                setStatus(contextSaved
                    ? 'Quiz created.'
                    : 'Quiz created, but its chat context could not be saved.', !contextSaved);
            } else {
                setStatus('Changes applied.');
            }
            await loadChats();
        } catch {
            setStatus(t('Client.ApplyNetwork', 'Network error applying changes.'), true);
            applyBtn.disabled = false;
            rejectBtn.disabled = false;
        }
    };

    const rejectChanges = async (messageId: string, card: HTMLElement, applyBtn: HTMLButtonElement, rejectBtn: HTMLButtonElement) => {
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
        } catch {
            setStatus(t('Client.RejectNetwork', 'Network error rejecting changes.'), true);
            applyBtn.disabled = false;
            rejectBtn.disabled = false;
        }
    };

    const { resetTranscript, renderMessage, renderRun, renderQuizCreatedMessage, loadHistory, onUpdate } = createTranscriptView({
        transcript, empty, defaultEmptyText,
        getTitle: () => getActiveChat()?.title || pageContextLabel || 'Assistant',
        getSelection: () => chatSelection,
        getRuns: () => runs,
        renderPendingChanges: message => renderPendingChanges(message),
        renderFeedback: (turnId, feedback) => renderFeedback(turnId, feedback),
        setStatus: (message, isError) => setStatus(message, isError),
        onRunUpdate: async run => {
            submit.disabled = false;
            if (isTerminal(run.status)) {
                setStatus(run.status === 'failed' ? (run.reason || runStatusText.failed()) : '', run.status === 'failed');
                if (chatSelection?.threadId === run.threadId) await loadHistory(chatSelection, false);
                await loadChats();
            } else {
                setStatus('');
            }
        },
    });

    const runs = createAssistantRuns({
        learningLanguageCode: panel.dataset.learningLanguageCode,
        onChanges: run => {
            for (const change of run.resourceChanges || []) {
                if (change.kind === 'delete_quiz' && change.entityId === quizId) void setQuizContext(null, 'GlobeGlotter');
            }
            window.dispatchEvent(new CustomEvent('assistant:changes', { detail: run }));
            void loadContextOptions();
        },
        request: async (url, options = {}) => {
            const response = await fetch(url, { ...options, headers: requestHeaders(options.method === 'POST' && !!options.body) });
            // Drain empty responses too, so the browser can finish the request normally.
            let data = null;
            if (response.status === 204) await response.text();
            else data = await response.json().catch(() => null);
            if (!response.ok) {
                const error = Object.assign(new Error(localizedProblem(data, response, 'Client.AssistantFailed', data?.detail || 'The assistant could not respond.')), { status: response.status });
                throw error;
            }
            return data;
        },
        isCurrent: threadId => activeThreadId === threadId,
        onUpdate: run => onUpdate(run),
    });

    const selectCreatedQuiz = async (createdQuiz: QuizOption) => {
        if (quizSelector) {
            let option = Array.from(quizSelector.options)
                .find(candidate => candidate.value.toLowerCase() === createdQuiz.id.toLowerCase());
            if (!option) {
                option = document.createElement('option');
                option.value = createdQuiz.id;
                quizSelector.appendChild(option);
            }
            option.dataset.contextLabel = createdQuiz.name;
            option.textContent = createdQuiz.sourceLanguage && createdQuiz.targetLanguage
                ? `${createdQuiz.name} (${createdQuiz.sourceLanguage} -> ${createdQuiz.targetLanguage})`
                : createdQuiz.name;
        }

        return setQuizContext(createdQuiz.id, createdQuiz.name, true);
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

    window.addEventListener('assistant:avatar-ended', event => {
        const id = event.detail?.conversationId;
        for (const [threadId, sourceId] of avatarContexts) {
            if (sourceId === id) avatarContexts.delete(threadId);
        }
        avatarLinks.persist();
        if (materialKind === 'avatar' && materialId === id) {
            setMaterialContext(null, null, false);
            setStatus('The live conversation has ended. Any text already shared is still available in this chat.');
        }
    });

    window.addEventListener('assistant:avatar', async event => {
        const id = event.detail?.conversationId;
        if (!id || avatarOpening) return;
        avatarOpening = true;
        try {
            await openAssistant();
            let threadId = avatarThreads.get(id);
            if (!threadId || !chats.some(chat => chat.id === threadId)) {
                const chat = await createChat({ contextQuizId: null, contextTranscriptId: null, contextBookDocumentId: null });
                threadId = chat.id;
                avatarThreads.set(id, threadId);
            }
            await selectChat(threadId);
            if (activeThreadId !== threadId) return;
            await setMaterialContext('avatar', id, false);
            await setQuizContext(null, 'GlobeGlotter', false);
            setStatus('Using your conversation so far. Shared text is retained in this assistant chat.');
            if (event.detail?.quiz) textarea.value = 'Make a quiz based on this Speaking conversation.';
            textarea.focus();
        } catch (error) { setStatus(errorMessage(error, 'Assistant request failed.') || 'Could not open the assistant.', true); }
        finally { avatarOpening = false; }
    });

    close?.addEventListener('click', closeAssistant);

    const startNewChat = async () => {
        if (!chatSelection?.contextReady) return;
        let selection: ChatSelection | null = chatSelection;
        try {
            const chat = await createChat();
            if (!ownsSelection(selection)) return;
            const loading = selectChat(chat.id);
            selection = chatSelection;
            await loading;
        } catch (err) {
            if (ownsSelection(selection)) setStatus(errorMessage(err, 'Assistant request failed.') || 'Could not create chat.', true);
        }
    };

    reset?.addEventListener('click', startNewChat);
    newChatButton?.addEventListener('click', startNewChat);

    quizSelector?.addEventListener('change', async () => {
        if (!chatSelection?.contextReady || contextOptionsBusy) return;
        const selection = chatSelection;
        const selectedOption = quizSelector.selectedOptions?.[0] || null;
        const label = selectedOption?.dataset.contextLabel || 'GlobeGlotter';
        const contextPersisted = await setQuizContext(quizSelector.value || null, label, true);
        if (contextPersisted && canConfirmContext(selection)) {
            setStatus(quizId ? `Quiz set to ${label}.` : 'No quiz selected.');
        }
    });

    materialSelector?.addEventListener('change', async () => {
        if (!chatSelection?.contextReady || contextOptionsBusy) return;
        const selection = chatSelection;
        const [kind, id] = (materialSelector.value || '').split(':');
        const contextPersisted = await setMaterialContext(kind || null, id || null, true);
        if (contextPersisted && canConfirmContext(selection)) {
            setStatus(materialId ? `Reading ${materialLabel()}.` : 'No material selected.');
        }
    });

    document.addEventListener('keydown', (event) => {
        if (!event.defaultPrevented
            && event.key === 'Escape'
            && !windowEl?.hidden
            && !document.querySelector<HTMLElement>('.modal-backdrop.open')) {
            closeAssistant();
        }
    });

    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        const message = textarea.value.trim();
        if (!message) return;
        const selection = chatSelection;
        if (!selection) return;
        const threadId = selection.threadId;
        await activeSelectionPromise;
        if (!ownsSelection(selection) || !selection.historyLoaded || pendingSends.has(threadId)) return;
        pendingSends.add(threadId);
        sendErrors.delete(threadId);

        const documentContext = currentBookPageContext({
            pageDocumentId: pageDocumentId,
            currentPage: panel.dataset.currentPage,
            selectedMaterialKind: materialKind,
            selectedMaterialId: materialId,
        });
        // The picker can move the chat to another transcript while the reader still shows
        // this one, and then the page on screen is not the selected material's page.
        const transcriptContext = materialKind === 'transcript'
            && materialId === pageTranscriptId
            && pageTranscriptPage > 0
            ? { page: pageTranscriptPage, stream: pageTranscriptStream }
            : null;

        renderMessage({
            id: `local-${Date.now()}`,
            role: 'user',
            text: message,
            pendingChanges: [],
            status: 'active',
        });
        textarea.value = '';
        submit.disabled = true;
        setStatus(t('Client.AssistantRunWorking', 'Working…'));
        const clientStartedAt = performance.now();

        try {
            const run = await runs.send(threadId, {
                message,
                contextQuizId: quizId,
                contextAnkiCollectionId: panel.dataset.ankiCollectionId || null,
                focusedWordId,
                transcriptId: materialKind === 'transcript' ? materialId : null,
                avatarConversationId: materialKind === 'avatar' ? materialId : null,
                bookDocumentId: materialKind === 'book' ? materialId : null,
                documentContext,
                transcriptContext,
            });
            if (run && ownsSelection(selection)) renderRun(run);
            const clientDurationMs = performance.now() - clientStartedAt;
            if (run?.turnId && validClientDuration(clientDurationMs)) {
                void api.json(clientMetricsUrl(run.turnId), {
                    method: 'PUT',
                    body: JSON.stringify({ clientDurationMs }),
                }).catch(() => { /* Timing is best-effort and never blocks the reply. */ });
            }
            void loadChats();
        } catch (err) {
            // fetch rejects with a TypeError worded by the browser ("Failed to fetch"). Show
            // the localized network message then; errors raised here keep their own text.
            let sendError = err instanceof TypeError || !errorMessage(err, '')
                ? t('Client.AssistantNetwork', 'Network error talking to the assistant.')
                : errorMessage(err, 'Assistant request failed.');
            if (ownsSelection(selection) && materialKind === 'avatar' && (errorStatus(err) === 404 || errorStatus(err) === 410)) {
                setMaterialContext(null, null, false);
                sendError = 'This conversation is no longer available. Select a saved transcript, or send your message without it.';
            }
            if (ownsSelection(selection) && !textarea.value) textarea.value = message;
            // The request may have reached the server before the connection dropped.
            void runs.discover(threadId).catch(() => { /* Reconnect when the user reopens the chat. */ });
            sendErrors.set(threadId, sendError);
            if (ownsSelection(selection)) {
                setStatus(sendError, true);
            }
        } finally {
            pendingSends.delete(threadId);
            if (ownsSelection(selection)) {
                submit.disabled = false;
                if (canFocusAssistant()) textarea.focus();
            } else if (activeThreadId === threadId) {
                // The user returned while this send was pending. Reload the stored
                // conversation instead of appending to a newer history snapshot.
                await selectChat(threadId);
            }
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
        setScanStatus(t('Client.Loading', 'Loading…'));
        try {
            const response = await fetch(imageInput.dataset.extractUrl || '/Quiz/ExtractTextFromImage', {
                method: 'POST',
                headers: requestHeaders(),
                body,
            });
            const data = await response.json().catch(() => null);
            if (!response.ok || !data?.text) {
                setScanStatus(localizedProblem(data, response, 'Client.PictureFailed', 'Could not read text from that picture.'), true);
                return;
            }

            const prompt = `Add useful vocabulary from this text:\n\n${data.text.trim()}`;
            textarea.value = textarea.value.trim()
                ? `${textarea.value.trim()}\n\n${prompt}`
                : prompt;
            textarea.focus();
            setScanStatus(t('Client.Correct', 'Done'));
        } catch {
            setScanStatus(t('Client.PictureNetwork', 'Network error reading picture.'), true);
        } finally {
            imageInput.value = '';
            imageInput.disabled = false;
        }
    });
})();
