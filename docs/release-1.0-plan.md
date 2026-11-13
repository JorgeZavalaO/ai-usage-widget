# AI Usage Widget: implementation plan for version 1.0

## Objective and execution contract

Deliver a reliable, lightweight Windows widget whose usage figures are explainable, whose refresh work cannot remain blocked indefinitely, and whose portable release can be reproduced and verified on another computer.

This is an implementation handoff, not evidence that corrections have already been made. Execute the work units in dependency order. Keep fixes, regression tests, and relevant documentation together. Do not claim release readiness until the final gates pass.

### Quick start for the implementing agent

1. Read this entire plan, repository instructions, and the current source.
2. Record `git status --short`, branch, HEAD, toolchain, OS, and baseline build result.
3. Preserve existing modifications and untracked files. Create a feature branch without discarding local changes.
4. Implement W01 first, then reproduce each defect with a failing test before correcting it.
5. Record exact verification results and remaining blockers in `docs/release-1.0-evidence.md`.
6. Stop before publication, tagging, signing, or selecting a license without maintainer authorization.

### Constraints

- Preserve WPF, low resource use, and the current portable delivery model.
- Do not migrate to Electron, another UI framework, or a new runtime as part of this work.
- Do not add providers, historical dashboards, notifications, or automatic updating.
- Do not refresh OAuth tokens or modify the Codex/Claude credential files.
- Never commit credentials, account identifiers, session conversations, or unsanitized API captures.
- Automated tests must not read real CLI credentials, contact production providers, launch the real OpenCode service, modify actual autostart settings, or overwrite the user's configuration.
- Use English for code, UI text, comments, and repository documentation.
- Use Conventional Commits without AI attribution. Do not commit unless the task authorization permits it.
- Existing unrelated changes are not part of an implementation commit.
- Consult current official documentation before introducing or changing Windows/.NET API usage, compiler setup, or CI actions; verify compatibility with the selected runtime.

## Baseline and evidence quality

Review date: 2026-11-13. Inspected HEAD: `9a7eac7`; revalidate this before implementation.

The review inspected source and repository files, but did not compile the app, run the UI, reproduce failures at runtime, or call live provider APIs.

Observed working-tree changes at review time:

- Modified: `README.md`, `build.ps1`.
- Untracked: `.atl/`, `assets/`, `tools/`.
- Build output existed under `dist/`; existing output is not proof that the current checkout builds.

Confirmed source-level findings:

- `test/` contains three quota fixtures, but no executable test suite.
- `RunOpenCodeStats` reads stdout to completion before its timed process wait.
- Auth failures usually return a `StaleAuth` snapshot, while scheduler auth backoff is implemented only in the exception path.
- `QueueRefresh` drops requests while another refresh is running.
- `Refreshing` is cleared on the happy path rather than protected by a cleanup guarantee.
- The `30d` aggregation includes ages 0 through 30, or 31 calendar dates.
- Configuration save deletes the previous file before moving the temporary file; errors are logged but not returned to the caller.
- The footer uses the last quota attempt time, including unsuccessful attempts, as “Updated”.
- No CI workflow, license file, or Git release tags were found in the reviewed checkout.
- The manifest already declares `1.0.0.0`; this is not a coherent application release-version mechanism.

Items requiring investigation, not assumptions:

- Provider-specific token overlap and duplicate-event behavior.
- Shared serializer and mutable configuration access across threads.
- Resource behavior over long runs and cache growth.
- Supported OS/runtime/toolchain combinations.
- Display placement after monitor removal, mixed DPI, and suspend/resume.
- Security policy for local service URLs, redirects, and certificate revocation.

Source line numbers in the initial review are approximate anchors; use symbol names after edits.

## Dependencies and work boundaries

Execution order:

1. W00 baseline.
2. W01 test infrastructure.
3. W02 process lifetime and W03 scheduler reliability.
4. W04 quota parsing and W05 consumption correctness.
5. W06 persistence and settings feedback.
6. W07 freshness/UI/lifecycle and W08 credential transport hardening.
7. W09 reproducible build and CI.
8. W10 release package and W11 validation/handoff.

W04 and W05 may run independently after W01 if their owners avoid simultaneous edits to shared models. W09 depends on the selected W01 build/test strategy. W11 requires all blocking work units.

Prefer one reviewable behavior per commit, with tests and documentation in that commit. Split large units into independently passing slices; do not separate all tests into a later commit. Avoid parallel edits to `AppState.cs`, `Model.cs`, or `build.ps1`.

## W00 — Establish a reproducible baseline

Priority: prerequisite. Files: repository status, `build.ps1`, `README.md`, evidence document.

Actions:

1. Capture branch, HEAD, status, tracked files, available compiler, reference assemblies, and actual runtime.
2. Read existing local changes before editing their files. Do not stage `.atl/` or unrelated assets automatically.
3. Run the existing build without `-Run` or `-Clean`. Record warnings and failures verbatim after sanitization.
4. Inspect the executable metadata and target framework requirements. The README says .NET Framework 4.8 while the build selects v4.0 reference assemblies; resolve this from actual API/compiler requirements, not by changing the README alone.
5. Create an evidence document with baseline, per-unit verification, manual matrix, release gates, and blockers.

Acceptance:

- Baseline failures and pre-existing modifications are distinguishable from changes introduced by this work.
- No live provider requests or destructive clean operations occurred during baseline collection.

## W01 — Introduce deterministic offline tests

Priority: release blocker. Suggested files: `test/run.ps1`, `test/*Tests.cs`, `test/fixtures/`, minimal production test seams.

Recommended direction: a small console test runner compiled with the same toolchain, with assertions and a nonzero failure exit code. This preserves the dependency-free build. If a standard test framework materially improves maintainability, document its restore/toolchain cost before adopting it.

Actions:

1. Add a test entry point with named tests, isolated temporary directories, cleanup, bounded duration, and a summary.
2. Provide narrow seams for time, provider fetching, process execution, configuration location, and HTTP transport only where needed.
3. Keep production defaults unchanged. Prefer constructor arguments or small interfaces over a new dependency injection framework.
4. Ensure the production compiler does not accidentally include test entry points.
5. Use fake providers and synthetic credentials, logs, process scripts, and API bodies.
6. Make fixture paths independent of the caller's working directory.
7. Make cleanup safe after failed assertions; terminate only processes created by that test.

Required initial coverage:

- JSON parsing, null/malformed input, dates, and invalid numeric values.
- Existing provider fixtures and expected normalized windows.
- Basic configuration defaults and a temporary-directory round trip.
- Test runner failure propagation and operation from another working directory.

Acceptance:

- `.\test\run.ps1` is the documented proposed test command and exits nonzero on an intentional failing assertion.
- Tests pass without network connectivity, installed provider CLIs, or real credentials.
- Future defect units add their regression cases to this runner.

## W02 — Bound subprocess execution and cancellation

Priority: release blocker. Files: `src/Providers/Consumption.cs`, process helper if extracted, regression tests.

Defect: `StandardOutput.ReadToEnd()` can block before `WaitForExit(25000)` is reached.

Actions:

1. Reproduce using a synthetic child that keeps stdout open beyond its allowed lifetime.
2. Drain stdout without defeating the deadline; if stderr becomes redirected, drain it concurrently.
3. Apply an overall deadline and cancellation through process start, output collection, exit, and cleanup.
4. Bound output size; reject excessive output without retaining it indefinitely.
5. Clean up the command wrapper and its descendants using an approach supported by the chosen Windows/.NET baseline. Killing only `cmd.exe` is not sufficient evidence.
6. Pass application cancellation to local consumption work. Ensure shutdown does not wait for the full normal refresh interval.
7. Resolve the CLI through supported install locations or PATH without trusting the working directory; retain safe quoting for spaces and non-ASCII paths.

Tests:

- Normal JSON output; nonzero exit; missing executable; empty output.
- Hung process with stdout open; large output; cancellation during execution.
- Wrapper spawning a child, with proof that no owned descendant remains after timeout.
- Executable paths containing spaces and non-ASCII characters.

Acceptance:

- Timeout tests finish within their configured deadline plus a documented small cleanup allowance.
- A timed-out consumption fetch cannot prevent subsequent refreshes.
- No synthetic child survives test completion; no real OpenCode process is touched.

## W03 — Make refresh scheduling and backoff consistent

Priority: release blocker. Files: `src/AppState.cs`, minimal provider/model seams, scheduler tests.

Actions:

1. Centralize outcome classification so returned statuses and exceptions receive consistent retry handling.
2. Apply auth backoff to `StaleAuth`, not only thrown HTTP exceptions.
3. Do not classify every 403 as expired authentication without provider-specific evidence; distinguish entitlement/unsupported cases where justified.
4. Fix the transient delay sequence to match the documented `1, 2, 4, 8, 15` minutes. Existing failure counting starts at 2 minutes.
5. Define precedence for normal polling, backoff, manual refresh, and reset-triggered refresh.
6. Preserve an explicit manual retry path, but prevent countdown-triggered refreshes from repeatedly bypassing auth backoff.
7. Coalesce pending quota/consumption work instead of silently dropping timer requests. Do not create an unbounded queue or overlapping refreshes.
8. Clear refresh state in guaranteed cleanup, including provider, consumption, and subscriber failures.
9. Observe cancellation between providers and prevent callbacks from scheduling work after disposal.
10. Preserve last good data where appropriate without presenting it as newly fetched.
11. Audit shared mutable state/configuration access; capture a consistent configuration snapshot per run where needed.
12. Decide and document whether hiding consumption also disables its background collection; avoid unnecessary work when disabled.

Tests:

- Returned `StaleAuth` and equivalent exceptions; expiry and recovery of backoff.
- Transient delay sequence, success resetting failures, unsupported plan, disabled provider.
- Consumption timer firing during quota work; manual refresh during a run; burst coalescing.
- Exception cleanup, cancellation, disposal during a run, repeated start/schedule calls.
- Expired countdown cannot override automatic auth backoff.

Acceptance:

- One active refresh at a time; bounded pending work; no permanently stuck “Refreshing”.
- Authentication failures do not trigger repeated automatic requests inside the blocked interval.
- Scheduler tests use a controllable clock, not real multi-minute sleeps.

## W04 — Lock down provider parsing contracts

Priority: release blocker. Files: `src/Providers/Quota.cs`, `src/Json.cs`, quota fixtures/tests.

Actions:

1. Separate response normalization from network and credential reads sufficiently to test it offline.
2. Use existing fixtures as regression inputs, not as proof of current provider compatibility.
3. Cover Claude `limits[]` and legacy fields, Codex primary/secondary windows, and OpenCode billing-cycle reset dates.
4. Test partial windows, unknown kinds, omitted fields, malformed JSON, nulls, numeric strings, out-of-range dates, non-finite percentages, empty responses, and non-JSON errors.
5. Specify the fallback when a nonempty Claude `limits[]` contains no usable windows; do not silently report success with no data.
6. Ensure malformed provider data produces a safe, diagnosable status without breaking other providers.
7. Verify credential-file reads tolerate missing, locked, malformed, and partially replaced files without modifying them.
8. Check serializer use across UI saves and background reads; remove unsafe sharing if confirmed by documentation/testing.

Acceptance:

- Every supported response shape has expected normalized values, units, resets, and status assertions.
- Invalid numeric/date input cannot crash the refresh or create invalid UI geometry.
- Independent providers continue updating when another provider fails.
- Live compatibility remains separately marked unverified until authorized manual checks.

## W05 — Make consumption totals auditable

Priority: release blocker. Files: `src/Providers/Consumption.cs`, `src/Model.cs`, synthetic JSONL fixtures/tests.

Actions:

1. Define “today”, “7d”, and “30d” explicitly. Recommended local-file semantics: local calendar today, today plus the previous 6 dates, and today plus the previous 29 dates.
2. Fix the inclusive 31-date range and test boundaries at ages 0, 6, 7, 29, 30, and future dates.
3. Verify OpenCode CLI period semantics using current CLI documentation/output; do not assume `--days 0/7/30` matches local-file semantics. Align them or clearly disclose the difference.
4. Establish field semantics per provider. Determine whether reasoning is included in output and cached input is included in input before calculating `TotalTokens`.
5. Normalize to a documented non-overlapping total. Do not simply sum every field or apply one provider's semantics to all providers.
6. Investigate repeated cumulative counters, duplicate message/event identifiers, streaming updates, and cross-file duplicates using synthetic fixtures and authorized sanitized samples.
7. Where repeated records represent updates, define replacement/delta semantics rather than blindly summing them.
8. Skip or isolate malformed/truncated lines while retaining earlier valid events; a missing timestamp must not silently convert old data into today's consumption.
9. Handle file deletion/rotation, access errors during lazy directory enumeration, and files changing during reads.
10. Prune obsolete cache entries and bound retention. Confirm unchanged cached files are not reopened unnecessarily.
11. Separate “no records”, “zero usage”, and “collection failed”. Do not show old consumption indefinitely as current when a source disappears.

Tests:

- Exact date boundaries, month/year changes, local timezone and daylight-saving boundaries.
- Duplicate events and cumulative/streaming updates with explicitly stated expected totals.
- Token overlap cases, large values, malformed records, absent timestamps.
- Deleted/rotated files, inaccessible subdirectory, partially written final line.
- Cache hit, invalidation, pruning, and failed scan with prior data.

Acceptance:

- Expected totals are derived independently in test explanations, not copied from the implementation.
- No double counting in supported fixture shapes.
- `30d` covers exactly the documented dates, and failures are distinguishable from zero usage.

## W06 — Persist settings safely and report failure

Priority: release blocker. Files: `src/Config.cs`, `src/UI/SettingsWindow.cs`, settings callers in tray/main window, config tests.

Actions:

1. Replace delete-then-move with a documented same-volume safe replacement strategy, including first-save handling and a recoverable backup.
2. Serialize concurrent saves and clean up owned temporary files without deleting a valid prior configuration.
3. Return a structured success/failure result or propagate a controlled exception; never imply persistence succeeded after a logged failure.
4. Keep the last committed in-memory configuration/key consistent with disk. Define rollback or explicit unsaved state.
5. Show a concise actionable error when save, key encryption, decryption, or autostart modification fails.
6. Reconcile launch-at-login UI with the actual registry operation; changing a checkbox must not falsely claim success.
7. Validate finite coordinates, supported intervals, threshold ordering/ranges, and configuration schema versions.
8. Recover corrupt configuration from a validated backup or defaults without silently overwriting evidence needed for recovery.
9. Initialize logging early enough to capture load failures, without leaking configuration content.
10. Check the visibly suspicious settings title encoding in the actual source/runtime and correct it only if confirmed.

Tests:

- First save, replace, reload, missing directory, denied write, injected replacement failure.
- Old valid config remains readable after a failed save.
- Concurrent saves produce a complete valid JSON document.
- Corrupt config, corrupt backup, invalid/future schema, invalid numbers.
- DPAPI round trip using synthetic test secrets on Windows; unavailable DPAPI path.
- Fake autostart failure rolls back or visibly reports unsaved state.

Acceptance:

- No persistence failure is represented as a successful save.
- Existing valid settings survive injected write/replacement failures.
- Tests never touch actual `%APPDATA%\AIUsageWidget` or the real Run key.

## W07 — Expose freshness and validate desktop lifecycle

Priority: core release validation. Files: `src/AppState.cs`, `src/Model.cs`, `src/UI/MainWindow.cs`, `src/UI/Tray.cs`, `src/Program.cs`.

Actions:

1. Distinguish last attempt from last successful fetch, per provider; model consumption freshness separately if needed.
2. Show stale/auth/error states with age or last-success time. Do not label a failed attempt as “Updated”.
3. Make no-credentials and auth recovery instructions actionable without spawning CLIs automatically.
4. Ensure saved placement intersects a real monitor's usable area, not only the virtual desktop bounding rectangle.
5. Recover reachability after monitor disconnection, resolution changes, and mixed-DPI moves.
6. Validate initial hidden startup, settings opened from tray, second-instance activation, repeated show/hide, and exit during refresh.
7. Verify suspend/resume and network recovery do not cause overlapping work or request bursts.
8. Check keyboard navigation, visible focus, accessible names for icon-only controls, and status communication without relying on color alone.
9. Review global UI exception handling: recoverable errors may be handled; do not blindly continue after corrupted state.

Acceptance:

- A stale number is visibly different from fresh data; one healthy provider does not conceal another's failure.
- Widget and settings remain reachable across the manual monitor matrix.
- Exiting removes tray state and owned background work without crashes.
- Evidence records screenshots without account details or credentials.

## W08 — Harden credential transport without broad redesign

Priority: release blocker for confirmed credential exposure. Files: `src/Http.cs`, `src/Providers/Quota.cs`, transport tests.

Actions:

1. Validate the OpenCode service registration URL before attaching Basic credentials. Allow only the intended local loopback transport, explicitly rejecting remote hosts, userinfo, and unsupported schemes.
2. Constrain redirects for authenticated requests; never forward credentials to a different origin. Test actual client behavior rather than relying on assumptions.
3. Review the explicit certificate revocation disable in `Http.cs`. Use a documented secure policy compatible with the selected OS/runtime; record availability/performance tradeoffs.
4. Propagate request cancellation or bounded abort semantics to the chosen HTTP implementation.
5. Bound response sizes and retain finite timeouts for all providers and local service calls.
6. Keep logs free of request headers, tokens, credential responses, local conversation content, and raw exception messages that could expose them.
7. Document the threat boundary: URL validation reduces credential misrouting; it does not protect against a fully compromised local user account.

Tests:

- Allowed loopback endpoints; rejected remote/lookalike hosts and unsafe schemes.
- Same-origin and cross-origin redirects using synthetic authorization values.
- Timeout, cancellation, oversized response, malformed response.
- Log inspection with sentinel secrets ensures no secret was written.

Acceptance:

- Fake secrets are never sent to a rejected destination.
- No TLS verification bypass is introduced.
- Tests use a local fake transport/server, never production credentials.

## W09 — Make build and CI reproducible

Priority: release blocker. Files: `build.ps1`, test script, CI workflow, `README.md`.

Actions:

1. Select and document the actual supported target framework and compiler/reference-assembly discovery.
2. Fail with actionable prerequisites when the toolchain is missing. Do not claim a fallback works without testing it.
3. Remove machine-specific assumptions that prevent a clean supported environment from building.
4. Avoid lossy ASCII response-file paths; verify an encoding supported by the chosen compiler with non-ASCII checkout paths.
5. Make production/test compilation options explicit and consistent.
6. Add Windows CI for build and offline tests on pushes and pull requests, with minimal permissions.
7. Review and pin CI dependencies under the repository's security policy.
8. Upload only sanitized test/build evidence. Do not enable live provider tests in CI.
9. Ensure executable icon inputs required for a release are tracked; do not silently ship a degraded release because a required asset is missing.

Acceptance:

- Clean checkout builds and tests on a documented Windows runner.
- A deliberate compile error or failing assertion fails CI.
- Build succeeds from a checkout path containing spaces and non-ASCII characters.
- README prerequisites match the actual build; no developer-specific path is required.

## W10 — Prepare a coherent portable release

Priority: release packaging; publication requires approval.

Actions:

1. Introduce a single authoritative application version used by assembly/file/product metadata and visible UI.
2. Keep configuration schema version separate from application version.
3. Account for the manifest's existing `1.0.0.0`; remove misleading independently maintained release identifiers.
4. Add changelog/release notes describing supported providers, credential access, limitations, and recovery steps.
5. Package a versioned portable ZIP containing only intended release files; exclude build response files, temporary executables, logs, screenshots, and test data.
6. Produce SHA-256 checksums and record the source commit, build environment, and successful verification commands.
7. Document manual upgrade, rollback, autostart path implications, and removal of autostart/configuration.
8. Request a maintainer license decision. Do not choose ownership terms or insert a license without authorization.
9. Treat signing as a distribution decision: document unsigned executable warnings if signing is unavailable; do not promise reputation or bypass Windows protections.
10. Create/push a release tag and publish artifacts only after explicit approval and all gates pass.

Acceptance:

- Executable metadata, visible version, release notes, and archive agree.
- Portable package starts on a separate supported machine without compiler tools installed.
- Checksums match the delivered artifacts; rollback instructions work.
- Public distribution is blocked until licensing/distribution approval is resolved.

## W11 — Execute the validation matrix and release gate

### Automated gate

All of the following must pass with exact commands and results recorded:

- Production build and offline test suite.
- Process timeout/cleanup and scheduler/auth-backoff regressions.
- Quota parsing and consumption correctness fixtures.
- Persistence failure/recovery tests.
- Transport destination/secret-redaction tests.
- Clean Windows CI build and tests.

Do not substitute a coverage percentage or successful compilation for behavioral assertions.

### Manual Windows matrix

Record OS version/build, architecture, runtime, app version, scenario, expected result, observed result, and evidence.

- Windows 11 on a clean standard-user account; no elevation.
- Windows 10 only if included in the supported release contract. If unavailable, explicitly exclude it rather than claiming support.
- Single monitor at 100%, 150%, and 200% scaling.
- Two monitors with mixed DPI, negative coordinates, monitor gaps, primary-monitor change, and monitor disconnection.
- Online startup, offline startup, provider error, auth expiry/recovery, and reconnect.
- Hidden startup, settings from tray, second launch, repeated toggle, and shutdown during refresh.
- Suspend/resume and next-day consumption update.
- Portable launch from spaces/non-ASCII paths; move/upgrade/rollback and autostart repair.

Live provider checks require authorization and a test account or the maintainer's supervised session. Record normalized numbers and timestamps only. If provider figures change between observations, compare the same source/time window before calling it a defect.

### Resource and soak gate

1. Measure hidden idle and visible idle after warm-up; record sampling method and hardware.
2. Compare private memory, CPU, handles, thread count, cache entries, and owned subprocess count against baseline.
3. Run repeated refresh/show-hide cycles and at least a 24-hour soak spanning a day boundary; include disconnect/reconnect and suspend/resume.
4. Assert no monotonic resource growth, orphan subprocesses, permanent refreshing state, or uncontrolled retry loop.
5. Replace README performance claims with reproducible measurements or qualify them as historical. Do not enforce the old 66 KB/51 MB figures without a current baseline.

### Definition of done

- [ ] All W02–W06 blocking defects have regression tests and verified fixes.
- [ ] Credential transport findings are resolved or explicitly block release.
- [ ] No open critical/high-severity correctness, credential, or stability defect.
- [ ] Offline suite and clean CI pass.
- [ ] Supported Windows/desktop scenarios pass; unsupported combinations are disclosed.
- [ ] Consumption totals reconcile with independently derived fixture expectations.
- [ ] Manual live checks are recorded, or lack of access is an explicit release blocker.
- [ ] Soak/resource evidence is attached.
- [ ] Package/version/checksums and upgrade/rollback instructions are verified.
- [ ] License and publication decisions are approved before public distribution.
- [ ] Remaining lower-risk limitations have owner-approved rationale.

## Evidence, rollback, and final agent handoff

For each work unit, append:

```text
Unit:
Status: not started | in progress | verified | blocked
Source commit / dirty-tree state:
Files changed:
Reproduction and expected failure:
Fix rationale:
Focused test command and exact result:
Full suite/build command and exact result:
Runtime scenario and result, or reason not applicable:
Security/privacy checks:
Rollback boundary:
Remaining limitations / approval needed:
```

Rollback policy:

- Keep each behavior change isolated enough to revert without undoing unrelated fixes.
- Do not roll back user settings or registry values from tests; tests must not touch them.
- Before a manual upgrade/config migration, obtain consent and preserve a recoverable backup.
- A reverted unit loses its verified status until dependent checks run again.

Final reply from the implementing agent must distinguish implemented changes, automated verification, manual verification, and unverified claims. Include evidence paths, remaining blockers, and the next required maintainer decision. Never mark 1.0 ready solely because all task checkboxes were edited.

## Suggested review slices

- `test: add isolated offline regression harness` — W01.
- `fix: bound consumption subprocess lifetime` — W02 and tests.
- `fix: coalesce refresh work and honor auth backoff` — W03 and tests.
- `fix: validate quota response normalization` — W04 and tests.
- `fix: correct consumption periods and token totals` — W05, split by independently verifiable behavior if large.
- `fix: preserve settings on persistence failure` — W06 and tests.
- `fix: expose data freshness and recover desktop placement` — W07, split UI and lifecycle if needed.
- `fix: constrain authenticated transport destinations` — W08 and tests.
- `ci: verify Windows builds and offline tests` — W09.
- `build: prepare versioned portable release artifacts` — W10, without publication.
- `docs: record release validation evidence and limitations` — W11.

These are proposed boundaries, not authorization to commit, create PRs, publish, or tag.
