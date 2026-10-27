import { requiredElement } from './dom.js';

(() => {
    const dialog = document.querySelector<HTMLDialogElement>('#app-settings');
    if (!dialog || typeof dialog.showModal !== 'function') return;
    const triggers = [...document.querySelectorAll<HTMLElement>('[data-app-settings-open]')];
    const tabs = [...dialog.querySelectorAll<HTMLElement>('[data-settings-tab]')];
    const tablist = requiredElement(dialog, '[role="tablist"]');
    const narrow = matchMedia('(max-width: 600px)');
    let opener: HTMLElement | undefined;

    const selectTab = (tab: HTMLElement) => {
        for (const item of tabs) {
            const selected = item === tab;
            item.setAttribute('aria-selected', String(selected));
            item.tabIndex = selected ? 0 : -1;
            requiredElement<HTMLElement>(dialog, '#' + item.getAttribute('aria-controls')).hidden = !selected;
        }
    };
    const orientation = () => tablist.setAttribute('aria-orientation', narrow.matches ? 'horizontal' : 'vertical');
    orientation(); narrow.addEventListener('change', orientation);
    for (const trigger of triggers) trigger.addEventListener('click', event => {
        event.preventDefault();
        if (dialog.open) return;
        opener = trigger;
        selectTab(tabs[0]);
        dialog.showModal();
        document.body.classList.add('app-settings-open');
        triggers.forEach(item => item.setAttribute('aria-expanded', 'true'));
    });
    requiredElement(dialog, '[data-app-settings-close]').addEventListener('click', () => dialog.close());
    dialog.addEventListener('close', () => {
        document.body.classList.remove('app-settings-open');
        triggers.forEach(item => item.setAttribute('aria-expanded', 'false'));
        // The visible trigger can change if the viewport rotates while open.
        const available = (item: HTMLElement | undefined) => item && !item.closest('[inert]') && item.getClientRects().length && getComputedStyle(item).visibility !== 'hidden';
        const target = available(opener) ? opener : triggers.find(available);
        (target ?? document.querySelector<HTMLElement>('[data-sidebar-toggle]'))?.focus();
    });
    dialog.addEventListener('keydown', event => {
        if (event.key !== 'Tab') return;
        const focusable = [...dialog.querySelectorAll<HTMLElement>('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])')]
            .filter(item => item.tabIndex >= 0 && item.getClientRects().length);
        const first = focusable[0], last = focusable.at(-1);
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault(); last?.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault(); first?.focus();
        }
    });
    // Only a click that begins and ends on the backdrop dismisses the panel.
    let backdropPressed = false;
    const outside = (event: MouseEvent) => {
        const rect = dialog.getBoundingClientRect();
        return event.target === dialog && (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom);
    };
    dialog.addEventListener('pointerdown', event => { backdropPressed = outside(event); });
    dialog.addEventListener('click', event => {
        if (backdropPressed && outside(event)) dialog.close();
        backdropPressed = false;
    });
    for (const tab of tabs) {
        tab.addEventListener('click', () => selectTab(tab));
        tab.addEventListener('keydown', event => {
            const index = tabs.indexOf(tab);
            const forward = narrow.matches ? (document.documentElement.dir === 'rtl' ? 'ArrowLeft' : 'ArrowRight') : 'ArrowDown';
            const backward = narrow.matches ? (document.documentElement.dir === 'rtl' ? 'ArrowRight' : 'ArrowLeft') : 'ArrowUp';
            let next;
            if (event.key === forward) next = tabs[(index + 1) % tabs.length];
            else if (event.key === backward) next = tabs[(index + tabs.length - 1) % tabs.length];
            else if (event.key === 'Home') next = tabs[0];
            else if (event.key === 'End') next = tabs.at(-1);
            if (next) { event.preventDefault(); selectTab(next); next.focus(); }
        });
    }
    selectTab(tabs[0]);
    dialog.removeAttribute('open');
    document.body.classList.add('app-settings-ready');
})();
