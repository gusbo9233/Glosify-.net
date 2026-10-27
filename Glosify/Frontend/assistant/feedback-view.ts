import { createLatestRequestGate, feedbackFormValues, feedbackPanelState, feedbackReasons, normalizeFeedback, type Feedback } from './feedback.js';
import { createRecoverablePromiseQueue } from './state.js';

interface FeedbackActions {
    put: (feedback: Feedback) => Promise<Feedback | null>;
    remove: () => Promise<unknown>;
    onError: (error: unknown) => void;
}

export const createFeedbackView = (initialFeedback: Feedback | null | undefined, actions: FeedbackActions) => {
    const container = document.createElement('div');
    container.className = 'assistant-feedback';
    let current: Feedback | null = normalizeFeedback(initialFeedback);
    // Set once the user saves details, so the form closes and stays closed until they
    // change their mind rather than reopening on every re-render.
    let acknowledged = false;
    // The reasons and comment the user last tried to send. Held until the server accepts
    // them, so a failed save leaves their words in the form rather than reverting the
    // fields along with the rating.
    let draft: Pick<Feedback, 'reasonCodes' | 'comment'> | null = null;
    const requests = createLatestRequestGate();
    const writeFeedback = createRecoverablePromiseQueue((operation: () => Promise<unknown>) => operation());

    const prompt = document.createElement('span');
    prompt.className = 'assistant-feedback-prompt';
    prompt.textContent = 'Was this helpful?';

    const voteActions = document.createElement('div');
    voteActions.className = 'assistant-feedback-votes';
    const details = document.createElement('div');
    details.className = 'assistant-feedback-details';
    details.hidden = !current;

    const save = async (next: Feedback) => {
        const request = requests.next();
        const previous = current;
        current = next;
        sync();
        try {
            const saved = await writeFeedback(() => actions.put(next));
            if (!requests.isCurrent(request)) return false;
            current = normalizeFeedback(saved as Feedback | null);
            sync();
            return true;
        } catch (error) {
            if (!requests.isCurrent(request)) return false;
            current = previous;
            acknowledged = false;
            sync();
            actions.onError(error);
            return false;
        }
    };

    const makeVote = ({ rating, icon, label }: { rating: Feedback['rating']; icon: string; label: string }) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'assistant-feedback-vote';
        button.title = label;
        button.setAttribute('aria-label', label);
        const iconElement = document.createElement('span');
        iconElement.className = 'material-symbols-outlined';
        iconElement.setAttribute('aria-hidden', 'true');
        iconElement.textContent = icon;
        button.appendChild(iconElement);
        button.addEventListener('click', () => {
            const sameRating = current?.rating === rating;
            // Reopens the form: pressing a vote again is how the user gets back to the
            // reasons and comment after thanking them for the last one. A new rating has a
            // different reason set, so an unsent draft from the old one is discarded.
            acknowledged = false;
            draft = null;
            void save({
                rating,
                reasonCodes: sameRating ? current!.reasonCodes : [],
                comment: sameRating ? current!.comment : null,
            });
        });
        voteActions.appendChild(button);
        return button;
    };

    const up = makeVote({ rating: 'up', icon: 'thumb_up', label: 'Helpful' });
    const down = makeVote({ rating: 'down', icon: 'thumb_down', label: 'Not helpful' });

    const reasonList = document.createElement('div');
    reasonList.className = 'assistant-feedback-reasons';
    const comment = document.createElement('textarea');
    comment.className = 'assistant-feedback-comment';
    comment.maxLength = 1000;
    comment.rows = 2;
    comment.placeholder = 'Optional details';
    const detailActions = document.createElement('div');
    detailActions.className = 'assistant-feedback-detail-actions';
    const saveDetails = document.createElement('button');
    saveDetails.type = 'button';
    saveDetails.className = 'btn-secondary';
    saveDetails.textContent = 'Save details';
    saveDetails.addEventListener('click', async () => {
        if (!current) return;
        const reasonCodes = Array.from(reasonList.querySelectorAll<HTMLInputElement>('input:checked'))
            .map(input => input.value);
        saveDetails.disabled = true;
        // Held across the request so a rejected save re-renders the user's words rather
        // than the last thing the server accepted.
        draft = { reasonCodes, comment: comment.value.trim() || null };
        try {
            // Only close on a save the server accepted. A failure leaves the form open with
            // the user's words still in it, next to the error, so nothing has to be retyped.
            if (await save({ rating: current.rating, ...draft })) {
                draft = null;
                acknowledged = true;
                sync();
            }
        } finally {
            saveDetails.disabled = false;
        }
    });
    const clear = document.createElement('button');
    clear.type = 'button';
    clear.className = 'assistant-feedback-clear';
    clear.textContent = 'Clear';
    clear.addEventListener('click', async () => {
        const request = requests.next();
        const previous = current;
        current = null;
        acknowledged = false;
        draft = null;
        sync();
        try {
            await writeFeedback(actions.remove);
        } catch (error) {
            if (!requests.isCurrent(request)) return;
            current = previous;
            sync();
            actions.onError(error);
        }
    });
    detailActions.append(saveDetails, clear);
    details.append(reasonList, comment, detailActions);

    const note = document.createElement('span');
    note.className = 'assistant-feedback-note';
    // A live region: the form vanishing is the visual confirmation, and this is the
    // spoken one for anyone who cannot see it happen.
    note.setAttribute('role', 'status');
    note.textContent = 'Thanks — your feedback was saved.';
    note.hidden = true;

    const sync = () => {
        const upSelected = current?.rating === 'up';
        const downSelected = current?.rating === 'down';
        up.classList.toggle('is-selected', upSelected);
        down.classList.toggle('is-selected', downSelected);
        up.setAttribute('aria-pressed', String(upSelected));
        down.setAttribute('aria-pressed', String(downSelected));
        const panel = feedbackPanelState(current, acknowledged);
        details.hidden = !panel.showDetails;
        note.hidden = !panel.showThanks;
        reasonList.innerHTML = '';
        if (!current) {
            comment.value = '';
            return;
        }
        const values = feedbackFormValues(current, draft);
        for (const [code, label] of feedbackReasons[current.rating] || []) {
            const chip = document.createElement('label');
            chip.className = 'assistant-feedback-reason';
            const input = document.createElement('input');
            input.type = 'checkbox';
            input.value = code;
            input.checked = values.reasonCodes.includes(code);
            const text = document.createElement('span');
            text.textContent = label;
            chip.append(input, text);
            reasonList.appendChild(chip);
        }
        comment.value = values.comment;
    };

    container.append(prompt, voteActions, note, details);
    sync();
    return container;
};
