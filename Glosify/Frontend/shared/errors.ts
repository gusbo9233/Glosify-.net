export function errorMessage(error: unknown, fallback: string): string {
    return error && typeof error === 'object' && 'message' in error && typeof error.message === 'string'
        ? error.message : fallback;
}

export function errorStatus(error: unknown): number | undefined {
    return error && typeof error === 'object' && 'status' in error && typeof error.status === 'number'
        ? error.status : undefined;
}
