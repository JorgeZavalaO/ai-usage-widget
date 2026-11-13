# Release 1.0 implementation evidence

Review/implementation date: 2026-11-13.
Plan: `docs/release-1.0-plan.md`.

This document distinguishes what was implemented, what was verified automatically,
what was verified manually, and what remains unverified. It is not a claim that
release 1.0 is ready.

## Baseline (W00)

| Item | Value |
|---|---|
| Branch | `main` |
| HEAD before work | `9a7eac7` |
| Pre-existing modified | `README.md`, `build.ps1` (icon embedding) |
| Pre-existing untracked | `.atl/`, `assets/`, `tools/`, `docs/` |
| OS | Windows 11, build `26300` |
| PowerShell | 5.1.26100.9818 |
| Compiler | `C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe` |
| Reference assemblies | `.NETFramework\v4.0` only (no 4.8 targeting pack installed) |
| Runtime requirement | .NET Framework 4.8 (registry Release check) |
| Baseline build | `.\build.ps1` succeeded, 114.5 KB (pre-change) |
| Post-change build | 142 KB |

The pre-existing icon changes were preserved. The review checkout contained no
executable test suite; `test/` only held three JSON fixtures.

Note: the local machine has only the v4.0 reference-assembly folder. Roslyn binds
`mscorlib` to the installed 4.8 runtime, so the produced executable requires .NET
Framework 4.8. CI is expected to use the 4.8 Developer Pack; this local
configuration is a documented environment caveat, not a claim that v4.0 is the
target framework.

## Implemented units

### W01 - deterministic offline tests

Status: verified.

Files: `test/TestFramework.cs`, `test/TestSupport.cs`, `test/*Tests.cs`,
`test/run.ps1`, `tools/toolchain.ps1`.

- Named tests with assertions, temporary directories, cleanup, per-test duration,
  and a nonzero failure exit code.
- Narrow seams: `IClock`, `IHttpTransport`, `IProcessRunner`, `IConsumptionService`,
  `IAutostart`, `ConfigStore(directory)`, `FetchContext.Home`, and `--config-dir`.
- Production build compiles only `src/**/*.cs`; the test build adds `test/*.cs` and
  uses `/main:AiUsageWidget.Tests.TestRunner`.
- Fixtures are resolved by walking up from the test executable, not the caller's
  working directory; `run.ps1` invokes the suite from `%TEMP%`.
- `.\test\run.ps1 -SelfTest` produces a failing assertion and exits 1.

Command/result:

```text
.\test\run.ps1
113 passed, 0 failed, 113 total in 14.9s
```

### W02 - bounded subprocess execution

Status: verified offline.

Files: `src/ProcessRunner.cs`, `src/Providers/Consumption.cs`, `test/ProcessRunnerTests.cs`.

- Replaced `StandardOutput.ReadToEnd()`-before-wait with concurrent stdout/stderr
  reader threads and a 50 ms wait loop.
- Overall deadline, cancellation, and bounded output (2 MB). The process is killed
  through a Windows job object configured with `KILL_ON_JOB_CLOSE`, so descendants
  of `cmd.exe` do not survive. A direct `Process.Kill()` fallback covers systems
  where job assignment fails.
- CLI resolution prefers `%APPDATA%\npm` then absolute `PATH` entries; empty and
  relative entries are ignored (`OpenCodeLocator.ResolveFrom` test).
- Application cancellation is passed to the runner; a timed-out or canceled
  consumption fetch cannot block later refreshes.

Regression cases: normal JSON, nonzero exit, missing executable, empty output,
hung child with stdout open, stdout and stderr floods, cancellation, wrapper
spawning a child (PID proven gone), spaces/non-ASCII paths, arguments with spaces.

### W03 - refresh scheduling and backoff

Status: verified offline.

Files: `src/AppState.cs`, `test/SchedulerTests.cs`.

- One active run at a time; pending quota/consumption work is coalesced instead of
  dropped.
- `Refreshing` cleared in `finally`; subscriber exceptions are isolated.
- Authentication backoff (30 min) applies to returned `StaleAuth` and thrown 401.
  A 403 is transient, not automatically "expired authentication".
- Transient sequence is 1, 2, 4, 8, 15 minutes; success resets it.
- Next quota wake-up is pulled earlier by the earliest provider backoff, so the
  sequence is observable with a 5-minute poll.
- Manual refresh bypasses backoff once; timer and countdown refreshes do not.
- A blocked provider keeps its previous status/message, so an auth error is not
  replaced by a generic "backoff" note.
- Configuration is cloned per run; disposal cancels and prevents later callbacks.
- Consumption collection is skipped when `ShowConsumption` is false.

Regression cases: returned/throw auth states, 403 transient, full delay sequence,
success reset, coalescing, exception cleanup, disposal during work, repeated
`ScheduleTimers`, subscriber failure, stable config snapshot, per-provider
independence, last attempt vs last success.

### W04 - provider parsing contracts

Status: verified offline; live compatibility unverified.

Files: `src/Providers/QuotaParser.cs`, `src/Providers/Quota.cs`, `src/Json.cs`,
`test/QuotaParsingTests.cs`, `test/JsonTests.cs`.

- Parsing is separated from credentials/network and covered offline.
- Claude `limits[]`, legacy fields, fallback when `limits[]` has no usable window,
  unknown kinds, duplicate kinds, malformed JSON, empty windows.
- Codex primary/secondary, partial windows, unsupported plan, malformed JSON.
- OpenCode three windows, billing-cycle flag, invalid percentages, out-of-range
  reset timestamps.
- Non-finite numeric values are rejected; percentages are clamped; oversized
  window durations cannot create invalid UI geometry.
- Credential files tolerate missing, locked, malformed, and partially replaced
  content without modification.
- `JavaScriptSerializer` is instantiated per operation; a concurrent round-trip
  test covers UI saves and background reads.

### W05 - auditable consumption totals

Status: verified offline.

Files: `src/Providers/Consumption.cs`, `src/Model.cs`, `test/ConsumptionTests.cs`.

- Local calendar semantics: today, today + previous 6 dates, today + previous 29
  dates. Boundary tests cover ages 0, 6, 7, 29, 30, future dates, month/year
  changes, and a US DST transition.
- OpenCode CLI semantics verified locally with v2.3.0:
  `--days 0` = today, `--days 7` = 7 local dates, `--days 30` = 30 local dates
  (inspection of `range.from`; aggregate numbers were not committed).
- Token semantics: Codex total = input + output (cached/reasoning are subsets);
  Claude total = input + output + cache read + cache write; OpenCode total = sum of
  the separately reported buckets.
- Duplicate Claude message IDs and repeated identical Codex events count once.
  Missing timestamps are skipped, not attributed to today.
- Malformed and partially written final lines do not discard earlier records.
- Cache hit/invalidation/pruning/rotation tests; deleted files are removed.
- Directory enumeration catches per-directory access errors and materializes the
  file list inside the guard; a direct access-error regression test covers the
  error path.
- `NoData`, `Unavailable`, and `Failed` are distinct; the UI/app keeps prior data
  marked stale only for a failure, and removes it when the source disappears.

### W06 - settings persistence and failure reporting

Status: verified offline.

Files: `src/Config.cs`, `src/UI/SettingsWindow.cs`, `src/UI/Tray.cs`,
`src/UI/MainWindow.cs`, `test/ConfigTests.cs`.

- `File.Replace` with `config.json.bak`; first save uses `File.Move`; temp files are
  uniquely owned and removed on failure.
- Save returns `SaveResult`; failure rolls `Current` back to the last committed
  clone so callers cannot claim success.
- Corrupt/future-schema files are preserved as `config.json.corrupt-<timestamp>`;
  the loader tries the backup, then defaults.
- Validation covers intervals, finite coordinates, threshold ordering, and schema
  version.
- Launch-at-login is a single unit: save then registry; failure rolls back. The
  settings dialog reconciles against the actual Run value and repairs a moved
  executable path.
- Logging starts before configuration load in `Program`.
- The mojibake settings title was confirmed and replaced with `AI Usage - Settings`.

Regression cases: first save, replace, reload, locked destination, directory path
that is a file, concurrent saves, corrupt/backup/future schema, invalid numbers,
DPAPI round trip, invalid protected key, autostart failure rollback, temp-dir
isolation, log location.

### W07 - freshness and desktop lifecycle

Status: implemented; automated/partial manual verification only.

Files: `src/AppState.cs`, `src/Model.cs`, `src/UI/MainWindow.cs`, `src/UI/Tray.cs`,
`src/UI/SettingsWindow.cs`, `src/UI/DesktopPlacement.cs`, `src/Program.cs`.

- `LastAttempt`, `LastSuccessfulRefresh`, per-snapshot `FetchedAt`, `LastAttemptAt`,
  `FromCache`, and `IsStale`.
- Footer uses the last successful fetch and appends `retrying`; cards show the last
  success age for non-OK states. "Updated" is never used for a failed attempt.
- Saved placement intersects a real monitor working area; `EnsureVisible` runs on
  the UI tick and when showing from the tray.
- Glyph buttons are focusable, keyboard-activatable, and have automation names and
  a visible focus border. Status remains textual (percent + message), not color-only.
- Screenshot smoke runs used providers disabled and `--config-dir` under `%TEMP%`,
  so no live credentials or provider requests were used.

Smoke evidence:

```text
.\dist\AIUsageWidget.exe --config-dir=<temp> --screenshot=<temp>\widget.png
exit=0; PNG and visual tree produced; footer is "All providers disabled in Settings"
.\dist\AIUsageWidget.exe --config-dir=<temp> --screenshot-settings=<temp>\settings.png
exit=0; PNG shows title, version, key controls, and toggles
.\dist\AIUsageWidget.exe --screenshot=<temp>\default.png
exit=0; an isolated temp config with all providers disabled is created automatically
```

A screenshot run without `--config-dir` does not read the real configuration or
contact providers: it creates a per-process temporary configuration with providers
and consumption disabled.

Not verified: mixed-DPI moves, monitor disconnection/resolution changes, suspend/
resume, hidden-start visibility on a machine without an existing instance, repeated
show/hide, and tray presence. See the manual matrix.

### W08 - credential transport hardening

Status: verified offline; TLS/CRL runtime behavior unverified.

Files: `src/Http.cs`, `src/Providers/Quota.cs`, `test/HttpTests.cs`.

- OpenCode service URL must be loopback HTTP(S), without userinfo/query/fragment;
  otherwise Basic credentials are not attached.
- Redirects are manual and same-origin only; cross-origin redirects are rejected
  before a second request. Tested against real loopback `TcpListener` servers.
- `CheckCertificateRevocationList = true`; TLS verification is not bypassed.
- Caller cancellation aborts `HttpWebRequest`; timeouts and response size (2 MB)
  are bounded.
- Log test uses a sentinel secret and proves only the provider/error code is logged.
- Local service requests bypass the system proxy.

### W09 - reproducible build and CI

Status: locally verified; CI authored but not yet executed on GitHub.

Files: `build.ps1`, `tools/toolchain.ps1`, `test/run.ps1`,
`.github/workflows/ci.yml`, `README.md`.

- Compiler discovery uses `vswhere`, then VS globs, then the in-box compiler, with
  actionable errors.
- Reference assemblies are discovered highest-first; UTF-8 response files replace
  ASCII (non-ASCII checkout paths).
- `-deterministic+` is used when Roslyn is available.
- CI runs build, offline tests, the intentional-failure self-test, and a build/test
  from an accented path with spaces under `%RUNNER_TEMP%`, with minimal
  `contents: read` permissions and a pinned checkout action SHA.

Local non-ASCII path verification:

```text
target=<temp>\tést repo with space 75e2ce
Built ...\dist\AIUsageWidget.exe (142 KB)
113 passed, 0 failed, 113 total in 15.0s
```

### W10 - portable release package

Status: package generated; publication blocked.

Files: `src/AppInfo.cs`, `src/app.manifest`, `CHANGELOG.md`, `tools/release.ps1`,
`README.md`.

- `AppInfo.Version` is the single release version; assembly/file/product metadata
  and the settings dialog use it. The manifest identity must equal `1.0.0.0` and is
  enforced by a test.
- `tools/release.ps1` builds, tests, stages only the executable/docs/build info,
  creates the versioned ZIP, and writes SHA-256.
- Package contents: `AIUsageWidget.exe`, `README.md`, `CHANGELOG.md`,
  `BUILD-INFO.txt`.

Generated package:

```text
dist\release\AIUsageWidget-1.0.0-win-x64.zip
SHA-256 5a1f423599b6ad54e469c7457889b300b04dddf98a3f59ae30afb85c763bdc27
```

Regenerate the package after any source change; the hash above is the final build
from this session. `BUILD-INFO.txt` records `9a7eac7-dirty` because the work was
not committed (no commit authorization in this task).

### W11 - validation gate

Status: automated gate passes; manual/soak gates not complete.

## Verification summary

| Command | Result |
|---|---|
| `.\build.ps1` | success, `dist\AIUsageWidget.exe` 142 KB |
| `.\test\run.ps1` | 113 passed, 0 failed |
| `.\test\run.ps1 -SelfTest` | intentional failure, exit 1 |
| `.\tools\release.ps1` | ZIP + SHA-256 generated |
| non-ASCII/space path build + tests | success, 113 passed |
| `--screenshot` smoke | exit 0, PNG/tree produced |
| `--screenshot-settings` smoke | exit 0, PNG/tree produced |

## Manual Windows matrix

| Scenario | Result |
|---|---|
| Windows 11 standard user, no elevation | Partially exercised by screenshot/instance runs; no UAC prompt observed |
| Windows 10 | Not tested; not claimed as supported |
| Single monitor 100% | Screenshot smoke only |
| 150% / 200% scaling | Not tested |
| Two monitors, mixed DPI, negative coordinates, gaps | Not tested |
| Primary monitor change / monitor disconnect | Not tested (placement logic added) |
| Online/offline startup, provider error, auth expiry/recovery, reconnect | Not tested live |
| Hidden startup | Blocked by an already-running local instance during this session; screenshot startup path exercised |
| Settings from tray | Settings window render verified; tray menu click not exercised |
| Second launch | Exits 0 and signals the existing instance (observed) |
| Repeated show/hide | Not tested |
| Shutdown during refresh | Automated disposal test passes |
| Suspend/resume, next-day update | Not tested |
| Portable launch from spaces/non-ASCII path | Build/test path verified; executable launch not separately repeated |
| Move/upgrade/rollback/autostart repair | Logic implemented; not tested end-to-end |

## Resource/soak gate

Not executed. No hidden/visible idle measurement, 24-hour soak, handle/thread
trend, or cache-growth measurement was recorded in this session. README performance
figures are explicitly qualified as historical.

## Open blockers and approvals

1. **License/public distribution**: no license chosen; do not publish or tag until
   the maintainer decides ownership terms.
2. **Live provider checks**: Claude/Codex/OpenCode live responses and auth recovery
   need an authorized supervised session. Fixtures are not a compatibility proof.
3. **Manual desktop matrix**: mixed DPI, monitor removal, suspend/resume, hidden
   startup, tray interactions, and second-instance show behavior remain.
4. **Soak/resources**: 24-hour soak and resource baseline remain.
5. **CI first run**: the workflow is authored and pinned but has not executed in
   GitHub Actions.
6. **Targeting pack**: this machine lacks the 4.8 reference-assembly pack; verify a
   clean 4.8 Developer Pack environment in CI or a second machine.
7. **Signing/distribution**: the package is unsigned; SmartScreen behavior is
   documented but not bypassed.
8. **Icon/tools assets**: `assets/` and `tools/make-icon.ps1` remain untracked and
   must be included in the release commit.

## Rollback boundaries

- W02 (`ProcessRunner`, consumption runner call) can revert independently; tests
  for the old behavior will fail until replaced.
- W03 (`AppState`) is a single behavioral unit; revert it with its scheduler tests.
- W04 (`QuotaParser`, `Json`) and W05 (`Consumption`, token totals) are independent
  of the UI.
- W06 (`ConfigStore`, autostart, settings callers) is independent of providers.
- W08 (`Http` redirect/loopback/size rules) is independent of persistence.
- W09/W10 are build/packaging-only.
- Do not roll back user configuration, registry values, or real credentials from
  tests; tests never touch them.

