// Site-wide delegated behaviors, replacing inline on* attributes so the CSP can
// disallow 'unsafe-inline' scripts.
(() => {
    let dictionary = {};
    try {
        dictionary = JSON.parse(document.body?.dataset.i18n || '{}');
    } catch {
        dictionary = {};
    }

    window.glosifyText = (key, fallback = '', ...values) => {
        const template = dictionary[key] || fallback || key;
        return values.reduce(
            (result, value, index) => result.replaceAll(`{${index}}`, String(value)),
            template);
    };

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

    let activeModal = null;
    let activeModalTrigger = null;

    const visibleFocusableElements = (dialog) => Array.from(dialog.querySelectorAll(focusableSelector))
        .filter(element => element.getClientRects().length > 0 && element.getAttribute('aria-hidden') !== 'true');

    const closeModal = (backdrop, restoreFocus = true) => {
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
            document.activeElement.blur();
        }

        backdrop.setAttribute('aria-hidden', 'true');

        if (activeModal === backdrop) {
            activeModal = null;
            activeModalTrigger = null;
        }
    };

    const openModal = (backdrop, trigger) => {
        const dialog = backdrop?.querySelector('[role="dialog"]');
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

            const initialFocus = dialog.querySelector('[data-modal-initial-focus]')
                || visibleFocusableElements(dialog)[0]
                || dialog;
            initialFocus.focus();
        });
    };

    document.addEventListener('click', event => {
        const opener = event.target.closest('[data-modal-open]');
        if (opener) {
            openModal(document.getElementById(opener.dataset.modalOpen), opener);
            return;
        }

        const closer = event.target.closest('[data-modal-close]');
        if (closer) {
            closeModal(closer.closest('.modal-backdrop'));
            return;
        }

        // Clicking the backdrop itself (not the modal inside it) closes it.
        if (event.target instanceof Element && event.target.classList.contains('modal-backdrop')) {
            closeModal(event.target);
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

        const dialog = activeModal.querySelector('[role="dialog"]');
        const focusableElements = dialog ? visibleFocusableElements(dialog) : [];
        if (focusableElements.length === 0) {
            event.preventDefault();
            dialog?.focus();
            return;
        }

        const first = focusableElements[0];
        const last = focusableElements[focusableElements.length - 1];
        if (event.shiftKey && (document.activeElement === first || !dialog.contains(document.activeElement))) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && (document.activeElement === last || !dialog.contains(document.activeElement))) {
            event.preventDefault();
            first.focus();
        }
    });

    document.addEventListener('submit', event => {
        const form = event.target instanceof Element ? event.target.closest('form[data-confirm]') : null;
        if (form && !window.confirm(form.dataset.confirm)) {
            event.preventDefault();
        }
    });

    document.addEventListener('change', event => {
        if (event.target instanceof Element && event.target.matches('[data-submit-on-change]')) {
            event.target.closest('form')?.submit();
        }
    });
})();
