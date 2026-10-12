// Refetch server-rendered content after committed assistant changes; keep the chat shell alive.
export function createLearningUpdates({ refresh, accept = () => true, schedule = (fn, ms) => setTimeout(fn, ms), cancel = clearTimeout }) {
    const revisions = new Map();
    let timer;
    let generation = 0;
    return {
        receive(run) {
            if (!run.changeRevision || revisions.get(run.id) === run.changeRevision) return;
            revisions.set(run.id, run.changeRevision);
            if (!accept(run)) return;
            const current = ++generation;
            cancel(timer);
            timer = schedule(() => refresh(() => current === generation), 180);
        },
    };
}

// Unknown/legacy change payloads remain conservative. Known unrelated resources do not
// interrupt a study session; source edits still invalidate Anki cards from that quiz.
export function affectsStudy(run, { studyQuizId, studyAnkiCollectionId, studyCardId } = {}) {
    if (!Array.isArray(run.resourceChanges) || !run.resourceChanges.length) return true;
    if (!studyQuizId && !studyAnkiCollectionId) return true;
    const metadataOnly = new Set(['rename_quiz', 'rename_collection', 'move_quiz', 'move_collection',
        'quiz_visibility', 'collection_visibility', 'anki_rename', 'rename_anki_collection',
        'create_quiz', 'create_collection', 'copy_quiz', 'copy_collection', 'create_anki_collection', 'anki_create']);
    return run.resourceChanges.some(change => {
        if (metadataOnly.has(change.kind)) return false;
        if (['word', 'sentence', 'quiz'].includes(change.entityType)) {
            const quizId = change.quizId || (change.entityType === 'quiz' ? change.entityId : null);
            return !quizId || !studyQuizId || quizId === studyQuizId;
        }
        if (['anki', 'anki_collection'].includes(change.entityType))
            return !!studyAnkiCollectionId && change.entityId === studyAnkiCollectionId;
        if (change.entityType === 'anki_card')
            return !!studyAnkiCollectionId && (!studyCardId || change.entityId === studyCardId);
        if (change.entityType === 'anki_quiz_link') return !!studyAnkiCollectionId;
        // A deleted folder can contain the current quiz, including a nested folder.
        return true;
    });
}

// Deleted quiz/folder detail routes redirect to their own library. Treat only
// that exact same-origin destination as removal; login or unrelated redirects
// must never replace learning content with another page's response.
export function isRemovedItemRedirect(response, requestUrl) {
    if (!response.ok || !response.redirected) return false;
    try {
        const original = new URL(requestUrl), destination = new URL(response.url);
        const resource = original.pathname.match(/^\/(quiz|anki)\/(?:details|collection|settings)(?:\/|$)/i)?.[1];
        return !!resource && destination.origin === original.origin
            && new RegExp(`^/${resource}(?:/index)?/?$`, 'i').test(destination.pathname)
            && !destination.search && !destination.hash;
    } catch { return false; }
}

if (typeof window !== 'undefined' && typeof document !== 'undefined') {
    const channel = typeof BroadcastChannel === 'function' ? new BroadcastChannel('glosify-learning-changes') : null;
    window.addEventListener('assistant:changes', event => channel?.postMessage({ id: event.detail.id, changeRevision: event.detail.changeRevision, resourceChanges: event.detail.resourceChanges }));
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
        const coordinator = createLearningUpdates({ refresh, accept: run => {
            if (!screen.querySelector('[data-flashcard-session], [data-typing-quiz]')) return true;
            const study = screen.querySelector('[data-study-quiz-id], [data-study-anki-collection-id]');
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
