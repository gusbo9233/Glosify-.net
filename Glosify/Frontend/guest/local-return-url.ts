export function localReturnUrl(value: unknown, origin: string): string {
    if (typeof value !== 'string' || !value) return '/';
    try {
        const url = new URL(value, origin);
        return url.origin === origin && url.pathname.startsWith('/') ? url.pathname + url.search : '/';
    } catch { return '/'; }
}
