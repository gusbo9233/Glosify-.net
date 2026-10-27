// Resolve destructive transitions against the server's finalized turn count.
export async function preserveConversation<S extends { turnCount: number; savedTurns: number }>(id: string, { status, choose, save, forget, stop = async () => {}, pendingTurn = false }: {
    status: (id: string) => Promise<S>;
    choose: (status: S) => Promise<string | null>;
    save: (id: string) => Promise<unknown>;
    forget: (id: string) => Promise<unknown>;
    stop?: () => Promise<unknown>;
    pendingTurn?: boolean;
}) {
    const current = await status(id);
    let decision;
    if (current.turnCount > current.savedTurns || pendingTurn) {
        decision = await choose(current);
        if (decision !== 'save' && decision !== 'discard') return false;
    }
    // Stop only after an accepted decision, then save the finalized source.
    await stop();
    if (decision === 'save') await save(id);
    await forget(id);
    return true;
}
