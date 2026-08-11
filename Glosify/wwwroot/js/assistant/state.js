export const materialPayload = (kind, id) => ({
    contextTranscriptId: kind === 'transcript' ? id : null,
    contextBookDocumentId: kind === 'book' ? id : null,
});

const sameId = (left, right) => String(left).toLowerCase() === String(right).toLowerCase();

export const upsertChat = (chats, updated) => [updated, ...chats.filter(chat => !sameId(chat.id, updated.id))];
export const replaceChat = (chats, updated) => chats.map(chat => sameId(chat.id, updated.id) ? updated : chat);
export const removeChat = (chats, id) => chats.filter(chat => !sameId(chat.id, id));

export const createRecoverablePromiseQueue = (write) => {
    let tail = Promise.resolve();

    return (snapshot) => {
        const current = tail
            .catch(() => undefined)
            .then(() => write(snapshot));
        tail = current;
        return current;
    };
};

export const chooseInitialChat = (chats, storedId) => {
    const stored = storedId ? chats.find(chat => sameId(chat.id, storedId)) : null;
    return stored || (chats[0] && !chats[0].preview ? chats[0] : null);
};
