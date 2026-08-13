export const feedbackReasons = {
    up: [
        ['helpful', 'Helpful'],
        ['correct', 'Correct'],
        ['clear', 'Clear'],
        ['saved_time', 'Saved time'],
        ['tool_worked', 'Action worked'],
        ['other', 'Other'],
    ],
    down: [
        ['incorrect', 'Incorrect'],
        ['irrelevant', 'Not relevant'],
        ['confusing', 'Confusing'],
        ['too_slow', 'Too slow'],
        ['tool_failed', 'Action failed'],
        ['unsafe_or_inappropriate', 'Inappropriate'],
        ['other', 'Other'],
    ],
};

export const normalizeFeedback = (feedback) => feedback ? {
    rating: feedback.rating,
    reasonCodes: Array.isArray(feedback.reasonCodes) ? [...new Set(feedback.reasonCodes)] : [],
    comment: feedback.comment || null,
} : null;

/**
 * Which half of the feedback control is visible.
 *
 * A rating opens the detail form so reasons and a comment can be added. Saving those details
 * closes it and thanks the user, because re-rendering the same open form on success was
 * indistinguishable from the click doing nothing. Changing or clearing the rating reopens it.
 */
export const feedbackPanelState = (feedback, acknowledged) => ({
    showDetails: Boolean(feedback) && !acknowledged,
    showThanks: Boolean(feedback) && acknowledged,
});

export const validClientDuration = (milliseconds) =>
    Number.isFinite(milliseconds) && milliseconds >= 0 && milliseconds <= 900000;

export const createLatestRequestGate = () => {
    let latestRequest = 0;
    return {
        next: () => ++latestRequest,
        isCurrent: request => request === latestRequest,
    };
};
