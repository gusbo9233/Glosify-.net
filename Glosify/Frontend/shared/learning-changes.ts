export interface ResourceChange { kind: string; entityType: string; entityId: string; quizId?: string | null; }
export interface LearningChange { id: string; changeRevision?: string | null; resourceChanges?: ResourceChange[] | null; }
export interface StudyContext { studyQuizId?: string; studyAnkiCollectionId?: string; studyCardId?: string; }
interface UpdateOptions {
    refresh(isCurrent: () => boolean): unknown;
    accept?(run: LearningChange): boolean;
    schedule?(callback: () => void, delay: number): number;
    cancel?(timer: number | undefined): void;
}

// Refetch server-rendered content after committed assistant changes; keep the chat shell alive.
export function createLearningUpdates({ refresh, accept = () => true, schedule = (fn, ms) => setTimeout(fn, ms), cancel = clearTimeout }: UpdateOptions) {
    const revisions = new Map<string, string>();
    let timer: number | undefined;
    let generation = 0;
    return {
        receive(run: LearningChange) {
            if (!run.changeRevision || revisions.get(run.id) === run.changeRevision) return;
            revisions.set(run.id, run.changeRevision);
            if (!accept(run)) return;
            const current = ++generation;
            cancel(timer);
            timer = schedule(() => refresh(() => current === generation), 180);
        },
    };
}

// Unknown/legacy change payloads remain conservative. Known unrelated resources do not
// interrupt a study session; source edits still invalidate Anki cards from that quiz.
export function affectsStudy(run: Pick<LearningChange, 'resourceChanges'>, { studyQuizId, studyAnkiCollectionId, studyCardId }: StudyContext = {}) {
    if (!Array.isArray(run.resourceChanges) || !run.resourceChanges.length) return true;
    if (!studyQuizId && !studyAnkiCollectionId) return true;
    const metadataOnly = new Set(['rename_quiz', 'rename_collection', 'move_quiz', 'move_collection',
        'quiz_visibility', 'collection_visibility',
        'create_quiz', 'create_collection', 'copy_quiz', 'copy_collection']);
    return run.resourceChanges.some(change => {
        if (metadataOnly.has(change.kind)) return false;
        if (['word', 'sentence', 'quiz'].includes(change.entityType)) {
            const quizId = change.quizId || (change.entityType === 'quiz' ? change.entityId : null);
            return !quizId || !studyQuizId || quizId === studyQuizId;
        }
        if (['anki', 'anki_collection'].includes(change.entityType))
            return !!studyAnkiCollectionId && change.entityId === studyAnkiCollectionId;
        if (change.entityType === 'anki_card')
            return !!studyAnkiCollectionId && (!studyCardId || change.entityId === studyCardId);
        if (change.entityType === 'anki_quiz_link') return !!studyAnkiCollectionId;
        // A deleted folder can contain the current quiz, including a nested folder.
        return true;
    });
}

// Deleted quiz/folder detail routes redirect to their own library. Treat only
// that exact same-origin destination as removal; login or unrelated redirects
// must never replace learning content with another page's response.
export function isRemovedItemRedirect(response: Pick<Response, 'ok' | 'redirected' | 'url'>, requestUrl: string) {
    if (!response.ok || !response.redirected) return false;
    try {
        const original = new URL(requestUrl), destination = new URL(response.url);
        const resource = original.pathname.match(/^\/(quiz|anki)\/(?:details|collection|settings)(?:\/|$)/i)?.[1];
        return !!resource && destination.origin === original.origin
            && new RegExp(`^/${resource}(?:/index)?/?$`, 'i').test(destination.pathname)
            && !destination.search && !destination.hash;
    } catch { return false; }
}
