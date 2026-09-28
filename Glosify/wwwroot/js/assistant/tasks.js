// Durable work is owned by the server. Polling and browser lifetime never cancel it.
export function createAssistantTasks({ request, onProgress, onCompleted, isCurrent,
    schedule = (callback, delay) => setTimeout(callback, delay),
    isHidden = () => document.hidden, isOnline = () => navigator.onLine,
    newKey = () => crypto.randomUUID() }) {
    const tasks = new Map();
    const submissions = new Map();
    const finished = new Set();
    const polling = new Set();
    let capability;
    const enabled = () => capability ??= request('/Assistant/Tasks/capabilities')
        .then(x => x?.enabled === true).catch(() => false);
    const active = task => task && !['completed', 'cancelled', 'failed'].includes(task.status);
    const update = async (threadId, task) => {
        if (!task) {
            tasks.delete(threadId);
            if (isCurrent(threadId)) onProgress(null);
            return;
        }
        tasks.set(threadId, task);
        if (isCurrent(threadId)) onProgress(task);
        if (['completed', 'cancelled'].includes(task.status) && !finished.has(task.id)) {
            finished.add(task.id);
            if (isCurrent(threadId)) await onCompleted(threadId);
        }
    };
    const poll = (threadId) => {
        if (polling.has(threadId)) return;
        polling.add(threadId);
        schedule(async () => {
            polling.delete(threadId);
            if (!isCurrent(threadId)) return;
            try {
                const task = tasks.get(threadId);
                if (task && isOnline()) await update(threadId, await request(`/Assistant/Tasks/${task.id}`));
            } catch { /* Reconnect to saved progress; a lost poll does not mean failed work. */ }
            const task = tasks.get(threadId);
            if (active(task) || (task?.status === 'completed' && task.evaluatedCalls < task.totalCalls
                && task.activity?.some(x => x.evaluationStatus === 'pending'))) poll(threadId);
        }, isHidden() || !isOnline() ? 15000 : 2000);
    };
    return {
        enabled,
        async discover(threadId) {
            if (!await enabled()) return;
            await update(threadId, await request(`/Assistant/Tasks/chats/${threadId}`));
            if (tasks.has(threadId)) poll(threadId);
        },
        async send(threadId, input) {
            if (!await enabled()) return false;
            const current = tasks.get(threadId);
            const fingerprint = JSON.stringify({ threadId, input });
            // Recover an ambiguous submission before treating this text as new steering.
            const pendingKey = submissions.get(fingerprint);
            let task;
            if (active(current) && !pendingKey) {
                let latest = current;
                for (let attempt = 0; attempt < 3; attempt++) {
                    try {
                        task = await request(`/Assistant/Tasks/${current.id}/steer`, {
                            method: 'POST', body: JSON.stringify({ revision: latest.revision, message: input.message }),
                        });
                        break;
                    } catch (error) {
                        if (error.status !== 409 || attempt === 2) throw error;
                        latest = await request(`/Assistant/Tasks/${current.id}`);
                        if (!active(latest)) throw error;
                    }
                }
            } else {
                const key = pendingKey ?? newKey();
                submissions.set(fingerprint, key);
                task = await request(`/Assistant/Tasks/chats/${threadId}`, {
                    method: 'POST', body: JSON.stringify({ idempotencyKey: key, request: input }),
                });
                submissions.delete(fingerprint);
            }
            await update(threadId, task);
            poll(threadId);
            return true;
        },
        async command(threadId, command) {
            const current = tasks.get(threadId);
            if (!current) return;
            let latest = current;
            for (let attempt = 0; attempt < 3; attempt++) {
                try {
                    await update(threadId, await request(`/Assistant/Tasks/${current.id}/${command}`, {
                        method: 'POST', body: JSON.stringify({ revision: latest.revision }),
                    }));
                    break;
                } catch (error) {
                    // Stop keeps the same meaning after progress advances. Approval does
                    // not: a changed proposal must be shown to the user again.
                    if (command !== 'cancel' || error.status !== 409 || attempt === 2) throw error;
                    latest = await request(`/Assistant/Tasks/${current.id}`);
                }
            }
            poll(threadId);
        },
    };
}
