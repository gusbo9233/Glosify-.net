import { escapeHtml, quizLink } from './presentation.js';

import type { MessagePart } from './run-types.js';

export const bubble = (text: string) => {
    const body = document.createElement('div');
    body.className = 'assistant-bubble';
    body.dir = 'auto';
    body.innerHTML = escapeHtml(text).replace(/\n/g, '<br />');
    return body;
};

const toolIcons: Record<string, string> = {
    completed: 'check_circle',
    error: 'error',
    rejected: 'block',
    superseded: 'remove_circle',
    awaiting_approval: 'pending',
    awaiting_input: 'help',
    approved: 'pending',
    pending: 'progress_activity',
};

// A reply is its parts in order: text, and runs of tool activity shown as one list each.
export const renderParts = (container: HTMLElement, parts: MessagePart[] | null | undefined) => {
    let activity: HTMLUListElement | null = null;
    for (const part of parts || []) {
        if (part.type === 'quiz_link') {
            activity = null;
            const link = quizLink(document, part);
            if (link) container.appendChild(link);
            continue;
        }
        if (part.type === 'text') {
            activity = null;
            if (part.text) container.appendChild(bubble(part.text));
            continue;
        }
        if (part.type !== 'tool') continue;
        if (!activity) {
            activity = document.createElement('ul');
            activity.className = 'assistant-activity';
            container.appendChild(activity);
        }
        const item = document.createElement('li');
        item.className = `assistant-activity-item is-${part.state || 'pending'}`;
        item.dir = 'auto';
        const icon = document.createElement('span');
        icon.className = 'material-symbols-outlined';
        icon.setAttribute('aria-hidden', 'true');
        icon.textContent = toolIcons[part.state || 'pending'] || 'progress_activity';
        const label = document.createElement('span');
        label.textContent = part.title || part.tool || '';
        item.append(icon, label);
        activity.appendChild(item);
    }
};
