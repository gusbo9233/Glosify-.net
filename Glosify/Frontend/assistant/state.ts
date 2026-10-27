export interface ChatContext {
    contextQuizId?: string | null;
    contextTranscriptId?: string | null;
    contextBookDocumentId?: string | null;
}
export interface Chat extends ChatContext {
    id: string;
    title: string;
    preview?: string | null;
    updatedAtUtc?: string;
    updatedAt?: string;
    contextQuizName?: string | null;
    contextBookTitle?: string | null;
    contextTranscriptTitle?: string | null;
    contextLabel?: string | null;
}
export const materialPayload = (kind: string | null, id: string | null) => ({
    contextTranscriptId: kind === 'transcript' ? id : null,
    contextBookDocumentId: kind === 'book' ? id : null,
});
export const chatContext = (chat: ChatContext | null) => ({
    contextQuizId: chat?.contextQuizId || null,
    contextTranscriptId: chat?.contextTranscriptId || null,
    contextBookDocumentId: chat?.contextBookDocumentId || null,
});
const sameId = (left: string, right: string) => String(left).toLowerCase() === String(right).toLowerCase();
export const upsertChat = <T extends { id: string }>(chats: T[], updated: T) => [updated, ...chats.filter(chat => !sameId(chat.id, updated.id))];
export const replaceChat = <T extends { id: string }>(chats: T[], updated: T) => chats.map(chat => sameId(chat.id, updated.id) ? updated : chat);
export const removeChat = <T extends { id: string }>(chats: T[], id: string) => chats.filter(chat => !sameId(chat.id, id));
export const createRecoverablePromiseQueue = <T, R>(write: (snapshot: T) => Promise<R>) => {
    let tail: Promise<unknown> = Promise.resolve();
    return (snapshot: T) => {
        const current = tail.catch(() => undefined).then(() => write(snapshot));
        tail = current;
        return current;
    };
};
export const createRecoverableSingleFlight = <T extends unknown[], R>(work: (...args: T) => R | Promise<R>) => {
    let current: Promise<R> | null = null;
    return (...args: T) => {
        if (!current) {
            current = Promise.resolve().then(() => work(...args)).catch(error => {
                current = null;
                throw error;
            });
        }
        return current;
    };
};
export const chooseInitialChat = <T extends { id: string; preview?: string | null }>(chats: T[], storedId: string | null) => {
    const stored = storedId ? chats.find(chat => sameId(chat.id, storedId)) : null;
    return stored || (chats[0] && !chats[0].preview ? chats[0] : null);
};
