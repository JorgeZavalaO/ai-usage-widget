using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AiUsageWidget.Providers
{
    internal sealed class TokenTotals
    {
        public long Input;
        public long Output;
        public long Reasoning;
        public long CacheRead;
        public long CacheWrite;
        public long Total = -1;

        public long TotalTokens
        {
            get { return Total >= 0 ? Total : Input + Output + Reasoning + CacheRead + CacheWrite; }
        }

        public void Add(TokenTotals other)
        {
            Input += other.Input;
            Output += other.Output;
            Reasoning += other.Reasoning;
            CacheRead += other.CacheRead;
            CacheWrite += other.CacheWrite;

            // Totals are additive only when every contributor defines a
            // non-overlapping total. The first explicit total seeds the bucket.
            if (other.Total < 0) Total = -1;
            else if (Total < 0) Total = other.Total;
            else Total += other.Total;
        }
    }

    public enum ConsumptionStatus
    {
        Ok,
        NoData,
        Unavailable,
        Failed
    }

    public sealed class ConsumptionResult
    {
        public ConsumptionStatus Status = ConsumptionStatus.NoData;
        public readonly List<Consumption> Items = new List<Consumption>();

        /// <summary>Local daily series for the chart, oldest first.</summary>
        public readonly List<DailyUsage> Daily = new List<DailyUsage>();

        /// <summary>
        /// True when a date in the last 30 is unknown rather than genuinely empty.
        /// </summary>
        public bool DailyIncomplete;

        public string ErrorCode;
        public bool Partial;

        public static ConsumptionResult Ok()
        {
            return new ConsumptionResult { Status = ConsumptionStatus.Ok };
        }

        public static ConsumptionResult NoData(string code)
        {
            return new ConsumptionResult { Status = ConsumptionStatus.NoData, ErrorCode = code };
        }

        public static ConsumptionResult Unavailable(string code)
        {
            return new ConsumptionResult { Status = ConsumptionStatus.Unavailable, ErrorCode = code };
        }

        public static ConsumptionResult Failed(string code)
        {
            return new ConsumptionResult { Status = ConsumptionStatus.Failed, ErrorCode = code };
        }
    }

    public interface IConsumptionService
    {
        ConsumptionResult Fetch(string providerId, FetchContext context, CancellationToken token);
    }

    /// <summary>
    /// Local consumption plane. Best effort: a failure here never affects quota data.
    /// Files are cached by (size, mtime) so a 30-minute refresh re-reads nothing.
    ///
    /// Period semantics are local calendar dates:
    ///   today = today
    ///   7d    = today plus the previous 6 dates
    ///   30d   = today plus the previous 29 dates
    /// </summary>
    public sealed class ConsumptionService : IConsumptionService
    {
        private sealed class FileCache
        {
            public long Size;
            public DateTime MTimeUtc;
            public Dictionary<string, TokenTotals> Days;
            public Dictionary<string, TokenTotals> ByModel;
            public bool HasRecords;
            public bool SkippedNoTimestamp;
            public DateTimeOffset LastSeenUtc;
        }

        private sealed class ParseOutcome
        {
            public readonly Dictionary<string, TokenTotals> Days =
                new Dictionary<string, TokenTotals>(StringComparer.Ordinal);

            /// <summary>Keyed by "yyyy-MM-dd|model" so list prices can be applied.</summary>
            public readonly Dictionary<string, TokenTotals> ByModel =
                new Dictionary<string, TokenTotals>(StringComparer.Ordinal);

            public bool HasRecords;
            public bool SkippedNoTimestamp;
        }

        private const int MaxCacheEntries = 10000;
        private const int ChartDays = 30;
        private const int BackfillBudgetMs = 75000;

        private readonly Dictionary<string, FileCache> _cache =
            new Dictionary<string, FileCache>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheGate = new object();
        private readonly IClock _clock;
        private readonly IProcessRunner _runner;
        private readonly IPriceCatalog _prices;
        private UsageHistory _history;
        private string _historyDirectory;
        private DateTime _backfillAttemptDate = DateTime.MinValue;

        public ConsumptionService(IClock clock, IProcessRunner runner)
            : this(clock, runner, null)
        {
        }

        public ConsumptionService(IClock clock, IProcessRunner runner, IPriceCatalog prices)
        {
            _clock = clock ?? SystemClock.Instance;
            _runner = runner ?? new ProcessRunner();
            _prices = prices;
        }

        public ConsumptionService()
            : this(SystemClock.Instance, new ProcessRunner(), null)
        {
        }

        public int CacheHits { get; private set; }
        public int CacheMisses { get; private set; }

        public int CacheEntries
        {
            get
            {
                lock (_cacheGate)
                {
                    return _cache.Count;
                }
            }
        }

        public void ResetCache()
        {
            lock (_cacheGate)
            {
                _cache.Clear();
                CacheHits = 0;
                CacheMisses = 0;
                _history = null;
                _historyDirectory = null;
                _backfillAttemptDate = DateTime.MinValue;
            }
        }

        public ConsumptionResult Fetch(string providerId, FetchContext context, CancellationToken token)
        {
            try
            {
                if (providerId == "opencode-go") return FetchOpenCode(context, token);
                if (providerId == "codex")
                {
                    return Scan(
                        providerId,
                        Path.Combine(context.ResolveHome(), ".codex", "sessions"),
                        "rollout-*.jsonl",
                        ParseCodexFile,
                        context,
                        token);
                }
                if (providerId == "claude")
                {
                    return Scan(
                        providerId,
                        Path.Combine(context.ResolveHome(), ".claude", "projects"),
                        "*.jsonl",
                        ParseClaudeFile,
                        context,
                        token);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error("consumption/" + providerId + ": " + ex.GetType().Name);
                return ConsumptionResult.Failed("read_failed");
            }

            return ConsumptionResult.Unavailable("unknown_provider");
        }

        // ---------------------------------------------------------------- opencode

        private ConsumptionResult FetchOpenCode(FetchContext context, CancellationToken token)
        {
            var running = context.OpenCodeServiceRunning.HasValue
                ? context.OpenCodeServiceRunning.Value
                : OpenCodeService.IsRunning(context);
            if (!running) return ConsumptionResult.Unavailable("service_not_running");

            var executable = string.IsNullOrEmpty(context.OpenCodeExecutable)
                ? OpenCodeLocator.Resolve()
                : context.OpenCodeExecutable;
            if (executable == null) return ConsumptionResult.Unavailable("cli_missing");

            var result = ConsumptionResult.Ok();
            var failures = 0;

            var todayItem = AddOpenCodePeriod(result, executable, 0, "today", token, ref failures);
            AddOpenCodePeriod(result, executable, 7, "7d", token, ref failures);
            AddOpenCodePeriod(result, executable, 30, "30d", token, ref failures);

            if (result.Items.Count == 0)
            {
                return ConsumptionResult.Failed(failures > 0 ? "stats_failed" : "stats_empty");
            }

            var today = _clock.LocalNow.Date;
            var history = LoadHistory(context);

            if (todayItem != null)
            {
                history.Set(DateKey(today), ToHistoryDay(todayItem, false));
                FinalizeYesterday(executable, history, today, todayItem, token);
            }

            if (context.AllowSlowBackfill && NeedsBackfill(history, today) && CanAttemptBackfill(today))
            {
                try { BackfillOpenCode(executable, history, today, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Log.Error("consumption/opencode history: " + ex.GetType().Name); }
            }

            history.Prune(today.AddDays(-UsageHistory.RetentionDays));
            history.Save();
            BuildOpenCodeDaily(result, history, today);

            result.Partial = failures > 0;
            if (failures > 0) result.ErrorCode = "stats_partial";
            return result;
        }

        private Consumption AddOpenCodePeriod(
            ConsumptionResult target,
            string executable,
            int days,
            string period,
            CancellationToken token,
            ref int failures)
        {
            token.ThrowIfCancellationRequested();

            var item = ReadOpenCodeTotals(executable, days, token);
            if (item == null)
            {
                failures++;
                return null;
            }

            item.Period = period;
            target.Items.Add(item);
            return item;
        }

        /// <summary>
        /// One CLI call. Returns null when the command failed, timed out, or
        /// produced an unparsable payload; callers treat that as a missing sample,
        /// never as zero usage.
        /// </summary>
        private Consumption ReadOpenCodeTotals(string executable, int days, CancellationToken token)
        {
            var json = RunOpenCodeStats(executable, days, token);
            if (json == null) return null;

            var root = Json.ParseObject(json);
            if (root == null) return null;

            var tokens = Json.Obj(Json.Get(root, "tokens"));
            var cache = Json.Obj(Json.Get(tokens, "cache"));

            var item = new Consumption
            {
                CostUsd = Json.Num(Json.Get(root, "cost"), double.NaN),
                Input = Json.Long(Json.Get(tokens, "input")),
                Output = Json.Long(Json.Get(tokens, "output")),
                Reasoning = Json.Long(Json.Get(tokens, "reasoning")),
                CacheRead = Json.Long(Json.Get(cache, "read")),
                CacheWrite = Json.Long(Json.Get(cache, "write"))
            };
            if (!item.CostUsd.HasValue || !Json.IsFinite(item.CostUsd.Value)) item.CostUsd = null;

            // OpenCode exposes prompt, output, reasoning, and cache buckets
            // separately and derives cost from each; the auditable total is the sum.
            item.Total = item.Input + item.Output + item.Reasoning + item.CacheRead + item.CacheWrite;
            return item;
        }

        private string RunOpenCodeStats(string executable, int days, CancellationToken token)
        {
            var arguments = "stats --days " + days.ToString(CultureInfo.InvariantCulture) + " --json";
            var result = _runner.Run(executable, arguments, 25000, token);

            if (result.Canceled) throw new OperationCanceledException(token);
            if (!result.Started || result.TimedOut || result.OutputLimitExceeded) return null;
            if (result.ExitCode != 0) return null;
            return string.IsNullOrWhiteSpace(result.StandardOutput) ? null : result.StandardOutput;
        }

        // ------------------------------------------------------- opencode history

        private UsageHistory LoadHistory(FetchContext context)
        {
            var directory = context.ResolveDataDirectory();
            if (_history == null ||
                !string.Equals(_historyDirectory, directory, StringComparison.OrdinalIgnoreCase))
            {
                _history = UsageHistory.Load(directory);
                _historyDirectory = directory;
            }
            return _history;
        }

        private static string DateKey(DateTime date)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static HistoryDay ToHistoryDay(Consumption item, bool final)
        {
            return new HistoryDay
            {
                Input = item.Input,
                Output = item.Output,
                Reasoning = item.Reasoning,
                CacheRead = item.CacheRead,
                CacheWrite = item.CacheWrite,
                Total = item.TotalTokens,
                CostUsd = item.CostUsd,
                Final = final
            };
        }

        /// <summary>
        /// A day captured while the widget was running stops updating at midnight.
        /// One extra "--days 2" call closes yesterday exactly, so the chart and the
        /// live 7-day total cannot disagree.
        /// </summary>
        private void FinalizeYesterday(
            string executable,
            UsageHistory history,
            DateTime today,
            Consumption todayTotals,
            CancellationToken token)
        {
            var yesterday = DateKey(today.AddDays(-1));
            var stored = history.Get(yesterday);
            if (stored != null && stored.Final) return;

            var window = ReadOpenCodeTotals(executable, 2, token);
            if (window == null) return;

            var day = Difference(window, todayTotals);
            day.Final = true;
            history.Set(yesterday, day);
        }

        private static bool NeedsBackfill(UsageHistory history, DateTime today)
        {
            for (int age = 1; age < ChartDays; age++)
            {
                var day = history.Get(DateKey(today.AddDays(-age)));
                if (day == null || !day.Final) return true;
            }
            return false;
        }

        private bool CanAttemptBackfill(DateTime today)
        {
            return _backfillAttemptDate != today;
        }

        /// <summary>
        /// Reconstructs the last 30 days from consecutive CLI windows:
        /// S(k) covers today plus k-1 previous dates, so day k-1 ago is
        /// S(k) - S(k-1). The CLI answers "--days 0" and "--days 1" with the same
        /// value, which is why the first difference starts at k = 2.
        ///
        /// This runs at most once per day and only when the statistics window asks
        /// for it: it costs about 30 short-lived CLI calls.
        /// </summary>
        private void BackfillOpenCode(
            string executable,
            UsageHistory history,
            DateTime today,
            CancellationToken token)
        {
            _backfillAttemptDate = today;

            var started = _clock.UtcNow;
            Consumption previous = null;

            for (int days = 0; days <= ChartDays; days++)
            {
                if ((_clock.UtcNow - started).TotalMilliseconds > BackfillBudgetMs) break;

                var sample = ReadOpenCodeTotals(executable, days, token);
                if (sample == null) break;

                if (previous != null && days >= 2)
                {
                    var day = Difference(sample, previous);
                    day.Final = true;
                    history.Set(DateKey(today.AddDays(-(days - 1))), day);
                }

                previous = sample;
            }
        }

        private static HistoryDay Difference(Consumption later, Consumption earlier)
        {
            var day = new HistoryDay
            {
                Input = Math.Max(0, later.Input - earlier.Input),
                Output = Math.Max(0, later.Output - earlier.Output),
                Reasoning = Math.Max(0, later.Reasoning - earlier.Reasoning),
                CacheRead = Math.Max(0, later.CacheRead - earlier.CacheRead),
                CacheWrite = Math.Max(0, later.CacheWrite - earlier.CacheWrite)
            };

            day.Total = day.Input + day.Output + day.Reasoning + day.CacheRead + day.CacheWrite;

            if (later.CostUsd.HasValue && earlier.CostUsd.HasValue)
            {
                var cost = later.CostUsd.Value - earlier.CostUsd.Value;
                day.CostUsd = cost < 0 ? 0 : cost;
            }

            return day;
        }

        private static void BuildOpenCodeDaily(ConsumptionResult result, UsageHistory history, DateTime today)
        {
            result.Daily.Clear();
            var incomplete = false;

            for (int age = ChartDays - 1; age >= 0; age--)
            {
                var key = DateKey(today.AddDays(-age));
                var day = history.Get(key);
                var point = new DailyUsage { Date = key };

                if (day == null)
                {
                    // Today is always known once the CLI answers; a missing or
                    // provisional past date means the history is not settled yet.
                    if (age > 0) incomplete = true;
                }
                else
                {
                    point.Input = day.Input;
                    point.Output = day.Output;
                    point.Reasoning = day.Reasoning;
                    point.CacheRead = day.CacheRead;
                    point.CacheWrite = day.CacheWrite;
                    point.Total = day.Total;
                    point.Billable = day.Input + day.Output;
                    point.CostUsd = day.CostUsd;

                    if (age > 0 && !day.Final) incomplete = true;
                }

                result.Daily.Add(point);
            }

            result.DailyIncomplete = incomplete;
        }

        // --------------------------------------------------------------- file scan

        private ConsumptionResult Scan(
            string providerId,
            string root,
            string pattern,
            Func<string, CancellationToken, HashSet<string>, ParseOutcome> parser,
            FetchContext context,
            CancellationToken token)
        {
            if (!Directory.Exists(root)) return ConsumptionResult.Unavailable("source_missing");

            bool enumerateError;
            var files = SafeEnumerate(root, pattern, out enumerateError);

            var days = new Dictionary<string, TokenTotals>(StringComparer.Ordinal);
            var byModel = new Dictionary<string, TokenTotals>(StringComparer.Ordinal);
            var seenEvents = new HashSet<string>(StringComparer.Ordinal);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var anyRecords = false;
            var skippedNoTimestamp = false;
            var hadError = enumerateError;
            var utcCutoff = _clock.UtcNow.AddDays(-32).UtcDateTime;
            var now = _clock.UtcNow;

            for (int i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var file = files[i];

                FileInfo info;
                try { info = new FileInfo(file); }
                catch (Exception) { hadError = true; continue; }

                DateTime mtime;
                long size;
                try
                {
                    if (!info.Exists) continue;
                    mtime = info.LastWriteTimeUtc;
                    if (mtime < utcCutoff) continue;
                    size = info.Length;
                }
                catch (Exception) { hadError = true; continue; }

                seenFiles.Add(file);
                lock (_cacheGate)
                {
                    FileCache cached;
                    if (_cache.TryGetValue(file, out cached) &&
                        cached.Size == size &&
                        cached.MTimeUtc == mtime)
                    {
                        cached.LastSeenUtc = now;
                        CacheHits++;
                        Merge(days, cached.Days);
                        Merge(byModel, cached.ByModel);
                        anyRecords |= cached.HasRecords;
                        skippedNoTimestamp |= cached.SkippedNoTimestamp;
                        continue;
                    }
                    CacheMisses++;
                }

                ParseOutcome parsed;
                try
                {
                    parsed = parser(file, token, seenEvents);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    hadError = true;
                    continue;
                }

                lock (_cacheGate)
                {
                    _cache[file] = new FileCache
                    {
                        Size = size,
                        MTimeUtc = mtime,
                        Days = parsed.Days,
                        ByModel = parsed.ByModel,
                        HasRecords = parsed.HasRecords,
                        SkippedNoTimestamp = parsed.SkippedNoTimestamp,
                        LastSeenUtc = now
                    };
                }

                Merge(days, parsed.Days);
                Merge(byModel, parsed.ByModel);
                anyRecords |= parsed.HasRecords;
                skippedNoTimestamp |= parsed.SkippedNoTimestamp;
            }

            PruneCache(seenFiles, now);

            if (!anyRecords)
            {
                if (hadError) return ConsumptionResult.Failed("read_failed");
                return skippedNoTimestamp
                    ? ConsumptionResult.NoData("missing_timestamp")
                    : ConsumptionResult.NoData(null);
            }

            if (_prices != null)
            {
                try { _prices.EnsureLoaded(token); }
                catch (OperationCanceledException) { }
            }

            var result = ConsumptionResult.Ok();
            Aggregate(result, days, byModel, providerId);
            result.Partial = hadError;
            if (hadError) result.ErrorCode = "read_partial";
            return result;
        }

        private void PruneCache(HashSet<string> seenFiles, DateTimeOffset now)
        {
            lock (_cacheGate)
            {
                var stale = new List<string>();
                foreach (var pair in _cache)
                {
                    if (!seenFiles.Contains(pair.Key))
                    {
                        try
                        {
                            if (!File.Exists(pair.Key)) stale.Add(pair.Key);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }

                for (int i = 0; i < stale.Count; i++) _cache.Remove(stale[i]);

                if (_cache.Count > MaxCacheEntries)
                {
                    var ordered = new List<KeyValuePair<string, FileCache>>(_cache);
                    ordered.Sort((a, b) => a.Value.LastSeenUtc.CompareTo(b.Value.LastSeenUtc));
                    var excess = _cache.Count - MaxCacheEntries;
                    for (int i = 0; i < excess; i++) _cache.Remove(ordered[i].Key);
                }
            }
        }

        private ParseOutcome ParseCodexFile(string path, CancellationToken token, HashSet<string> seenEvents)
        {
            var outcome = new ParseOutcome();
            var model = "unknown";

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                       64 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    token.ThrowIfCancellationRequested();

                    if (line.IndexOf("\"turn_context\"", StringComparison.Ordinal) >= 0)
                    {
                        var contextRoot = Json.ParseObject(line);
                        var contextPayload = Json.Obj(Json.Get(contextRoot, "payload"));
                        var name = Json.Str(Json.Get(contextPayload, "model"));
                        if (!string.IsNullOrEmpty(name)) model = name;
                        continue;
                    }

                    if (line.IndexOf("token_count", StringComparison.Ordinal) < 0) continue;
                    if (line.IndexOf("last_token_usage", StringComparison.Ordinal) < 0) continue;

                    var root = Json.ParseObject(line);
                    var payload = Json.Obj(Json.Get(root, "payload"));
                    if (payload == null) continue;

                    var info = Json.Obj(Json.Get(payload, "info"));
                    if (info == null) continue;

                    var usage = Json.Obj(Json.Get(info, "last_token_usage"));
                    if (usage == null) continue;

                    var date = Json.Date(Json.Get(root, "timestamp"));
                    if (!date.HasValue)
                    {
                        outcome.SkippedNoTimestamp = true;
                        continue;
                    }

                    var input = Json.Long(Json.Get(usage, "input_tokens"));
                    var output = Json.Long(Json.Get(usage, "output_tokens"));
                    var reasoning = Json.Long(Json.Get(usage, "reasoning_output_tokens"));
                    var cacheRead = Json.Long(Json.Get(usage, "cached_input_tokens"));
                    var cacheWrite = Json.Long(Json.Get(usage, "cache_write_input_tokens"));

                    var eventId = Json.Str(Json.Get(payload, "id"));
                    if (string.IsNullOrEmpty(eventId)) eventId = Json.Str(Json.Get(root, "id"));
                    if (string.IsNullOrEmpty(eventId))
                    {
                        eventId = "codex|" + date.Value.UtcTicks + "|" + input + "|" + output + "|" +
                                  reasoning + "|" + cacheRead + "|" + cacheWrite;
                    }
                    if (!seenEvents.Add(eventId)) continue;

                    outcome.HasRecords = true;

                    // input_tokens already includes cached input and output_tokens
                    // already includes reasoning, so they are subsets, not addends.
                    var totals = new TokenTotals
                    {
                        Input = input,
                        Output = output,
                        Reasoning = reasoning,
                        CacheRead = cacheRead,
                        CacheWrite = cacheWrite,
                        Total = input + output
                    };

                    var key = LocalKey(date.Value);
                    Bucket(outcome.Days, key).Add(totals);
                    Bucket(outcome.ByModel, key + "|" + model).Add(totals);
                }
            }

            return outcome;
        }

        private ParseOutcome ParseClaudeFile(string path, CancellationToken token, HashSet<string> seenEvents)
        {
            var outcome = new ParseOutcome();

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                       64 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    token.ThrowIfCancellationRequested();

                    if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) continue;
                    if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) continue;

                    var root = Json.ParseObject(line);
                    if (root == null) continue;

                    var message = Json.Obj(Json.Get(root, "message"));
                    if (message == null) continue;

                    var usage = Json.Obj(Json.Get(message, "usage"));
                    if (usage == null) continue;

                    var date = Json.Date(Json.Get(root, "timestamp"));
                    if (!date.HasValue)
                    {
                        outcome.SkippedNoTimestamp = true;
                        continue;
                    }

                    var messageId = Json.Str(Json.Get(message, "id"));
                    if (string.IsNullOrEmpty(messageId)) messageId = Json.Str(Json.Get(root, "requestId"));
                    if (string.IsNullOrEmpty(messageId)) messageId = Json.Str(Json.Get(root, "uuid"));
                    if (!string.IsNullOrEmpty(messageId) && !seenEvents.Add("claude|" + messageId)) continue;

                    outcome.HasRecords = true;

                    var input = Json.Long(Json.Get(usage, "input_tokens"));
                    var output = Json.Long(Json.Get(usage, "output_tokens"));
                    var cacheRead = Json.Long(Json.Get(usage, "cache_read_input_tokens"));
                    var cacheWrite = Json.Long(Json.Get(usage, "cache_creation_input_tokens"));
                    var model = Json.Str(Json.Get(message, "model"));
                    if (string.IsNullOrEmpty(model)) model = "unknown";

                    // Anthropic reports cache reads/writes outside input_tokens;
                    // they are disjoint billable buckets and must be added.
                    var totals = new TokenTotals
                    {
                        Input = input,
                        Output = output,
                        CacheRead = cacheRead,
                        CacheWrite = cacheWrite,
                        Total = input + output + cacheRead + cacheWrite
                    };

                    var key = LocalKey(date.Value);
                    Bucket(outcome.Days, key).Add(totals);
                    Bucket(outcome.ByModel, key + "|" + model).Add(totals);
                }
            }

            return outcome;
        }

        // ---------------------------------------------------------------- helpers

        private string LocalKey(DateTimeOffset value)
        {
            return _clock.ToLocal(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static TokenTotals Bucket(Dictionary<string, TokenTotals> map, string key)
        {
            TokenTotals totals;
            if (!map.TryGetValue(key, out totals))
            {
                totals = new TokenTotals();
                map[key] = totals;
            }
            return totals;
        }

        private static void Merge(Dictionary<string, TokenTotals> target, Dictionary<string, TokenTotals> source)
        {
            foreach (var pair in source)
            {
                Bucket(target, pair.Key).Add(pair.Value);
            }
        }

        private void Aggregate(
            ConsumptionResult target,
            Dictionary<string, TokenTotals> days,
            Dictionary<string, TokenTotals> byModel,
            string providerId)
        {
            var today = _clock.LocalNow.Date;
            var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var todayTotals = new TokenTotals();
            var weekTotals = new TokenTotals();
            var monthTotals = new TokenTotals();
            var any = false;

            foreach (var pair in days)
            {
                DateTime parsed;
                if (!DateTime.TryParseExact(pair.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out parsed))
                {
                    continue;
                }

                var age = (today - parsed.Date).Days;
                if (age < 0 || age > 29) continue;

                any = true;
                if (pair.Key == todayKey) todayTotals.Add(pair.Value);
                if (age <= 6) weekTotals.Add(pair.Value);
                monthTotals.Add(pair.Value);
            }

            if (!any) return;

            target.Items.Add(ToConsumption("today", todayTotals));
            target.Items.Add(ToConsumption("7d", weekTotals));
            target.Items.Add(ToConsumption("30d", monthTotals));

            // Exactly ChartDays points, oldest first. A date with no records is a
            // real zero for a local file scan: the file was read end to end.
            for (int age = ChartDays - 1; age >= 0; age--)
            {
                var key = DateKey(today.AddDays(-age));

                TokenTotals totals;
                days.TryGetValue(key, out totals);
                if (totals == null) totals = new TokenTotals();

                var point = new DailyUsage
                {
                    Date = key,
                    Input = totals.Input,
                    Output = totals.Output,
                    Reasoning = totals.Reasoning,
                    CacheRead = totals.CacheRead,
                    CacheWrite = totals.CacheWrite,
                    Total = totals.Total,
                    Billable = Billable(providerId, totals)
                };

                EstimateCost(point, byModel, key, providerId);
                target.Daily.Add(point);
            }

            target.DailyIncomplete = false;
        }

        private static long Billable(string providerId, TokenTotals totals)
        {
            if (providerId == "codex")
            {
                // Cached input is inside Input and is not fresh prompt work.
                var fresh = totals.Input - totals.CacheRead;
                if (fresh < 0) fresh = 0;
                return fresh + totals.Output;
            }

            return totals.Input + totals.Output;
        }

        private void EstimateCost(
            DailyUsage point,
            Dictionary<string, TokenTotals> byModel,
            string dateKey,
            string providerId)
        {
            if (byModel == null || byModel.Count == 0 || _prices == null) return;

            var prefix = dateKey + "|";
            var cost = 0.0;
            var priced = false;
            long unpriced = 0;

            foreach (var pair in byModel)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;

                var model = pair.Key.Substring(prefix.Length);
                var totals = pair.Value;

                ModelPrice price;
                if (!_prices.TryGet(providerId, model, out price))
                {
                    unpriced += totals.TotalTokens;
                    continue;
                }

                priced = true;
                cost += providerId == "codex"
                    ? price.CostInclusiveInput(totals.Input, totals.Output, totals.CacheRead)
                    : price.CostDisjoint(totals.Input, totals.Output, totals.CacheRead, totals.CacheWrite);
            }

            point.UnpricedTokens = unpriced;
            if (priced) point.CostUsd = cost;
        }

        private static Consumption ToConsumption(string period, TokenTotals totals)
        {
            return new Consumption
            {
                Period = period,
                CostUsd = null,
                Input = totals.Input,
                Output = totals.Output,
                Reasoning = totals.Reasoning,
                CacheRead = totals.CacheRead,
                CacheWrite = totals.CacheWrite,
                Total = totals.Total
            };
        }

        internal static List<string> SafeEnumerate(string root, string pattern, out bool hadError)
        {
            hadError = false;
            var result = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var directory = stack.Pop();

                try
                {
                    result.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));
                }
                catch (Exception)
                {
                    hadError = true;
                }

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        stack.Push(child);
                    }
                }
                catch (Exception)
                {
                    hadError = true;
                }
            }

            return result;
        }
    }

    internal static class OpenCodeLocator
    {
        private static readonly string[] Names = { "opencode.cmd", "opencode.exe", "opencode.bat" };

        public static string Resolve()
        {
            return ResolveFrom(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetEnvironmentVariable("PATH"),
                File.Exists);
        }

        internal static string ResolveFrom(string appData, string path, Func<string, bool> exists)
        {
            if (!string.IsNullOrEmpty(appData))
            {
                var hit = FindIn(Path.Combine(appData, "npm"), exists);
                if (hit != null) return hit;
            }

            if (string.IsNullOrEmpty(path)) return null;

            var entries = path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i].Trim().Trim('"');
                if (entry.Length == 0) continue;

                // An empty or relative PATH entry would resolve against the
                // process working directory; do not trust it.
                if (!Path.IsPathRooted(entry)) continue;

                var hit = FindIn(entry, exists);
                if (hit != null) return hit;
            }

            return null;
        }

        private static string FindIn(string directory, Func<string, bool> exists)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                try
                {
                    var candidate = Path.Combine(directory, Names[i]);
                    if (exists(candidate)) return candidate;
                }
                catch (Exception)
                {
                }
            }
            return null;
        }
    }
}
