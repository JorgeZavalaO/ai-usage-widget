# AI Usage Widget

Small always-on-top Windows widget showing the usage windows of your AI subscriptions:

| Provider | Windows |
|---|---|
| OpenCode Go | 5h rolling · weekly · monthly (billing cycle) |
| ChatGPT / Codex | 5h · weekly |
| Claude | 5h · weekly (plus Opus/Sonnet windows when the plan exposes them) |

Each provider also shows local token consumption for today and the last 7 days
(cost in USD where a source exists, which today is OpenCode only).

## Why WPF instead of Electron

The original design used Electron. The requirement is minimum resource usage, and a
Chromium runtime for three progress bars is the wrong trade:

| | Electron | This widget (WPF, .NET Framework 4.8) |
|---|---|---|
| Installer | ~120 MB | ~66 KB executable |
| Private memory | ~150–250 MB | ~51 MB |
| Idle CPU | ~0.5 % | ~0 % (no measurable delta over 30 s) |
| Runtime dependency | bundled Chromium | .NET Framework 4.8 (built into Windows 10/11) |

There is no package manager involved: `build.ps1` calls the Roslyn compiler that
already ships with Visual Studio Build Tools (falling back to the in-box csc).

## Build and run

```powershell
.\build.ps1          # compiles src/**.cs into dist\AIUsageWidget.exe
.\build.ps1 -Run     # build and launch
.\build.ps1 -Clean   # wipe dist first
```

Run it once and it stays in the tray. Left-click the tray icon to toggle the card.

## Configuration

`%APPDATA%\AIUsageWidget\config.json` — thresholds, refresh intervals, autostart,
window position, provider toggles.

`%APPDATA%\AIUsageWidget\widget.log` — errors only, capped at 256 KB. No tokens,
no headers, no response bodies.

### OpenCode Go key

Resolved in this order:

1. The key you paste in **Settings** (encrypted with Windows DPAPI for the current
   user; never written in plaintext).
2. The `OPENCODE_API_KEY` environment variable.
3. **Automatic**: the running OpenCode v2 service stores it in its credential
   store, which the widget reads over the local loopback API using the service
   registration file (`%USERPROFILE%\.local\state\opencode\service.json`). No
   process is spawned and nothing leaves the machine.
4. Legacy `%USERPROFILE%\.local\share\opencode\auth.json` (may be expired).

### Codex and Claude

Read-only access to the CLI credential files:

- `%USERPROFILE%\.codex\auth.json`
- `%USERPROFILE%\.claude\.credentials.json`

**The widget never refreshes OAuth tokens and never writes to those files.**
Refreshing rotates the refresh token and would break the CLI login. When a token is
expired the card says so and tells you which CLI to open once.

## Data sources

| Provider | Quota | Local consumption |
|---|---|---|
| OpenCode Go | `GET https://opencode.ai/zen/go/v1/usage` | `opencode stats --days N --json` |
| Codex | `GET https://chatgpt.com/backend-api/wham/usage` | `%USERPROFILE%\.codex\sessions\**\rollout-*.jsonl` (`token_count` events) |
| Claude | `GET https://api.anthropic.com/api/oauth/usage` (prefers the `limits[]` array) | `%USERPROFILE%\.claude\projects\**\*.jsonl` (`message.usage`) |

Recorded response shapes live in `test/fixtures/` so a provider-side change can be
diffed against reality. The fixtures contain usage numbers only, no credentials.

Notes:

- OpenCode's monthly window resets on the **billing cycle** date, not on the 1st.
  The widget shows the real date.
- `experimental.session.stats` on OpenCode v2.1.4 ignores `from`/`to`, so
  per-period consumption comes from the CLI command instead.
- The 11 GB `opencode.db` is never opened.

## Resource behaviour

- Quota poll every 5 minutes, local consumption every 30 minutes (both configurable).
- Backoff: 1 → 2 → 4 → 8 → 15 minutes on failure; 30 minutes after a `401`.
- HTTP connections are not kept alive (`KeepAlive = false`), so idle sockets are zero.
- File scans are cached by `(path, size, mtime)`; a refresh with no new data reads
  nothing.
- The renderer rebuilds only when a snapshot changes. Countdowns update in place.

## Development

```
src/
  Program.cs                  entry point, single instance, lifecycle
  Model.cs                    shared data model
  Json.cs / Http.cs           in-box JSON + HttpWebRequest helpers
  Config.cs                   config, DPAPI, autostart, logger
  AppState.cs                 refresh scheduler, backoff, snapshot store
  Providers/Quota.cs          the three quota adapters + OpenCode v2 key lookup
  Providers/Consumption.cs    local token aggregation (cached)
  UI/MainWindow.cs            card, bars, countdowns, collapse
  UI/Tray.cs                  tray icon, tooltip, menu
  UI/SettingsWindow.cs        key entry and toggles
  UI/Screenshot.cs            dev-only visual capture
```

Debug flags:

```powershell
.\dist\AIUsageWidget.exe --hidden
.\dist\AIUsageWidget.exe --screenshot=out.png
.\dist\AIUsageWidget.exe --screenshot-settings=out.png
```

`--screenshot` also writes `out.png.tree.txt` with the laid-out visual tree, which
is how the ClearType-on-transparent rendering artifact was diagnosed.

## Known limits

- Cost in USD is only available for OpenCode. Codex and Claude show tokens.
- Claude's response shape is undocumented; parsing prefers `limits[]` and falls
  back to the legacy `five_hour` / `seven_day` fields.
- The OpenCode auto-detect needs the v2 background service to be running; otherwise
  paste the key once in Settings.
- Tested on Windows 11. Windows 10 should work (same framework) but is untested.
