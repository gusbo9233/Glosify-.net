// Site-wide delegated behaviors, replacing inline on* attributes so the CSP can
// disallow 'unsafe-inline' scripts.
(() => {
    document.addEventListener('click', event => {
        const opener = event.target.closest('[data-modal-open]');
        if (opener) {
            document.getElementById(opener.dataset.modalOpen)?.classList.add('open');
            return;
        }

        const closer = event.target.closest('[data-modal-close]');
        if (closer) {
            closer.closest('.modal-backdrop')?.classList.remove('open');
            return;
        }

        // Clicking the backdrop itself (not the modal inside it) closes it.
        if (event.target instanceof Element && event.target.classList.contains('modal-backdrop')) {
            event.target.classList.remove('open');
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
