import { text as t } from '../shared/localization.js';
import { errorMessage } from '../shared/errors.js';
import type { AssistantRun, RunPlanItem } from './run-types.js';
import type { createAssistantRuns } from './runs.js';

export const runStatusText: Record<string, () => string> = {
        queued: () => t('Client.AssistantRunQueued', 'Queued'),
        running: () => t('Client.AssistantRunWorking', 'Working…'),
        retry_wait: () => t('Client.AssistantRunRetrying', 'Retrying soon'),
        awaiting_input: () => t('Client.AssistantRunAwaitingInput', 'Waiting for your answer'),
        awaiting_approval: () => t('Client.AssistantRunAwaitingApproval', 'Waiting for your approval'),
        paused: () => t('Client.AssistantRunPaused', 'Paused'),
        cancelled: () => t('Client.AssistantRunStopped', 'Stopped'),
        failed: () => t('Client.AssistantRunFailed', 'Could not finish'),
    };

interface RunControlOptions {
    getRuns(): ReturnType<typeof createAssistantRuns>;
    setStatus(message: string, isError?: boolean): void;
    afterUndo(): Promise<unknown>;
}
export function createRunControls({ getRuns, setStatus, afterUndo }: RunControlOptions) {
    const button = (label: string, className: string, onClick: () => unknown, disableOnClick = true) => {
        const element = document.createElement('button');
        element.type = 'button';
        element.className = className;
        element.textContent = label;
        element.addEventListener('click', async () => {
            if (disableOnClick) element.disabled = true;
            try {
                await onClick();
            } catch (error) {
                setStatus(errorMessage(error, t('Client.GenericError', 'Something went wrong. Please try again.')), true);
                element.disabled = false;
            }
        });
        return element;
    };

    const renderUndo = (runId: string, undone?: boolean) => {
        const bar = document.createElement('div');
        bar.className = 'assistant-undo';
        if (undone) {
            const tag = document.createElement('span');
            tag.className = 'assistant-pending-tag muted';
            tag.textContent = t('Client.AssistantUndone', 'Changes undone');
            bar.appendChild(tag);
            return bar;
        }
        bar.appendChild(button(t('Client.AssistantUndo', 'Undo changes'), 'btn-secondary', async () => {
            if (!window.confirm(t('Client.AssistantUndoConfirm', 'Undo every change from this reply?'))) return;
            const result = await getRuns().undo(runId);
            setStatus(result?.kept
                ? t('Client.AssistantUndoKept', '{0} undone · {1} kept because you changed them', result.undone, result.kept)
                : t('Client.AssistantUndone', 'Changes undone'));
            await afterUndo();
        }));
        return bar;
    };

    const renderPlan = (plan: RunPlanItem[]) => {
        const section = document.createElement('div');
        section.className = 'assistant-plan';
        const heading = document.createElement('div');
        heading.className = 'assistant-plan-heading';
        heading.textContent = t('Client.AssistantPlan', 'Plan');
        const list = document.createElement('ol');
        for (const item of plan) {
            const entry = document.createElement('li');
            entry.className = `is-${item.status}`;
            entry.dir = 'auto';
            entry.textContent = item.text;
            list.appendChild(entry);
        }
        section.append(heading, list);
        return section;
    };

    const renderQuestion = (run: AssistantRun) => {
        const card = document.createElement('div');
        card.className = 'assistant-question';
        const questionData = run.question;
        if (!questionData) return card;
        let submitting = false;
        const answer = async (values: string[]) => {
            if (submitting) return;
            submitting = true;
            const controls = card.querySelectorAll('button');
            controls.forEach(control => { control.disabled = true; });
            try {
                await getRuns().answer(run.threadId, values);
            } catch (error) {
                submitting = false;
                controls.forEach(control => { control.disabled = false; });
                throw error;
            }
        };
        const question = document.createElement('p');
        question.dir = 'auto';
        question.textContent = questionData.question;
        card.appendChild(question);
        if (questionData.options.length) {
            const options = document.createElement('div');
            options.className = 'assistant-question-options';
            const chosen = new Set<string>();
            for (const option of questionData.options) {
                const choice = button(option, 'btn-secondary', async () => {
                    if (!questionData.multiple) {
                        await answer([option]);
                        return;
                    }
                    if (chosen.has(option)) chosen.delete(option);
                    else chosen.add(option);
                    choice.setAttribute('aria-pressed', String(chosen.has(option)));
                }, !questionData.multiple);
                if (questionData.multiple) choice.setAttribute('aria-pressed', 'false');
                options.appendChild(choice);
            }
            card.appendChild(options);
            if (questionData.multiple) {
                card.appendChild(button(t('Client.AssistantAnswer', 'Answer'), 'btn-submit', () => answer([...chosen])));
            }
        }
        const hint = document.createElement('small');
        hint.textContent = t('Client.AssistantAnswerPlaceholder', 'Or type your own answer');
        card.appendChild(hint);
        return card;
    };

    const renderApproval = (run: AssistantRun) => {
        const card = document.createElement('div');
        card.className = 'assistant-pending-card';
        const heading = document.createElement('div');
        heading.className = 'assistant-pending-heading';
        heading.textContent = t('Client.AssistantApprovalTitle', 'Approve these changes?');
        const list = document.createElement('ul');
        list.className = 'assistant-pending-list';
        for (const change of run.approval?.changes || []) {
            const item = document.createElement('li');
            item.dir = 'auto';
            item.textContent = change.summary;
            list.appendChild(item);
        }
        const reason = document.createElement('input');
        reason.type = 'text';
        reason.className = 'form-input';
        reason.maxLength = 500;
        reason.placeholder = t('Client.AssistantDeclineReason', 'Why not? (optional)');
        const actions = document.createElement('div');
        actions.className = 'assistant-pending-actions';
        actions.append(
            button(t('Client.AssistantApprove', 'Approve'), 'btn-submit', () => getRuns().approve(run.threadId)),
            button(t('Client.AssistantApproveAlways', 'Always allow in this chat'), 'btn-secondary', () => getRuns().approve(run.threadId, true)),
            button(t('Client.AssistantDecline', 'Decline'), 'btn-secondary', () => getRuns().reject(run.threadId, reason.value.trim())));
        card.append(heading, list, reason, actions);
        return card;
    };

    return { button, renderUndo, renderPlan, renderQuestion, renderApproval };
}
