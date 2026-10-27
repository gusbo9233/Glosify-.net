export function cancellationError() {
    const error = new Error('Speech playback was stopped.');
    error.name = 'TtsPlaybackCancelledError';
    return error;
}

export function isCancellation(error: unknown) {
    return !!error && typeof error === 'object' && 'name' in error && (error.name === 'TtsPlaybackCancelledError' || error.name === 'AbortError');
}

export function safeCall<T extends unknown[]>(callback: ((...args: T) => void) | undefined, ...args: T) {
    if (typeof callback !== 'function') return;
    try {
        callback.call(null, ...args);
    } catch (error) {
        console.error('TTS callback failed.', error);
    }
}
