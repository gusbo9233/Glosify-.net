import { matchesOwnedStatus } from './context-options.js';
import { errorMessage } from '../shared/errors.js';
import { text as t } from '../shared/localization.js';

interface ShellOptions {
    panel: HTMLElement;
    windowEl: HTMLElement | null;
    toggle: HTMLElement | null;
    textarea: HTMLTextAreaElement;
    status: HTMLElement;
    ensureInitialChat(): Promise<unknown>;
    ensureContextOptions(): Promise<unknown>;
    setStatus(message: string, isError?: boolean): void;
}

export function createAssistantShell({ panel, windowEl, toggle, textarea, status, ensureInitialChat, ensureContextOptions, setStatus }: ShellOptions) {
    let visibleLoadError: string | null = null;
    const canFocus = () => !windowEl?.hidden
        && !document.querySelector('.modal-backdrop.open')
        && (document.activeElement === document.body || panel.contains(document.activeElement));

    const open = async () => {
        if (!windowEl || !toggle) return;
        window.dispatchEvent(new CustomEvent('assistant:opened'));
        windowEl.hidden = false;
        panel.classList.add('is-open');
        toggle.setAttribute('aria-expanded', 'true');
        const [chatResult, contextResult] = await Promise.allSettled([ensureInitialChat(), ensureContextOptions()]);
        if (chatResult.status === 'rejected') {
            visibleLoadError = errorMessage(chatResult.reason, 'Could not load the assistant.');
            setStatus(visibleLoadError, true);
        } else if (contextResult.status === 'rejected') {
            visibleLoadError = errorMessage(contextResult.reason, t('Client.GenericError', 'Something went wrong. Please try again.'));
            setStatus(visibleLoadError, true);
        } else {
            if (matchesOwnedStatus(status.textContent || '', visibleLoadError)) setStatus('');
            visibleLoadError = null;
        }
        window.requestAnimationFrame(() => { if (canFocus()) textarea.focus(); });
    };
    const close = () => {
        if (!windowEl || !toggle) return;
        windowEl.hidden = true;
        panel.classList.remove('is-open');
        toggle.setAttribute('aria-expanded', 'false');
        toggle.focus();
    };
    return { open, close, canFocus };
}
