# Python and shell DeepSource triage

Reviewed all **83 occurrences: 82 Python and 1 shell**, grouped into 18 rules. Recommendation: **fix 5 release-smoke occurrences together**, **defer 58 lower-value robustness/style/type occurrences**, **dismiss or document 20 contextual false positives / accepted tooling assumptions**. No urgent production vulnerability was established in this subset.

## Revision and scope

The reviewed default-branch source is `origin/master` at `ef9cc38e6806531f7cd586e24c04460b2384f79e`. The local working branch differs substantially. In particular, 61 findings concern `scripts/avatar/*`, which exists on default but not in the local working tree. These are **not stale findings merely because they are absent locally**. Avatar references below are to default-branch source. Other reviewed Python/shell files are identical between default and working tree (`git diff origin/master -- <five files>` produced no output). The local uncommitted work was not changed.

I read all occurrence lines, the surrounding functions and consumers, avatar source manifests/README, the root repository instructions/README/ADRs, and the deployment/browser-test workflow. I did not run a production smoke, delete blobs, call translation providers, download assets, or render Blender scenes.

## What is worth fixing

### P2 — Make release-gating smoke checks independent of Python optimization (5 reported occurrences)

`BAN-B101` at `scripts/verify-fractional-tts.py:42,43,57,59,62` checks provider configuration, pricing ceilings, returned audio, cache equality, and exact credit settlement. The script contains a sixth unreported assertion at line 64 for the integer balance API. The workflow runs it immediately before reopening ingress (`.github/workflows/master_glosify.yml:461–466`). This is test code, so “assert outside tests” overstates the classification, but these checks form a release gate and should always execute.

Python optimization removes assertion statements. An isolated experiment compiling the actual module's top-level assertions (without imports, requests, credentials, or Azure calls) raised `AssertionError` for a deliberately invalid catalogue under `optimize=0`, then performed no checks under `optimize=2`. The current CI invocation uses ordinary `python3` and does not configure `PYTHONOPTIMIZE`; this is a robustness improvement, **not evidence that today's release gate is bypassed**. [Python assert semantics](https://docs.python.org/3/reference/simple_stmts.html#the-assert-statement).

Recommended fix: use a small explicit check helper that raises an exception with a safe diagnostic, converting **all six** checks together. A focused isolated test should feed bad catalogue/audio/settlement results and demonstrate nonzero failure both normally and with `PYTHONOPTIMIZE=1`; it must mock network/ledger operations and must not run a paid production smoke. Keep the security properties of the script: no credentials, user text, or audio in output.

### P3 — Asset validations when the builder is next touched (2 occurrences; deferred)

`BAN-B101` at default `scripts/avatar/build-rain.py:267,279` checks packed accessor layouts and float VEC3 components before interpreting the glTF binary. These are the outputs of the Blender export in the same script, with an explicit asset-generation command rather than a web-input path. Under optimization the checks disappear. Replacing them with clear `ValueError`s is useful fail-fast hardening, but lower priority than the release gate. Validate with small malformed accessor fixtures; a full character rebuild is unnecessary to prove rejection. No runtime rendering failure was reproduced.

## Findings to dismiss or document (20)

| Rule | Count | Decision and evidence |
|---|---:|---|
| `PYL-C1802` | 1 | **Reject suggested truthiness rewrite.** At `build-rain.py:292`, `changed` is a NumPy array from `flatnonzero`, not a list. `if not changed` can raise for multiple elements and confuses a single zero index with emptiness. Preserve `len(changed) == 0` or use `changed.size == 0`. The existing test is correct. [NumPy truth-value semantics](https://numpy.org/doc/stable/reference/arrays.ndarray.html#arithmetic-matrix-multiplication-and-comparison-operations). |
| `PYL-W0102` | 1 | **No current bug despite CRITICAL severity.** `compact_morph_targets(..., tolerances={...})` at `build-rain.py:252` only reads the dictionary via `.get` at line 290; it never mutates or exposes it. The only call is line 318. A `None` default would be harmless optional style, not a security/correctness fix. |
| `SH-3015` | 1 | **Wrong portability assumption.** `scripts/run-browser-tests.sh:32` uses Bash regex intentionally. Line 1 is a Bash shebang, README line 180 invokes `bash`, and CI line 164 invokes `bash`. The script also deliberately uses arrays, `BASH_SOURCE`, `shopt` and other Bash constructs. `bash -n` passed; an isolated matrix accepted loopback origins and rejected external/path-bearing/file URLs. Do not weaken the loopback validator just to make it POSIX-sh-compatible. |
| `BAN-B310` | 4 | **Trusted developer-tool URLs, not attacker-controlled application URLs.** `prepare-human.py:16,20` and `build-library.py:22` read checked-in pinned asset manifests; all 22/44 URL entries respectively are HTTPS and the downloaded bytes must match their pinned SHA-256 before use/write. `translate_ui.py:65` uses a constant default HTTPS API URL optionally overridden by the operator's environment (`OPENAI_API_URL`, line 25). No HTTP request parameter or uploaded source controls these URLs. Do not substitute HTTP clients solely to silence this audit. |
| `BAN-B314`, `BAN-B405` | 2 | **Trusted repository XML.** `translate_ui.py:15,90` parses `Glosify/Resources/Localization.UiText.resx` selected by the constant `SOURCE`, through `entries(SOURCE)` at line 104. No remote model output or user-provided XML is parsed. The security warning matters for untrusted XML; this call graph does not establish that boundary. [Python XML security scope](https://docs.python.org/3/library/xml.html). |
| `BAN-B607` | 4 | **Intentional installed CLI lookup.** `verify-fractional-tts.py:13,48` invokes fixed `az`/`dotnet`; `cleanup-retired-tts-cache.py:22,29` invokes fixed `az`. All use argument arrays, no shell command interpolation. The scripts run in the trusted operator/CI environment; CLI executables are not chosen by app users. Hardcoding machine-specific executable locations would reduce portability without resolving a demonstrated threat. |
| `PTC-W0063` | 7 | **The stated generator failure is not present.** Each flagged `next(...)` is called in an ordinary function/module, with a generator expression as its argument; no flagged surrounding function yields. Library lines 32/40/123/236 require a Principled shader on newly initialized materials; Rain line 91 requires the texture created by the fitting helper; human_mesh lines 129/130 require a material descriptor/texture in the pinned assets. Missing assets fail the developer build. Optional better diagnostics are fine, but silently returning `None` is not a safe generic fix. |

## Defer bulk cleanup (56 further occurrences, plus 2 asset assertions above = 58)

| Rule | Count | Decision and evidence |
|---|---:|---|
| `PYL-W0621` | 44 | Local variable names shadow module-level render/build variables: 23 in `build-library.py`, 17 in `build-rain.py`, 1 in `prepare-human.py`, 3 in the TTS smoke helper. Inspected function scope and callers; no accidental read of a shadowed outer value was established. Renaming everything produces churn in art/build scripts without changing behavior. Group entry points into `main()` only as part of a real maintainability task. |
| `PY-R1000` | 4 | Complexity in `human_mesh.build_character`, `build_rain.compact_morph_targets`, `build_library.bookcase`, and `academia_outfit.dress`. These implement mesh fitting, glTF packing, room generation and clothes geometry, respectively. A threshold alone does not identify a defect. Separate pure glTF packing if testing or reuse needs it; do not refactor art scripts just for a score. |
| `PY-W2000` | 2 | `translate_legal.py:10,12` imports unused `Path` and `API_URL`. Valid tiny cleanup, no user impact. Remove next time the file changes. |
| `PTC-W0048` | 1 | `translate_legal.py:61` nested page-kind/notice checks can be merged. Existing branch behavior is correct and clear. Preference only. |
| `PYL-W0714` | 1 | `translate_ui.py:81` lists both `ValueError` and its subclass `JSONDecodeError` in one exception tuple. Confirmed subclass relation at runtime. Remove the redundant subclass when touching retries; existing behavior is unchanged. |
| `TYP-005` | 1 | `translate_ui.py:95` returns parsed tree and text map. `findtext(..., default="")` at line 92 returns text, the supplied empty default, or empty text: no observed runtime type failure. The local catalogue produced 661 values, all strings. Exact analyzer inference details were absent from this export; do not fabricate a bug or add casts blindly. If desired, reproduce with the same checker before improving explicit return/value annotations. [ElementTree findtext contract](https://docs.python.org/3/library/xml.etree.elementtree.html#xml.etree.ElementTree.Element.findtext). |
| `TYP-050` | 1 | `translate_ui.py:124` passes the string dictionary batch to a parameter annotated `dict[str, object]`. Mutable generic dictionary invariance is a likely checker cause; the value is valid JSON input at runtime. Adjust the payload annotation to its actual read-only/serialization contract during localization work, after obtaining the detailed diagnostic. Do not change values merely to satisfy inferred types. |
| `TYP-060` | 1 | `build-rain.py:325` reuses names for the optional portrait-light tuples, including a `size` previously used for integer texture dimensions (line 148). Numeric light sizes include 2.5; previous texture work has already completed. This is at most a variable-name/type-inference cleanup, not a demonstrated runtime defect. |
| `TYP-068` | 1 | `build-library.py:221` initializes `clusters=[]` before appending lists of vectors for chandelier light placement. Explicit `list[list[Vector]]` can aid the checker. Existing cluster construction/use at lines 225–229 is coherent. |

## Verification actually performed

- Read every Python/shell occurrence and its surrounding default-branch flow; no changes to project files.
- Compared the five non-avatar source files between default and local tree: identical.
- Safe isolated compile/execution of the actual top-level smoke assertions: invalid input fails normally; all assertions are removed at optimization level 2. No production script execution.
- `bash -n scripts/run-browser-tests.sh`: passed. Isolated existing regex: accepted localhost, case-insensitive localhost, IPv4 and bracketed IPv6 loopback origins; rejected an external host, a non-origin path, `file:` URL and `localhost.evil.com`.
- Read-only localization `entries` on the local resource file: 661 entries; every value was a string. No translations/API calls or files written.
- Scanned checked-in default-branch asset manifests: 22 human-source URLs and 44 library-source URLs, all HTTPS.
- Confirmed `issubclass(json.JSONDecodeError, ValueError)`.
- Blender, bpy, NumPy and mypy are not installed in the active Python environment. Full asset builds, NumPy runtime tests and analyzer-specific type checking were **not** run. NumPy conclusion verified against its primary documentation.

## Complete occurrence ledger

All references below identify the reviewed default-branch commit; repeated line numbers represent distinct reported occurrences and are intentionally retained.

### BAN-B101 — 7

- `scripts/avatar/build-rain.py`: [267](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L267), [279](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L279)
- `scripts/verify-fractional-tts.py`: [42](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L42), [43](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L43), [57](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L57), [59](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L59), [62](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L62)

### BAN-B310 — 4

- `scripts/avatar/build-library.py`: [22](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L22)
- `scripts/avatar/prepare-human.py`: [16](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/prepare-human.py#L16), [20](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/prepare-human.py#L20)
- `tools/localization/translate_ui.py`: [65](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/tools/localization/translate_ui.py#L65)

### BAN-B314 — 1

- `tools/localization/translate_ui.py`: [90](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/tools/localization/translate_ui.py#L90)

### BAN-B405 — 1

- `tools/localization/translate_ui.py`: [15](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/tools/localization/translate_ui.py#L15)

### BAN-B607 — 4

- `scripts/cleanup-retired-tts-cache.py`: [22](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/cleanup-retired-tts-cache.py#L22), [29](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/cleanup-retired-tts-cache.py#L29)
- `scripts/verify-fractional-tts.py`: [13](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L13), [48](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L48)

### PTC-W0048 — 1

- `tools/localization/translate_legal.py`: [61](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/tools/localization/translate_legal.py#L61)

### PTC-W0063 — 7

- `scripts/avatar/build-library.py`: [32](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L32), [40](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L40), [123](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L123), [236](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L236)
- `scripts/avatar/build-rain.py`: [91](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L91)
- `scripts/avatar/human_mesh.py`: [129](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/human_mesh.py#L129), [130](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/human_mesh.py#L130)

### PY-R1000 — 4

- `scripts/avatar/academia_outfit.py`: [13](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/academia_outfit.py#L13)
- `scripts/avatar/build-library.py`: [128](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L128)
- `scripts/avatar/build-rain.py`: [252](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L252)
- `scripts/avatar/human_mesh.py`: [63](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/human_mesh.py#L63)

### PY-W2000 — 2

- `tools/localization/translate_legal.py`: [10](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/tools/localization/translate_legal.py#L10), [12](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/tools/localization/translate_legal.py#L12)

### PYL-C1802 — 1

- `scripts/avatar/build-rain.py`: [292](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L292)

### PYL-W0102 — 1

- `scripts/avatar/build-rain.py`: [252](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L252)

### PYL-W0621 — 44

- `scripts/avatar/build-library.py`: [31](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L31), [32](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L32), [38](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L38), [39](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L39), [39](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L39), [40](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L40), [57](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L57), [59](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L59), [65](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L65), [65](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L65), [65](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L65), [65](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L65), [68](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L68), [121](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L121), [136](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L136), [138](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L138), [139](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L139), [152](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L152), [177](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L177), [180](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L180), [214](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L214), [215](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L215), [216](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L216)
- `scripts/avatar/build-rain.py`: [47](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L47), [48](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L48), [63](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L63), [65](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L65), [73](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L73), [76](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L76), [77](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L77), [264](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L264), [267](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L267), [269](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L269), [275](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L275), [282](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L282), [286](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L286), [288](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L288), [290](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L290), [305](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L305), [310](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L310)
- `scripts/avatar/prepare-human.py`: [7](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/prepare-human.py#L7)
- `scripts/verify-fractional-tts.py`: [26](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L26), [27](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L27), [32](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/verify-fractional-tts.py#L32)

### PYL-W0714 — 1

- `tools/localization/translate_ui.py`: [81](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/tools/localization/translate_ui.py#L81)

### SH-3015 — 1

- `scripts/run-browser-tests.sh`: [32](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/run-browser-tests.sh#L32)

### TYP-005 — 1

- `tools/localization/translate_ui.py`: [95](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/tools/localization/translate_ui.py#L95)

### TYP-050 — 1

- `tools/localization/translate_ui.py`: [124](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/tools/localization/translate_ui.py#L124)

### TYP-060 — 1

- `scripts/avatar/build-rain.py`: [325](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-rain.py#L325)

### TYP-068 — 1

- `scripts/avatar/build-library.py`: [221](https://github.com/gusbo9233/Glosify-.net/blob/ef9cc38e6806531f7cd586e24c04460b2384f79e/scripts/avatar/build-library.py#L221)
