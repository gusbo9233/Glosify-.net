# ADR 0001: Single-instance state for the portfolio deployment

- Status: Accepted
- Date: 2026-08-09

## Decision

Glosify runs as one web-app instance during the portfolio phase. The deployment must
not enable scale-out or overlapping slots that both accept traffic.

The following state is intentionally process-local:

- practice-session progress;
- short-lived mobile external-login authorization codes;
- realtime translation relay tokens and active session coordination;
- per-process rate-limit counters and keyed translation locks;
- default ASP.NET Core data-protection keys when no external key ring is configured.

Restarting the process can end active sessions and invalidate short-lived codes. This
is acceptable for a learning/portfolio deployment and is preferable to adding
distributed infrastructure that the current workload does not require.

## Scale-out trigger and replacement

Before running two instances, move session/authorization state and rate-limit counters
to Redis, persist data-protection keys in Blob Storage or Key Vault, and make
cleanup jobs lease-based so only one instance performs
each job. Add multi-instance integration tests before enabling App Service scale-out.
