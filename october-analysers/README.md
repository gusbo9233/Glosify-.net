# Analyzer report freshness

All four provider exports were refreshed on October 28, 2026. JSON/CSV record counts and identifiers were verified. Counts include the statuses returned by each API and do not represent confirmed defects.

| Provider | Exported at (UTC) | Records |
| --- | --- | --- |
| DeepSource | 2026-10-28T07:15:26.540132+00:00 | 2,502 |
| Semgrep | 2026-10-28T07:14:45.682200+00:00 | 77 |
| Codacy | 2026-10-28T07:15:30.232446+00:00 | 1,551 |
| SonarQube | 2026-10-28T07:07:13.059864+00:00 | 418 |

DeepSource counts issue occurrences. Semgrep includes 62 SAST and 15 Supply Chain findings; Secrets are outside this export scope. SonarQube also returned zero security hotspots.

These are fresh downloads of the latest available provider results, not newly triggered scans. Export time does not establish analysis coverage of the current checkout.

Available revision metadata:

- DeepSource repository latest commit: `f5464ec3048c8d62fdad9598cfccf2ccf82d2238` (repository metadata, not proof of analysis coverage).
- Codacy last analyzed commit: `f5464ec3048c8d62fdad9598cfccf2ccf82d2238`.
- SonarQube latest analysis: `f5464ec3048c8d62fdad9598cfccf2ccf82d2238`; see [summary](sonarqube-summary.md).

## Previous local assessments

`findings-vs-current-files.*`, `validity-review.*`, `valid-fixes.*`, `remaining-verification.*`, and `validation/` describe the previous October 27 reports and local verification. They are retained as historical evidence, have not been regenerated, and must not be treated as assessments of the refreshed exports.

No application source changes or new external review runs were made.
