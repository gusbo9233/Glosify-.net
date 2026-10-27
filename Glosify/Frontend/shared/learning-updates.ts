import { createLearningUpdates, affectsStudy, isRemovedItemRedirect, type LearningChange } from './learning-changes.js';

declare global {
    interface WindowEventMap {
        'assistant:changes': CustomEvent<LearningChange>;
        'learning:updated': CustomEvent<void>;
    }
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') {
    const channel = typeof BroadcastChannel === 'function' ? new BroadcastChannel('glosify-learning-changes') : null;
    window.addEventListener('assistant:changes', event => channel?.postMessage({ id: event.detail.id, changeRevision: event.detail.changeRevision, resourceChanges: event.detail.resourceChanges }));
    const screen = document.querySelector<HTMLElement>('[data-learning-screen]');
    if (screen && ['quiz', 'anki', 'flashcardquiz', 'typingquiz'].includes(screen.dataset.learningController || '')) {
        let dirty = false;
        let pending = false;
        let requestVersion = 0;
        const notice = document.createElement('p');
        notice.className = 'page-message';
        notice.setAttribute('role', 'status');
        notice.hidden = true;
        screen.before(notice);
        const message = (text: string, retry: boolean) => {
            notice.replaceChildren(document.createTextNode(text + ' '));
            if (retry) {
                const button = document.createElement('button');
                button.type = 'button'; button.className = 'btn-secondary'; button.textContent = 'Update view';
                button.addEventListener('click', () => { dirty = false; void refresh(() => true); });
                notice.append(button);
            }
            notice.hidden = false;
        };
        screen.addEventListener('input', event => {
            const target = event.target as Element | null;
            if (target?.closest('form') && !target.matches('[type="search"], [data-anki-item-search]')) dirty = true;
        });
        screen.addEventListener('change', event => {
            const target = event.target as Element | null;
            if (target?.closest('form')) dirty = true;
        });
        const refresh = async (isCurrent: () => boolean) => {
            pending = true;
            if (dirty || screen.querySelector('dialog[open]')) {
                message('Saved changes are available. Finish editing, or update this view to discard its unsaved edits.', true);
                return;
            }
            if (screen.querySelector('[data-flashcard-session], [data-typing-quiz]')) {
                // Avoid swapping the question under a learner's finger.
                message('Study content changed. Open the quiz or collection again before continuing.', false);
                screen.querySelectorAll<HTMLButtonElement>('button[type="submit"]').forEach(button => { button.disabled = true; });
                return;
            }
            const version = ++requestVersion;
            const url = location.href;
            const scroll = { x: window.scrollX, y: window.scrollY };
            const search = [...screen.querySelectorAll<HTMLInputElement>('input[type="search"], [data-anki-item-search]')].map(e => ({ id: e.id, name: e.name, value: e.value }));
            try {
                const response = await fetch(url, { headers: { 'X-Learning-Refresh': 'true' }, cache: 'no-store' });
                const html = response.ok ? await response.text() : '';
                if (!isCurrent() || version !== requestVersion || location.href !== url) return;
                if (dirty || screen.querySelector('dialog[open]')) { message('Saved changes are available. Finish editing before updating this view.', true); return; }
                if (response.status === 404 || isRemovedItemRedirect(response, url)) {
                    screen.replaceChildren();
                    const text = document.createElement('p'); text.textContent = 'This item was removed.';
                    const link = document.createElement('a'); link.href = /^\/anki/i.test(location.pathname) ? '/Anki' : '/Quiz'; link.textContent = 'Back to library';
                    screen.append(text, link);
                } else {
                    if (!response.ok || response.redirected) throw new Error('Refresh unavailable');
                    const parsed = new DOMParser().parseFromString(html, 'text/html');
                    const next = parsed.querySelector('[data-learning-screen]');
                    if (!next) throw new Error('Missing content');
                    next.querySelectorAll('script').forEach(script => script.remove());
                    screen.replaceChildren(...next.childNodes);
                    window.dispatchEvent(new CustomEvent('learning:updated'));
                    for (const saved of search) {
                        const input = [...screen.querySelectorAll<HTMLInputElement>('input[type="search"], [data-anki-item-search]')].find(e => e.id === saved.id && e.name === saved.name);
                        if (input) { input.value = saved.value; input.dispatchEvent(new Event('input', { bubbles: true })); }
                    }
                }
                pending = false; notice.hidden = true;
                window.scrollTo(scroll.x, scroll.y);
            } catch {
                if (version === requestVersion) message('Your changes were saved, but this view could not update.', true);
            }
        };
        const coordinator = createLearningUpdates({ refresh, accept: run => {
            if (!screen.querySelector('[data-flashcard-session], [data-typing-quiz]')) return true;
            const study = screen.querySelector<HTMLElement>('[data-study-quiz-id], [data-study-anki-collection-id]');
            return affectsStudy(run, study?.dataset);
        } });
        window.addEventListener('assistant:changes', event => {
            const change = { id: event.detail.id, changeRevision: event.detail.changeRevision, resourceChanges: event.detail.resourceChanges };
            coordinator.receive(change);
        });
        if (channel) channel.onmessage = event => {
            if (typeof event.data?.id === 'string' && typeof event.data?.changeRevision === 'string') coordinator.receive(event.data);
        };
        window.addEventListener('focus', () => { if (pending) void refresh(() => true); });
    }
}
