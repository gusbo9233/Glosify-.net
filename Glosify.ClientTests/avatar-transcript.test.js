import test from 'node:test';
import assert from 'node:assert/strict';
import { transcriptPresentation } from '../Glosify/wwwroot/js/avatar/transcript.js';

test('saving is unavailable before spoken content exists', () => {
    assert.equal(transcriptPresentation(null).enabled, false);
});
test('manual snapshot leaves newer spoken turns unsaved', () => {
    const view = transcriptPresentation({ turnCount: 8, savedTurns: 6, transcriptId: 'saved-id' });
    assert.equal(view.message, '2 unsaved spoken turns');
    assert.equal(view.label, 'Update transcript');
    assert.equal(view.href, '/Transcripts/saved-id');
});

test('deleting a transcript explains why automatic saving paused', () => {
    assert.match(transcriptPresentation({ turnCount: 4, savedTurns: 0, suppressed: true }).message, /Save to resume/);
});
