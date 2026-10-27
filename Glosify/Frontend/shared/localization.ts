export function createTextFormatter(serialized?: string) {
    let dictionary: Record<string, unknown> = {};
    try {
        const parsed: unknown = JSON.parse(serialized || '{}');
        if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) dictionary = parsed as Record<string, unknown>;
    } catch { /* Use the caller's fallback if localization data is unavailable. */ }

    return (key: string, fallback = '', ...values: unknown[]): string => {
        const localized = Object.hasOwn(dictionary, key) ? dictionary[key] : undefined;
        const template = typeof localized === 'string' && localized ? localized : fallback || key;
        return values.reduce<string>(
            (result, value, index) => result.replaceAll(`{${index}}`, String(value)), template);
    };
}

// Existing classic scripts consume the same formatter through the layout bridge.
export const text = (key: string, fallback: string, ...values: unknown[]) =>
    window.glosifyText?.(key, fallback, ...values) ?? fallback;
