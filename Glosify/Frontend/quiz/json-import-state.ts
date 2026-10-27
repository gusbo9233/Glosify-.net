export interface ImportProblem { errors?: Record<string, string | string[]>; error?: string; detail?: string; title?: string; canonicalJson?: string; }

export function problemMessages(problem: ImportProblem | null | undefined) {
    const messages: string[] = [];
    if (problem?.errors && typeof problem.errors === 'object') {
        Object.entries(problem.errors).forEach(([path, values]) => {
            const entries = Array.isArray(values) ? values : [values];
            entries.filter(Boolean).forEach(value => messages.push(`${path}: ${value}`));
        });
    }
    if (messages.length === 0 && (problem?.error || problem?.detail || problem?.title)) {
        messages.push(problem.error || problem.detail || problem.title!);
    }
    return messages.length > 0 ? messages : ['The request could not be completed.'];
}

export function buildExternalRepairPrompt(json: string, messages: string[]) {
    return `Repair this into GlobeGlotter version 1 quiz-import JSON. Return only the complete JSON object. Preserve all usable learning content and translations; do not invent ids, visibility, target_language, or unsupported fields.\n\nValidation errors:\n${messages.join('\n')}\n\nJSON to repair:\n${json}`;
}

export function previewMatches(canonicalJson: unknown, currentJson: string) {
    return typeof canonicalJson === 'string' && canonicalJson === currentJson;
}

export function isRequestTimeout(error: unknown) {
    return !!error && typeof error === 'object' && 'name' in error
        && (error.name === 'AbortError' || error.name === 'TimeoutError');
}
