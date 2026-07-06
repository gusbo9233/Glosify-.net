(() => {
    const container = document.querySelector('[data-flashcard-session]');
    if (!container) {
        return;
    }

    container.addEventListener('submit', async event => {
        const form = event.target.closest('[data-flashcard-form]');
        if (!form) {
            return;
        }

        event.preventDefault();
        const submitter = event.submitter;
        const clickableCard = form.querySelector('.flashcard-clickable');
        clickableCard?.classList.add('is-flipping');

        const body = new FormData(form);
        if (submitter?.name) {
            body.set(submitter.name, submitter.value);
        }

        const swipingCard = container.querySelector('.flashcard.is-swipe-out-left, .flashcard.is-swipe-out-right');
        if ((clickableCard || swipingCard) && !window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
            await new Promise(resolve => window.setTimeout(resolve, 180));
        }

        const response = await fetch(form.action, {
            method: form.method || 'post',
            body,
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        });

        if (response.redirected) {
            window.location.href = response.url;
            return;
        }

        if (response.ok) {
            container.innerHTML = await response.text();
        }
    });

    // Touch swipe: right = Good, left = Again; on a hidden card any swipe reveals.
    const SWIPE_THRESHOLD = 70;
    let drag = null;
    let suppressClick = false;

    container.addEventListener('pointerdown', event => {
        suppressClick = false;
        if (event.pointerType === 'mouse') {
            return;
        }
        const card = event.target.closest('.flashcard');
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

    const endDrag = (event, cancelled) => {
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

        if (cancelled || Math.abs(dx) < SWIPE_THRESHOLD) {
            return;
        }

        if (card.classList.contains('is-revealed')) {
            const rating = dx > 0 ? 'good' : 'again';
            const button = container.querySelector(`[data-flashcard-form] button[name="rating"][value="${rating}"]`);
            if (button) {
                card.classList.add(dx > 0 ? 'is-swipe-out-right' : 'is-swipe-out-left');
                button.closest('form').requestSubmit(button);
            }
        } else {
            container.querySelector('[data-card-reveal-form]')?.requestSubmit();
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
})();
