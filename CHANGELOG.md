# Changelog

All notable changes to this project are documented here.

## 1.0.0 - 2026-11-13

### Added

- Portable Windows tray widget showing quota windows for OpenCode Go, ChatGPT / Codex, and Claude.
- Local token consumption summary for the current day, the last 7 local dates, and the last 30 local dates.
- Editable OpenCode Go key (DPAPI-protected) plus automatic discovery from a running OpenCode v2 service.
- Offline regression test runner (`.\test\run.ps1`) with synthetic providers, credentials, JSONL logs, subprocesses, and HTTP servers.
- Windows CI workflow that runs the build and offline suite, verifies failing assertions, and builds from a path containing spaces and non-ASCII characters.

### Fixed

- Consumption subprocesses now have a hard deadline, bounded output, cancellation, and Windows job-object cleanup for descendant processes.
- Refresh requests are coalesced instead of dropped; `Refreshing` is cleared in guaranteed cleanup.
- Authentication backoff now applies to returned `StaleAuth` snapshots, not only thrown 401 responses, and the transient delay sequence is 1, 2, 4, 8, 15 minutes.
- `30d` consumption now covers exactly 30 local calendar dates instead of 31.
- Token totals no longer double count Codex cached input or reasoning output; Claude cache buckets remain additive.
- Duplicate Claude message IDs and repeated Codex token events are counted once.
- Records without timestamps are skipped instead of being attributed to today.
- Configuration replacement is atomic on the same volume, keeps a validated backup, rolls back on failure, and reports save errors to the caller.
- Launch-at-login changes roll back when the registry write fails.
- Authenticated HTTP requests no longer follow cross-origin redirects with credentials attached.
- The OpenCode service registration URL is restricted to loopback origins before Basic credentials are attached.
- Data freshness distinguishes the last attempt from the last successful fetch and marks retained consumption as stale.

### Security

- Certificate revocation checking is enabled; TLS verification is not bypassed.
- Response bodies are size-limited and local service requests bypass system proxies.
- Logs contain provider ids and error codes only, never headers, tokens, or credential response bodies.

### Known limitations

- Live provider compatibility was not verified in the automated environment; fixtures are regression inputs, not a compatibility guarantee.
- License and public distribution terms are undecided.
- The manual Windows/desktop matrix (mixed DPI, monitor removal, suspend/resume, second instance) still requires a human run.
- Windows 10 support is not claimed; Windows 11 is the tested contract.
