export function initializeModals() {
    const focusableSelector = [
        'a[href]',
        'button:not([disabled])',
        'input:not([disabled]):not([type="hidden"])',
        'select:not([disabled])',
        'textarea:not([disabled])',
        'summary',
        '[contenteditable="true"]',
        '[tabindex]:not([tabindex="-1"])'
    ].join(',');

    let activeModal: HTMLElement | null = null;
    let activeModalTrigger: HTMLElement | null = null;

    const visibleFocusableElements = (dialog: HTMLElement) => Array.from(dialog.querySelectorAll<HTMLElement>(focusableSelector))
        .filter(element => element.getClientRects().length > 0 && element.getAttribute('aria-hidden') !== 'true');

    const closeModal = (backdrop: HTMLElement | null, restoreFocus = true) => {
        if (!backdrop) {
            return;
        }

        const trigger = activeModal === backdrop ? activeModalTrigger : null;
        backdrop.classList.remove('open');

        if (trigger) {
            trigger.setAttribute('aria-expanded', 'false');
        }

        if (restoreFocus && trigger?.isConnected) {
            trigger.focus();
        } else if (backdrop.contains(document.activeElement)) {
            (document.activeElement as HTMLElement | null)?.blur();
        }

        backdrop.setAttribute('aria-hidden', 'true');

        if (activeModal === backdrop) {
            activeModal = null;
            activeModalTrigger = null;
        }
    };

    const openModal = (backdrop: HTMLElement | null, trigger: HTMLElement) => {
        const dialog = backdrop?.querySelector<HTMLElement>('[role="dialog"]');
        if (!backdrop || !dialog) {
            return;
        }

        if (activeModal && activeModal !== backdrop) {
            closeModal(activeModal, false);
        }

        activeModal = backdrop;
        activeModalTrigger = trigger;
        trigger.setAttribute('aria-expanded', 'true');
        backdrop.removeAttribute('aria-hidden');
        backdrop.classList.add('open');

        window.requestAnimationFrame(() => {
            if (activeModal !== backdrop) {
                return;
            }

            const initialFocus = dialog.querySelector<HTMLElement>('[data-modal-initial-focus]')
                || visibleFocusableElements(dialog)[0]
                || dialog;
            initialFocus.focus();
        });
    };

    document.addEventListener('click', event => {
        if (!(event.target instanceof Element)) return;
        const opener = event.target.closest<HTMLElement>('[data-modal-open]');
        if (opener) {
            openModal(document.getElementById(opener.dataset.modalOpen || ''), opener);
            return;
        }

        const closer = event.target.closest('[data-modal-close]');
        if (closer) {
            closeModal(closer.closest<HTMLElement>('.modal-backdrop'));
            return;
        }

        // Clicking the backdrop itself (not the modal inside it) closes it.
        if (event.target instanceof Element && event.target.classList.contains('modal-backdrop')) {
            closeModal(event.target as HTMLElement);
        }
    });

    document.addEventListener('keydown', event => {
        if (!activeModal) {
            return;
        }

        if (event.key === 'Escape') {
            event.preventDefault();
            event.stopImmediatePropagation();
            closeModal(activeModal);
            return;
        }

        if (event.key !== 'Tab') {
            return;
        }

        const dialog = activeModal.querySelector<HTMLElement>('[role="dialog"]');
        const focusableElements = dialog ? visibleFocusableElements(dialog) : [];
        if (focusableElements.length === 0) {
            event.preventDefault();
            dialog?.focus();
            return;
        }

        const first = focusableElements[0];
        const last = focusableElements[focusableElements.length - 1];
        if (event.shiftKey && (document.activeElement === first || !dialog?.contains(document.activeElement))) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && (document.activeElement === last || !dialog?.contains(document.activeElement))) {
            event.preventDefault();
            first.focus();
        }
    });
}
