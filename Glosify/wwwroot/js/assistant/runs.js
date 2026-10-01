// Runs are owned by the server. A dropped connection or a closed tab never cancels one:
// the page reconnects to the run and picks up from its latest saved state.

const TERMINAL = ['completed', 'cancelled', 'failed'];
const WAITING = ['awaiting_input', 'awaiting_approval', 'paused'];

export const isTerminal = status => TERMINAL.includes(status);
export const isWaiting = status => WAITING.includes(status);
export const isWorking = status => !!status && !isTerminal(status) && !isWaiting(status);

// A newer revision always wins; an update can arrive twice or out of order when the stream
// reconnects while a command response is also in flight.
export const newer = (current, next) => !current || current.id !== next.id || next.revision >= current.revision;

/** What the composer does with a message while a run is active in this chat. */
export const sendAction = run => {
    if (!run || isTerminal(run.status)) return 'start';
    if (run.status === 'awaiting_input') return 'answer';
    return 'steer';
};

export function createAssistantRuns({
    request,
    onUpdate,
    isCurrent,
    openEvents = url => (typeof EventSource === 'function' ? new EventSource(url) : null),
    schedule = (callback, delay) => setTimeout(callback, delay),
    newKey = () => crypto.randomUUID(),
    base = '/Assistant/Runs',
}) {
    const runs = new Map();
    const streams = new Map();
    const submissions = new Map();

    const publish = run => {
        if (!run) return;
        const current = runs.get(run.threadId);
        if (!newer(current, run)) return;
        runs.set(run.threadId, run);
        if (isTerminal(run.status)) stop(run.threadId);
        if (isCurrent(run.threadId)) onUpdate(run);
    };

    const stop = threadId => {
        const stream = streams.get(threadId);
        if (stream) {
            stream.close?.();
            streams.delete(threadId);
        }
    };

    // Server-sent events carry every checkpoint as it happens. Browsers reconnect on their own,
    // sending the last revision seen; polling covers browsers without EventSource.
    const watch = run => {
        if (!run || isTerminal(run.status) || streams.has(run.threadId)) return;
        const source = openEvents(`${base}/${run.id}/events`);
        if (!source) {
            poll(run.threadId);
            return;
        }
        const stream = { close: () => source.close() };
        streams.set(run.threadId, stream);
        source.addEventListener('run', event => {
            try { publish(JSON.parse(event.data)); } catch { /* A malformed event is skipped. */ }
        });
        source.onerror = () => {
            // Closed by the server at the end of its lifetime or by the network. Reconnection
            // is automatic unless the browser gave up; then fall back to polling.
            if (source.readyState === 2 && streams.get(run.threadId) === stream) {
                streams.delete(run.threadId);
                poll(run.threadId);
            }
        };
    };

    const poll = threadId => {
        if (streams.has(threadId)) return;
        const stream = { close: () => { stream.closed = true; } };
        streams.set(threadId, stream);
        const tick = async () => {
            if (stream.closed) return;
            const run = runs.get(threadId);
            if (!run || !isCurrent(threadId)) { streams.delete(threadId); return; }
            try { publish(await request(`${base}/${run.id}`)); } catch { /* Try again on the next tick. */ }
            const latest = runs.get(threadId);
            if (!stream.closed && latest && !isTerminal(latest.status)) schedule(tick, isWaiting(latest.status) ? 5000 : 2500);
            else streams.delete(threadId);
        };
        schedule(tick, 1500);
    };

    const command = async (threadId, name, body = {}) => {
        const current = runs.get(threadId);
        if (!current) return null;
        let revision = current.revision;
        for (let attempt = 0; attempt < 3; attempt++) {
            try {
                const run = await request(`${base}/${current.id}/${name}`, {
                    method: 'POST',
                    body: JSON.stringify({ revision, ...body }),
                });
                publish(run);
                watch(run);
                return run;
            } catch (error) {
                // Stop and follow-ups mean the same at any revision; a decision does not, because
                // what the user saw has changed.
                if (!['cancel', 'steer'].includes(name) || error.status !== 409 || attempt === 2) throw error;
                const latest = await request(`${base}/${current.id}`);
                publish(latest);
                revision = latest.revision;
            }
        }
        return null;
    };

    return {
        get: threadId => runs.get(threadId) || null,

        async discover(threadId) {
            const run = await request(`${base}/chats/${threadId}`);
            if (!run) { runs.delete(threadId); return null; }
            publish(run);
            watch(run);
            return run;
        },

        async start(threadId, input) {
            // Retrying the same message after a network error reuses its key, so the server
            // returns the run it already accepted instead of starting a second one.
            const fingerprint = JSON.stringify({ threadId, input });
            const key = submissions.get(fingerprint) ?? newKey();
            submissions.set(fingerprint, key);
            const run = await request(`${base}/chats/${threadId}`, {
                method: 'POST',
                body: JSON.stringify({ idempotencyKey: key, request: input }),
            });
            submissions.delete(fingerprint);
            publish(run);
            watch(run);
            return run;
        },

        send(threadId, input) {
            // Discovery after a lost start response must not turn its retry into steering.
            if (submissions.has(JSON.stringify({ threadId, input }))) return this.start(threadId, input);
            const run = runs.get(threadId);
            switch (sendAction(run)) {
                case 'answer': return command(threadId, 'answer', { message: input.message });
                case 'steer': return command(threadId, 'steer', { message: input.message });
                default: return this.start(threadId, input);
            }
        },

        stop: threadId => command(threadId, 'cancel'),
        resume: threadId => command(threadId, 'resume'),
        approve: (threadId, always = false) => command(threadId, 'approve', { always }),
        reject: (threadId, message) => command(threadId, 'reject', { message: message || null }),
        answer: (threadId, answers, message) => command(threadId, 'answer', { answers, message: message || null }),

        async undo(runId) {
            const result = await request(`${base}/${runId}/undo`, { method: 'POST' });
            if (result?.run) publish(result.run);
            return result;
        },

        forget(threadId) {
            stop(threadId);
        },
    };
}
