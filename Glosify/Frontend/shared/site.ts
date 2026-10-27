import { createTextFormatter } from './localization.js';
import { initializeModals } from './modals.js';

declare global {
    interface Window { glosifyText: ReturnType<typeof createTextFormatter>; }
}

// Delegated behavior keeps inline handlers out of the content security policy.
(() => {
    window.glosifyText = createTextFormatter(document.body?.dataset.i18n);
    initializeModals();

    document.addEventListener('submit', event => {
        const form = event.target instanceof Element ? event.target.closest<HTMLFormElement>('form[data-confirm]') : null;
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
