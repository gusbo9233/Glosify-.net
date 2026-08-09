# ADR 0002: Problem Details compatibility field

- Status: Accepted
- Date: 2026-08-09

## Context

Glosify APIs now use RFC 9457-style Problem Details responses. Older web and
browser-extension clients read a top-level `error` property, while the standard
human-readable fields are `detail` and `title`.

Removing `error` immediately would break already-installed extension clients.
Keeping it indefinitely without an exit condition would make the temporary shape
an accidental permanent contract.

## Decision

The current unversioned API contract includes `error` as a compatibility alias for
`detail`. First-party HTTP clients must prefer `detail`, then `title`, and consult
`error` only as a fallback for an older server.

The compatibility alias will not be removed from the current unversioned contract.
Its removal is tied to the first explicitly versioned, breaking API contract after:

1. the released web application consumes Problem Details without requiring `error`;
2. a released browser-extension version consumes Problem Details without requiring
   `error`; and
3. the versioned contract and its tests document that `error` is absent.

Until all three conditions are met, contract tests must continue to assert that the
current API emits `error` with the same user-safe text as `detail`.

## Consequences

- Existing clients remain compatible during the transition.
- New code learns and uses the standard Problem Details fields.
- Removing the alias requires an intentional API-version decision and matching
  contract tests, rather than an untracked cleanup.
