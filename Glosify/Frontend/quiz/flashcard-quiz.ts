import { text as t } from '../shared/localization.js';
(() => {
    let activeContainer: HTMLElement | null = null;
    let dispose = () => {};

    const initialize = () => {
        const container = document.querySelector<HTMLElement>('[data-flashcard-session]');
        if (container === activeContainer) return;
        dispose();
        activeContainer = container;
        dispose = container ? attachControls(container) : () => {};
    };

    const attachControls = (container: HTMLElement) => {
        let submissionInFlight = false;
        let startedAt = performance.now();
        const isBusy = () => submissionInFlight || container.closest?.('[data-anki-workspace][aria-busy="true"]');

        const setRequestError = (message = '') => {
            const status = container.querySelector<HTMLElement>('[data-flashcard-status]');
            if (!status) {
                return;
            }

            status.textContent = message;
            status.hidden = !message;
        };

        container.addEventListener('submit', async event => {
            const form = (event.target as Element | null)?.closest<HTMLFormElement>('[data-flashcard-form]');
            if (!form) {
                return;
            }

            event.preventDefault();
            if (isBusy()) {
                return;
            }

            submissionInFlight = true;
            container.setAttribute('aria-busy', 'true');
            setRequestError();

            const submitter = event.submitter as HTMLButtonElement | HTMLInputElement | null;
            const enabledSubmitControls = [...container.querySelectorAll<HTMLButtonElement | HTMLInputElement>(
                '[data-flashcard-form] button[type="submit"]:not(:disabled), [data-flashcard-form] input[type="submit"]:not(:disabled)'
            )];
            enabledSubmitControls.forEach(control => { control.disabled = true; });

            const clickableCard = form.querySelector('.flashcard-clickable');
            clickableCard?.classList.add('is-flipping');

            const body = new FormData(form);
            body.set("DurationMilliseconds", String(Math.round(performance.now() - startedAt)));
            if (submitter?.name) {
                body.set(submitter.name, submitter.value);
            }

            const swipingCard = container.querySelector('.flashcard.is-swipe-out-left, .flashcard.is-swipe-out-right');
            try {
                if ((clickableCard || swipingCard) && !window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
                    await new Promise(resolve => window.setTimeout(resolve, 180));
                }

                const response = await fetch(form.action, {
                    method: form.method || 'post',
                    body,
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                });

                if (response.redirected && !new URL(response.url).pathname.toLowerCase().startsWith("/anki/study")) {
                    window.location.href = response.url;
                    return;
                }

                if (response.ok) {
                    container.innerHTML = await response.text();
                    if (submitter?.name === "rating") startedAt = performance.now();
                } else {
                    const problem = await response.json().catch(() => null);
                    setRequestError(problem?.detail || t('Client.GenericError', 'Something went wrong. Please try again.'));
                }
            } catch {
                setRequestError(t(
                    'Client.GenericError',
                    'Something went wrong. Please try again.'));
            } finally {
                submissionInFlight = false;
                container.setAttribute('aria-busy', 'false');
                clickableCard?.classList.remove('is-flipping');
                swipingCard?.classList.remove('is-swipe-out-left', 'is-swipe-out-right');
                enabledSubmitControls.forEach(control => { control.disabled = false; });
            }
        });

        const onKeyDown = (event: KeyboardEvent) => {
            const target = event.target as HTMLElement;
            if (isBusy() || event.metaKey || event.ctrlKey || event.altKey || target.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName)) return;
            const button = [...container.querySelectorAll<HTMLButtonElement>('[data-rating-key]')].find(b => b.dataset.ratingKey === event.key);
            if (button) { event.preventDefault(); button.closest('form')?.requestSubmit(button); }
            else if (event.key === ' ' && container.querySelector<HTMLFormElement>('[data-card-reveal-form]') && !target.closest('button,a')) {
                event.preventDefault(); container.querySelector<HTMLFormElement>('[data-card-reveal-form]')?.requestSubmit();
            }
        };
        document.addEventListener('keydown', onKeyDown);

        // Both modes reveal hidden cards first; revealed-card swipes use the configured rating.
        const SWIPE_THRESHOLD = 70;
        let drag: { card: HTMLElement; pointerId: number; startX: number; startY: number; dx: number; active: boolean } | null = null;
        let suppressClick = false;

        container.addEventListener('pointerdown', event => {
            suppressClick = false;
            if (event.pointerType === 'mouse') {
                return;
            }
            if (isBusy() || (event.target as Element | null)?.closest('.btn-icon-tts')) return;
            const card = (event.target as Element | null)?.closest<HTMLElement>('.flashcard');
            if (!card) {
                return;
            }
            drag = {
                card,
                pointerId: event.pointerId,
                startX: event.clientX,
                startY: event.clientY,
                dx: 0,
                active: false
            };
        });

        container.addEventListener('pointermove', event => {
            if (!drag || event.pointerId !== drag.pointerId) {
                return;
            }
            const dx = event.clientX - drag.startX;
            const dy = event.clientY - drag.startY;
            if (!drag.active) {
                if (Math.abs(dx) < 10 || Math.abs(dx) <= Math.abs(dy)) {
                    return;
                }
                drag.active = true;
                drag.card.classList.add('is-dragging');
            }
            drag.dx = dx;
            drag.card.style.transform = `translateX(${dx}px) rotate(${dx / 28}deg)`;
            const pastThreshold = Math.abs(dx) >= SWIPE_THRESHOLD;
            drag.card.classList.toggle('drag-right', pastThreshold && dx > 0);
            drag.card.classList.toggle('drag-left', pastThreshold && dx < 0);
        });

        const endDrag = (event: PointerEvent, cancelled: boolean) => {
            if (!drag || event.pointerId !== drag.pointerId) {
                return;
            }
            const { card, dx, active } = drag;
            drag = null;
            if (!active) {
                return;
            }

            suppressClick = true;
            card.classList.remove('is-dragging', 'drag-left', 'drag-right');
            card.style.transform = '';

            if (cancelled || isBusy() || Math.abs(dx) < SWIPE_THRESHOLD) {
                return;
            }

            if (card.classList.contains('is-revealed')) {
                const rating = dx > 0 ? (container.querySelector<HTMLElement>('[data-right-rating]')?.dataset.rightRating || 'good') : 'again';
                const button = container.querySelector<HTMLButtonElement>(`[data-flashcard-form] button[name="rating"][value="${rating}"]`);
                if (button) {
                    card.classList.add(dx > 0 ? 'is-swipe-out-right' : 'is-swipe-out-left');
                    button.closest('form')?.requestSubmit(button);
                }
            } else {
                container.querySelector<HTMLFormElement>('[data-card-reveal-form]')?.requestSubmit();
            }
        };

        container.addEventListener('pointerup', event => endDrag(event, false));
        container.addEventListener('pointercancel', event => endDrag(event, true));

        container.addEventListener('click', event => {
            if (!suppressClick) {
                return;
            }
            suppressClick = false;
            event.preventDefault();
            event.stopPropagation();
        }, true);

        return () => document.removeEventListener('keydown', onKeyDown);
    };

    initialize();
    window.addEventListener('learning:updated', initialize);
})();
