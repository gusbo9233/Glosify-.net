const readError = async (response: Response, fallback: string) => {
    const body = await response.json().catch(() => null);
    throw new Error(body?.detail || body?.title || body?.error || fallback);
};

export const createAssistantApi = (tokenProvider: () => string | undefined) => ({
    async json<T = unknown>(url: URL | RequestInfo, options: Omit<RequestInit, 'headers'> & { headers?: Record<string, string> } = {}, fallback = 'Assistant request failed.'): Promise<T | null> {
        const headers = {
            Accept: 'application/json',
            RequestVerificationToken: tokenProvider() || '',
            ...(options.body === undefined ? {} : { 'Content-Type': 'application/json' }),
            ...options.headers,
        };
        const response = await fetch(url, { ...options, headers });
        if (!response.ok) await readError(response, fallback);
        if (response.status === 204) {
            await response.text();
            return null;
        }
        return response.json();
    },
});
