// Refetch server-rendered content after committed assistant changes; keep the chat shell alive.
export function createLearningUpdates({ refresh, schedule = (fn, ms) => setTimeout(fn, ms), cancel = clearTimeout }) {
    const revisions = new Map();
    let timer;
    let generation = 0;
    return {
        receive(run) {
            if (!run.changeRevision || revisions.get(run.id) === run.changeRevision) return;
            revisions.set(run.id, run.changeRevision);
            const current = ++generation;
            cancel(timer);
            timer = schedule(() => refresh(() => current === generation), 180);
        },
    };
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') {
    const channel = typeof BroadcastChannel === 'function' ? new BroadcastChannel('glosify-learning-changes') : null;
    window.addEventListener('assistant:changes', event => channel?.postMessage({ id: event.detail.id, changeRevision: event.detail.changeRevision }));
    const screen = document.querySelector('[data-learning-screen]');
    if (screen && ['quiz', 'anki', 'flashcardquiz', 'typingquiz'].includes(screen.dataset.learningController)) {
        let dirty = false;
        let pending = false;
        let requestVersion = 0;
        const notice = document.createElement('p');
        notice.className = 'page-message';
        notice.setAttribute('role', 'status');
        notice.hidden = true;
        screen.before(notice);
        const message = (text, retry) => {
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
            if (event.target.closest('form') && !event.target.matches('[type="search"], [data-anki-item-search]')) dirty = true;
        });
        screen.addEventListener('change', event => {
            if (event.target.closest('form')) dirty = true;
        });
        const refresh = async isCurrent => {
            pending = true;
            if (dirty || screen.querySelector('dialog[open]')) {
                message('Saved changes are available. Finish editing, or update this view to discard its unsaved edits.', true);
                return;
            }
            if (screen.querySelector('[data-flashcard-session], [data-typing-quiz]')) {
                // Avoid swapping the question under a learner's finger.
                message('Study content changed. Open the quiz or collection again before continuing.', false);
                screen.querySelectorAll('button[type="submit"]').forEach(button => { button.disabled = true; });
                return;
            }
            const version = ++requestVersion;
            const url = location.href;
            const scroll = { x: window.scrollX, y: window.scrollY };
            const search = [...screen.querySelectorAll('input[type="search"], [data-anki-item-search]')].map(e => ({ id: e.id, name: e.name, value: e.value }));
            try {
                const response = await fetch(url, { headers: { 'X-Learning-Refresh': 'true' }, cache: 'no-store' });
                const html = response.ok ? await response.text() : '';
                if (!isCurrent() || version !== requestVersion || location.href !== url) return;
                if (dirty || screen.querySelector('dialog[open]')) { message('Saved changes are available. Finish editing before updating this view.', true); return; }
                if (response.status === 404) {
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
                        const input = [...screen.querySelectorAll('input[type="search"], [data-anki-item-search]')].find(e => e.id === saved.id && e.name === saved.name);
                        if (input) { input.value = saved.value; input.dispatchEvent(new Event('input', { bubbles: true })); }
                    }
                }
                pending = false; notice.hidden = true;
                window.scrollTo(scroll.x, scroll.y);
            } catch {
                if (version === requestVersion) message('Your changes were saved, but this view could not update.', true);
            }
        };
        const coordinator = createLearningUpdates({ refresh });
        window.addEventListener('assistant:changes', event => {
            const change = { id: event.detail.id, changeRevision: event.detail.changeRevision };
            coordinator.receive(change);
        });
        if (channel) channel.onmessage = event => {
            if (typeof event.data?.id === 'string' && typeof event.data?.changeRevision === 'string') coordinator.receive(event.data);
        };
        window.addEventListener('focus', () => { if (pending) void refresh(() => true); });
    }
}
