export function transcriptPresentation(status) {
    const count = status?.turnCount || 0;
    const saved = status?.savedTurns || 0;
    return {
        enabled: count > 0,
        label: status?.transcriptId ? 'Update transcript' : 'Save transcript',
        message: count === 0 ? '' : status?.suppressed ? 'Automatic saving paused after deletion. Save to resume.'
            : saved >= count ? 'Transcript saved' : `${count - saved} unsaved spoken turns`,
        href: status?.transcriptId ? `/Transcripts/${encodeURIComponent(status.transcriptId)}` : null,
    };
}
