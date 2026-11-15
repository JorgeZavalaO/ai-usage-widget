# Usage statistics window

Status: shipped in 1.1. The 1.0 plan deliberately excluded "historical dashboards";
this document records the scope change and the methodology, so the numbers in the
window stay explainable.

## What the window shows

- The 30-day total is the headline number and the other metric sits beside it;
  the selected metric is rendered larger.
- 30 local calendar dates, oldest first, as stacked bars per provider.
- Two metrics: **tokens** (input + output) or **cost** (USD), switched with a
  segmented control.
- A per-provider table with both token and cost totals. Each row toggles that
  provider, so a dominant series does not hide the rest.
- A per-day breakdown on hover, or with the arrow keys once the chart has focus.
- The dialog caps itself to the work area and scrolls instead of clipping its
  footer on small or scaled displays.

## Why the token chart excludes cached input

Raw totals are dominated by cache reads. In the reference session used to check
this feature, 7 days of OpenCode usage were:

| Bucket | Tokens |
|---|---|
| input | 38.5M |
| output | 1.5M |
| reasoning | 1.2M |
| **cache read** | **1,515M** |

Cache read was 97.3% of the sum. Plotting the total produces one flat bar per
provider and makes input/output invisible, so the chart draws input + output and
says so in the window. The cards' `30d` summary still reports the non-overlapping
total, which includes cache.

Codex reports cached input inside `input_tokens`, so its billable value is
`(input - cache_read) + output`. Claude reports cache outside `input_tokens`, so
its billable value is `input + output`.

## Daily availability

| Provider | Daily series | Source |
|---|---|---|
| Codex | exact, already parsed | `turn_context.model` + `token_count` events in `~/.codex/sessions/**/rollout-*.jsonl` |
| Claude | exact, already parsed | `message.model` and `message.usage` in `~/.claude/projects/**/*.jsonl` |
| OpenCode Go | reconstructed | `opencode stats --days N --json` |

Codex and Claude are rebuilt from the session files on every scan, so no history
has to be stored. A date without records is a real zero: the files were read end
to end.

## Reconstructing OpenCode days

`opencode stats` answers "the last N days" and cannot enumerate days, and its JSON
`activity[]` array carries steps, not tokens. The CLI does, however, return the
same value for `--days 0` and `--days 1` (both mean today), so:

```
S(k)  = window of today plus k-1 previous dates
day(k-1 ago) = S(k) - S(k-1)
```

That makes a `--days 0..30` sweep an exact 30-day series for tokens and cost. The
sweep costs about 19 seconds of short-lived CLI calls, so it runs:

- only when the statistics window needs the history;
- at most once per local day;
- in the background, with a 75-second budget, and only on an explicit request.

Results are stored in `%APPDATA%\AIUsageWidget\usage-history.json`, one record per
date. Every other refresh only calls `--days 2` to close yesterday exactly: a day
captured live stops updating at midnight, and without that call the chart and the
live 7-day total could disagree by up to one refresh interval.

Missing or provisional past dates are reported as unknown (`DailyIncomplete`), not
drawn as zero usage. Today is always taken from the live call.

## Cost

| Provider | Cost source | Nature |
|---|---|---|
| OpenCode Go | `cost` in `opencode stats --json` | reported by the CLI; zero for subscription-backed models |
| Codex | estimated | list price Ã— tokens per model |
| Claude | estimated | list price Ã— tokens per model |

List prices come from `https://models.dev/api.json`, the catalogue OpenCode itself
uses. The document is about 6 MB, so:

- the response cap for that host is 12 MB (the general cap stays 2 MB);
- only the `anthropic` and `openai` sections are extracted, without parsing the
  rest;
- the filtered catalogue (about 5 KB) is cached for a day in
  `%APPDATA%\AIUsageWidget\pricing.json`;
- a failed fetch keeps the previous cache, and a failed first fetch leaves cost
  unknown rather than wrong.

Pricing is billed per million tokens. Cache reads and cache writes use their own
rates when the catalogue publishes them; otherwise the input rate applies.

Verification against the provider's own arithmetic: `claude-opus-5-5` at
4 / 20 / 0.2 / 5 USD per million for 8 input, 153,596 output, 883,136 cache read
and 7,938 cache write yields **3.2882692**, the exact figure OpenCode reported for
the same session. This is a regression test.

The figure is not an invoice. A ChatGPT or Claude subscription is charged flat, so
the number is the API-equivalent value of the usage. The window labels it as an
estimate, and a `+` marks days where at least one model had no published price
(those tokens are counted but not charged).

## Files

| Path | Role |
|---|---|
| `src/Pricing.cs` | catalogue fetch, filtering, cache, per-model price lookup |
| `src/Providers/UsageHistory.cs` | persisted per-day OpenCode totals |
| `src/Providers/Consumption.cs` | per-day and per-model aggregation, cost estimation, backfill |
| `src/UI/UsageChart.cs` | stacked bar renderer (`FrameworkElement.OnRender`, no chart package) |
| `src/UI/StatisticsWindow.cs` | window, metric toggle, legend filtering, hover breakdown |

## Verification

- `.\test\run.ps1`: 135 passed, 0 failed. The new cases cover price parsing and
  caching, cache-inclusive vs. disjoint billing, per-model attribution from both
  session formats, daily buckets, backfill differencing, the once-per-day guard,
  unknown-vs-zero days, and the chart maths.
- `.\build.ps1`: 185 KB executable, no new dependency and no NuGet restore.
- The window was rendered and reviewed with real local data at both metrics. No
  screenshot with real usage is committed.

## Known limits

- The chart reflects local data only. A provider used from another machine or
  another profile is not counted.
- Reasoning tokens are inside `output_tokens` for Codex and are not charted as a
  separate series.
- Prices are list prices. Discounts, negotiated rates, and flat plans are not
  modelled.


