import { createLatestRequestGate } from './feedback.js';
import type { Feedback } from './feedback.js';
import { bubble, renderParts } from './message-parts.js';
import { createRunControls, runStatusText } from './run-controls.js';
import { isTerminal, isWaiting, type createAssistantRuns } from './runs.js';
import type { AssistantRun, MessagePart, PendingChange } from './run-types.js';
import { text as t } from '../shared/localization.js';

export interface ChatSelection { threadId: string; historyLoaded?: boolean; contextReady?: boolean; }
export interface AssistantMessage {
    id: string; role: string; text?: string | null; turnId?: string | null; runId?: string | null;
    parts?: MessagePart[]; pendingChanges?: PendingChange[]; status?: string;
    canUndo?: boolean; undone?: boolean; canRate?: boolean; feedback?: Feedback | null;
}
interface TranscriptOptions {
    transcript: HTMLElement;
    empty: HTMLElement | null;
    defaultEmptyText: string;
    getTitle(): string;
    getSelection(): ChatSelection | null;
    getRuns(): ReturnType<typeof createAssistantRuns>;
    renderPendingChanges(message: AssistantMessage): HTMLElement;
    renderFeedback(turnId: string, feedback?: Feedback | null): HTMLElement;
    setStatus(message: string, isError?: boolean): void;
    onRunUpdate(run: AssistantRun): unknown;
}
export function createTranscriptView({ transcript, empty, defaultEmptyText, getTitle, getSelection, getRuns, renderPendingChanges, renderFeedback, setStatus, onRunUpdate }: TranscriptOptions) {
    const historyGate = createLatestRequestGate();
    const { button, renderUndo, renderPlan, renderQuestion, renderApproval } = createRunControls({
        getRuns, setStatus, afterUndo: async () => { const selection = getSelection(); if (selection) await loadHistory(selection); },
    });
    const renderEmptyState = (message?: string) => {
        const node = document.createElement('div');
        node.className = 'assistant-empty';
        node.dataset.assistantEmpty = '';

        const icon = document.createElement('span');
        icon.className = 'material-symbols-outlined';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = 'auto_awesome';

        const title = document.createElement('strong');
        title.textContent = getTitle();

        const copy = document.createElement('span');
        copy.textContent = message || defaultEmptyText;

        node.append(icon, title, copy);
        return node;
    };

    const resetTranscript = (message?: string) => {
        historyGate.next();
        if (!transcript) {
            return;
        }
        transcript.innerHTML = '';
        empty = renderEmptyState(message);
        transcript.appendChild(empty);
    };

    const renderMessage = (message: AssistantMessage) => {
        const hasPendingChanges = Array.isArray(message.pendingChanges) && message.pendingChanges.length > 0;
        const hasParts = Array.isArray(message.parts) && message.parts.length > 0;
        if (!message.text && !hasPendingChanges && !hasParts) {
            return;
        }
        historyGate.next();

        if (empty) empty.remove();

        const row = document.createElement('article');
        row.className = `assistant-message assistant-message-${message.role}`;
        row.dataset.messageId = message.id;
        if (message.turnId) row.dataset.turnId = message.turnId;
        if (message.runId) row.dataset.runId = message.runId;

        if (hasParts) {
            renderParts(row, message.undone ? message.parts!.filter(part => part.type !== 'quiz_link') : message.parts);
        } else if (message.text) {
            row.appendChild(bubble(message.text));
        }

        if (message.canUndo || message.undone) {
            row.appendChild(renderUndo(message.runId!, message.undone));
        }

        if (hasPendingChanges) {
            const card = renderPendingChanges(message);
            row.appendChild(card);
        }

        if (message.role === 'model' && message.turnId && message.canRate === true) {
            row.appendChild(renderFeedback(message.turnId, message.feedback));
        }

        transcript.appendChild(row);
        transcript.scrollTop = transcript.scrollHeight;
    };

    const renderQuizCreatedMessage = (createdQuizId: string) => {
        if (!createdQuizId || !transcript) {
            return;
        }
        historyGate.next();

        if (empty) empty.remove();

        const row = document.createElement('article');
        row.className = 'assistant-message assistant-message-model';

        const body = document.createElement('div');
        body.className = 'assistant-bubble';
        body.appendChild(document.createTextNode('Quiz created. '));

        const link = document.createElement('a');
        link.href = `/Quizzes/Details/${encodeURIComponent(createdQuizId)}`;
        link.textContent = 'Open quiz';
        body.appendChild(link);

        row.appendChild(body);
        transcript.appendChild(row);
        transcript.scrollTop = transcript.scrollHeight;
    };

    // One card per run, rebuilt from each update: the server's view is the whole truth.
    const renderRun = (run: AssistantRun) => {
        if (!transcript) return;
        let row = transcript.querySelector<HTMLElement>(`[data-live-run="${run.id}"]`);
        // send/steer may publish a terminal response before returning it to the
        // submit handler. Its duplicate render must not invalidate history reload.
        if (!row || row.dataset.runRevision !== String(run.revision) || row.dataset.runStatus !== run.status) historyGate.next();
        if (!row) {
            // A reply already in the history for this run is replaced by the live card.
            transcript.querySelectorAll(`.assistant-message-model[data-run-id="${run.id}"]`).forEach(node => node.remove());
            if (empty) empty.remove();
            row = document.createElement('article');
            row.className = 'assistant-message assistant-message-model assistant-run';
            row.dataset.liveRun = run.id;
            transcript.appendChild(row);
        }
        row.dataset.runRevision = String(run.revision);
        row.dataset.runStatus = run.status;
        row.replaceChildren();
        renderParts(row, run.undone ? run.parts.filter(part => part.type !== 'quiz_link') : run.parts);
        if (run.plan?.length) row.appendChild(renderPlan(run.plan));
        if (run.status === 'awaiting_input' && run.question) row.appendChild(renderQuestion(run));
        if (run.status === 'awaiting_approval' && run.approval) row.appendChild(renderApproval(run));

        const footer = document.createElement('div');
        footer.className = 'assistant-run-status';
        const label = runStatusText[run.status]?.();
        if (label) {
            const text = document.createElement('span');
            text.textContent = run.reason && (isWaiting(run.status) || run.status === 'failed') ? `${label} · ${run.reason}` : label;
            footer.appendChild(text);
        }
        if (run.savedChanges > 0) {
            const saved = document.createElement('span');
            saved.className = 'assistant-run-saved';
            saved.textContent = t('Client.AssistantSaved', '{0} saved', run.savedChanges);
            if (run.permanentChanges > 0) saved.textContent += ` · ${run.permanentChanges} permanent (not included in Undo)`;
            footer.appendChild(saved);
        }
        if (!isTerminal(run.status)) {
            footer.appendChild(button(t('Client.Stop', 'Stop'), 'btn-secondary', () => getRuns().stop(run.threadId)));
        }
        if (run.status === 'paused') {
            footer.appendChild(button(t('Client.AssistantResume', 'Resume'), 'btn-submit', () => getRuns().resume(run.threadId)));
        }
        if (footer.childElementCount) row.appendChild(footer);
        transcript.scrollTop = transcript.scrollHeight;
    };

    const loadHistory = async (selection: ChatSelection, discover = true) => {
        if (getSelection() !== selection) return false;
        const requestVersion = historyGate.next();
        const { threadId } = selection;
        try {
            const response = await fetch(`/Assistant/Chats/${threadId}/History`, {
                headers: { 'Accept': 'application/json' },
            });
            if (!response.ok) return false;
            const data: { messages?: AssistantMessage[] } | null = await response.json();
            if (getSelection() !== selection || !historyGate.isCurrent(requestVersion) || !Array.isArray(data?.messages)) return false;
            resetTranscript(defaultEmptyText);
            for (const message of data.messages ?? []) {
                renderMessage(message);
            }
            if (discover) {
                const discoveryVersion = historyGate.next();
                void getRuns().discover(threadId)
                    .then(run => { if (run && !isTerminal(run.status) && getSelection() === selection && historyGate.isCurrent(discoveryVersion)) renderRun(run); })
                    .catch(() => { /* The next open reconnects to the run. */ });
            }
            return true;
        } catch {
            return false;
        }
    };

    const onUpdate = async (run: AssistantRun) => {
        if (!getSelection()?.historyLoaded) return;
        renderRun(run);
        await onRunUpdate(run);
    };
    return { resetTranscript, renderMessage, renderRun, renderQuizCreatedMessage, loadHistory, onUpdate };
}
