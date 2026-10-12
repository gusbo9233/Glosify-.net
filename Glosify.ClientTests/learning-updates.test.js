import test from 'node:test';
import assert from 'node:assert/strict';
import { createLearningUpdates } from '../Glosify/wwwroot/js/learning-updates.js';

test('committed revisions coalesce, deduplicate, and invalidate stale refreshes', () => {
    const callbacks = new Map();
    const checks = [];
    let next = 0;
    const updates = createLearningUpdates({ refresh: current => checks.push(current), schedule: callback => { callbacks.set(++next, callback); return next; }, cancel: id => callbacks.delete(id) });
    updates.receive({ id: 'run', changeRevision: '' });
    assert.equal(callbacks.size, 0);
    updates.receive({ id: 'run', changeRevision: 'one' });
    updates.receive({ id: 'run', changeRevision: 'one' });
    assert.equal(callbacks.size, 1);
    [...callbacks.values()][0]();
    assert.equal(checks[0](), true);
    updates.receive({ id: 'run', changeRevision: 'two' });
    assert.equal(checks[0](), false);
    [...callbacks.values()][0]();
    assert.equal(checks[1](), true);
    updates.receive({ id: 'run', changeRevision: 'undone' });
    assert.equal(checks[1](), false);
});
