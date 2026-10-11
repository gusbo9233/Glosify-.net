(() => {
    const sidebar = document.getElementById('app-sidebar');
    const toggle = document.querySelector('[data-sidebar-toggle]');
    const backdrop = document.querySelector('[data-sidebar-backdrop]');
    if (!sidebar || !toggle || !backdrop) return;
    const mobile = matchMedia('(max-width: 900px)');
    const main = document.querySelector('.app-main');
    const assistant = document.querySelector('[data-assistant-panel]');
    const preferenceKey = 'glosify.sidebar.open';
    let desktopOpen = true;
    try { desktopOpen = localStorage.getItem(preferenceKey) !== 'false'; } catch { /* Storage can be disabled. */ }
    let open = false;
    const controls = () => [...sidebar.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex="0"]'), toggle]
        .filter(element => element.getClientRects().length && !element.closest('[hidden], [inert]'));
    const render = () => {
        const modal = open && mobile.matches;
        document.body.classList.toggle('app-sidebar-open', open);
        toggle.setAttribute('aria-expanded', String(open));
        toggle.setAttribute('aria-label', open ? toggle.dataset.closeLabel : toggle.dataset.openLabel);
        sidebar.inert = !open;
        backdrop.hidden = !modal;
        if (main) main.inert = modal;
        if (assistant) assistant.inert = modal;
        if (modal) {
            sidebar.setAttribute('role', 'dialog');
            sidebar.setAttribute('aria-modal', 'true');
        } else {
            sidebar.removeAttribute('role');
            sidebar.removeAttribute('aria-modal');
        }
    };
    const setOpen = (value, focus = true) => {
        open = value;
        if (!mobile.matches) {
            desktopOpen = value;
            try { localStorage.setItem(preferenceKey, String(value)); } catch { /* Storage can be disabled. */ }
        }
        render();
        if (focus) {
            if (open && mobile.matches) controls()[0]?.focus();
            else toggle.focus();
        }
    };
    toggle.addEventListener('click', () => setOpen(!open));
    backdrop.addEventListener('click', () => setOpen(false));
    sidebar.querySelector('[data-app-settings-open]')?.addEventListener('click', () => {
        if (mobile.matches && !document.body.classList.contains('app-settings-ready')) setOpen(false, false);
    });
    mobile.addEventListener('change', () => {
        const focusWasInside = sidebar.contains(document.activeElement);
        open = mobile.matches ? false : desktopOpen;
        render();
        if (!open && focusWasInside) toggle.focus();
    });
    document.addEventListener('keydown', event => {
        if (!open || document.querySelector('dialog:modal')) return;
        if (event.key === 'Escape') { event.preventDefault(); setOpen(false); }
        if (event.key !== 'Tab' || !mobile.matches) return;
        const items = controls();
        const first = items[0], last = items.at(-1);
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
    });
    // Edge swipes open the mobile drawer; swiping it toward the edge closes it.
    let touch;
    document.addEventListener('touchstart', event => {
        touch = undefined;
        if (!mobile.matches || event.touches.length !== 1 || document.querySelector('dialog:modal')) return;
        const point = event.touches[0];
        const rtl = document.documentElement.dir === 'rtl';
        const edge = rtl ? innerWidth - point.clientX : point.clientX;
        if ((!open && edge <= 24) || (open && sidebar.contains(event.target)))
            touch = { x: point.clientX, y: point.clientY, direction: rtl ? -1 : 1, wasOpen: open };
    }, { passive: true });
    document.addEventListener('touchend', event => {
        if (!touch || !event.changedTouches.length) return;
        const start = touch; touch = undefined;
        const point = event.changedTouches[0];
        const distance = (point.clientX - start.x) * start.direction;
        if (Math.abs(point.clientY - start.y) > 50) return;
        if (!start.wasOpen && distance > 60) setOpen(true);
        else if (start.wasOpen && distance < -60) setOpen(false);
    }, { passive: true });
    document.addEventListener('touchcancel', () => { touch = undefined; }, { passive: true });
    open = !mobile.matches && desktopOpen;
    render();
    document.body.classList.add('app-sidebar-ready');
})();
