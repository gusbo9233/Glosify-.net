# Review guidance

Use AGENTS.md and the referenced repository context when reviewing changes.
Confirm documentation against the current implementation and tests.

Prioritize actionable correctness, security, data-loss, and compatibility defects
introduced by the PR. Explain the concrete trigger, affected code path, and
observable consequence. Inspect callers and existing safeguards before reporting
a finding; distinguish verified behavior from assumptions.

Respect documented architecture and deployment decisions. Recommend structural
changes only when a concrete defect requires them. Verify framework-specific
claims against current primary documentation when available, and state uncertainty
when verification is unavailable.

Copilot also reviews these PRs. Evaluate findings independently; agreement between
bots is not evidence that a defect exists. Avoid repeating an existing finding
when it is visible unless adding new evidence or identifying an incomplete fix.
