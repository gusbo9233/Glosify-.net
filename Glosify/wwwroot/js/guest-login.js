const dialog = document.querySelector('[data-login-dialog]');
if (dialog) {
    const content = dialog.querySelector('[data-login-content]');
    const error = dialog.querySelector('[data-login-error]');
    let trigger;
    let request;
    let returnUrl = '/';
    function localUrl(value) {
        try {
            const url = new URL(value, location.origin);
            return url.origin === location.origin && url.pathname.startsWith('/') ? url.pathname + url.search : '/';
        } catch { return '/'; }
    }
    async function renderResponse(response) {
        if (!response.ok) throw new Error('Login request failed');
        if (response.headers.get('content-type')?.includes('application/json')) {
            const result = await response.json();
            location.assign(localUrl(result.redirectUrl));
            return;
        }
        content.innerHTML = await response.text();
        content.querySelector('input:not([type="hidden"]), button')?.focus();
    }
    async function openLogin(element) {
        trigger = element;
        returnUrl = localUrl(element.dataset.loginReturn || document.querySelector('[data-guest-return]')?.dataset.guestReturn || location.pathname + location.search);
        const url = `/login?dialog=true&returnUrl=${encodeURIComponent(returnUrl)}`;
        dialog.querySelector('[data-login-fallback]').href = `/login?returnUrl=${encodeURIComponent(returnUrl)}`;
        request?.abort();
        request = new AbortController();
        content.replaceChildren();
        error.hidden = true;
        if (!dialog.open) dialog.showModal();
        try { await renderResponse(await fetch(url, { signal: request.signal })); }
        catch (failure) { if (failure.name !== 'AbortError') error.hidden = false; }
    }
    document.addEventListener('click', event => {
        const element = event.target.closest('[data-login-required], .app-navigation-auth[href]');
        if (!element || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        openLogin(element);
    }, true);
    dialog.querySelector('[data-login-close]').addEventListener('click', () => dialog.close());
    dialog.addEventListener('close', () => { request?.abort(); trigger?.focus(); });
    dialog.addEventListener('submit', async event => {
        const form = event.target;
        if (!form.matches('.auth-form')) return; // External providers navigate normally.
        event.preventDefault();
        const submit = form.querySelector('[type="submit"]');
        submit.disabled = true;
        error.hidden = true;
        try {
            await renderResponse(await fetch(form.action, { method: 'POST', body: new FormData(form), signal: request.signal }));
        } catch (failure) {
            if (failure.name !== 'AbortError') error.hidden = false;
        } finally { submit.disabled = false; }
    });
    // Feature scripts can use this for a protected request that returns 401.
    document.addEventListener('glosify:login-required', () => openLogin(document.activeElement instanceof HTMLElement ? document.activeElement : document.body));
}
