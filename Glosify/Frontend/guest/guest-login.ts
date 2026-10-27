import { requiredElement } from '../shared/dom.js';
import { localReturnUrl } from './local-return-url.js';

const dialog = document.querySelector<HTMLDialogElement>('[data-login-dialog]');
if (dialog) {
    const content = requiredElement(dialog, '[data-login-content]');
    const error = requiredElement(dialog, '[data-login-error]');
    let trigger: HTMLElement | undefined;
    let request: AbortController | undefined;
    let returnUrl = '/';
    const localUrl = (value: unknown) => localReturnUrl(value, location.origin);
    async function renderResponse(response: Response) {
        if (!response.ok) throw new Error('Login request failed');
        if (response.headers.get('content-type')?.includes('application/json')) {
            const result = await response.json();
            location.assign(localUrl(result.redirectUrl));
            return;
        }
        content.innerHTML = await response.text();
        content.querySelector<HTMLElement>('input:not([type="hidden"]), button')?.focus();
    }
    const openLogin = async (element: HTMLElement) => {
        trigger = element;
        returnUrl = localUrl(element.dataset.loginReturn || document.querySelector<HTMLElement>('[data-guest-return]')?.dataset.guestReturn || location.pathname + location.search);
        const url = `/login?dialog=true&returnUrl=${encodeURIComponent(returnUrl)}`;
        requiredElement<HTMLAnchorElement>(dialog, '[data-login-fallback]').href = `/login?returnUrl=${encodeURIComponent(returnUrl)}`;
        request?.abort();
        request = new AbortController();
        content.replaceChildren();
        error.hidden = true;
        if (!dialog.open) dialog.showModal();
        try { await renderResponse(await fetch(url, { signal: request?.signal })); }
        catch (failure) { if (!(failure instanceof DOMException && failure.name === 'AbortError')) error.hidden = false; }
    };
    document.addEventListener('click', event => {
        const element = (event.target as Element | null)?.closest<HTMLElement>('[data-login-required], .app-navigation-auth[href]');
        if (!element || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        openLogin(element);
    }, true);
    requiredElement(dialog, '[data-login-close]').addEventListener('click', () => dialog.close());
    dialog.addEventListener('close', () => { request?.abort(); trigger?.focus(); });
    dialog.addEventListener('submit', async event => {
        const form = event.target;
        if (!(form instanceof HTMLFormElement)) return;
        if (!form.matches('.auth-form')) return; // External providers navigate normally.
        event.preventDefault();
        const submit = requiredElement<HTMLButtonElement>(form, '[type="submit"]');
        submit.disabled = true;
        error.hidden = true;
        try {
            await renderResponse(await fetch(form.action, { method: 'POST', body: new FormData(form), signal: request?.signal }));
        } catch (failure) {
            if (!(failure instanceof DOMException && failure.name === 'AbortError')) error.hidden = false;
        } finally { submit.disabled = false; }
    });
    // Feature scripts can use this for a protected request that returns 401.
    document.addEventListener('glosify:login-required', () => openLogin(document.activeElement instanceof HTMLElement ? document.activeElement : document.body));
}
