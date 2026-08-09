const readError = async (response, fallback) => {
    const body = await response.json().catch(() => null);
    throw new Error(body?.detail || body?.title || body?.error || fallback);
};

export const createAssistantApi = (tokenProvider) => ({
    async json(url, options = {}, fallback = 'Assistant request failed.') {
        const headers = {
            Accept: 'application/json',
            RequestVerificationToken: tokenProvider() || '',
            ...(options.body === undefined ? {} : { 'Content-Type': 'application/json' }),
            ...options.headers,
        };
        const response = await fetch(url, { ...options, headers });
        if (!response.ok) await readError(response, fallback);
        return response.status === 204 ? null : response.json();
    },
});
