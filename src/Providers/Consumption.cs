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
            public bool HasRecords;
            public bool SkippedNoTimestamp;
            public DateTimeOffset LastSeenUtc;
        }

        private sealed class ParseOutcome
        {
            public Dictionary<string, TokenTotals> Days = new Dictionary<string, TokenTotals>(StringComparer.Ordinal);
            public bool HasRecords;
            public bool SkippedNoTimestamp;
        }

        private const int MaxCacheEntries = 10000;

        private readonly Dictionary<string, FileCache> _cache =
            new Dictionary<string, FileCache>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheGate = new object();
        private readonly IClock _clock;
        private readonly IProcessRunner _runner;

        public ConsumptionService(IClock clock, IProcessRunner runner)
        {
            _clock = clock ?? SystemClock.Instance;
            _runner = runner ?? new ProcessRunner();
        }

        public ConsumptionService()
            : this(SystemClock.Instance, new ProcessRunner())
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
                        Path.Combine(context.ResolveHome(), ".codex", "sessions"),
                        "rollout-*.jsonl",
                        ParseCodexFile,
                        context,
                        token);
                }
                if (providerId == "claude")
                {
                    return Scan(
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

            AddOpenCodePeriod(result, executable, 0, "today", token, ref failures);
            AddOpenCodePeriod(result, executable, 7, "7d", token, ref failures);
            AddOpenCodePeriod(result, executable, 30, "30d", token, ref failures);

            if (result.Items.Count == 0)
            {
                return ConsumptionResult.Failed(failures > 0 ? "stats_failed" : "stats_empty");
            }

            result.Partial = failures > 0;
            if (failures > 0) result.ErrorCode = "stats_partial";
            return result;
        }

        private void AddOpenCodePeriod(
            ConsumptionResult target,
            string executable,
            int days,
            string period,
            CancellationToken token,
            ref int failures)
        {
            token.ThrowIfCancellationRequested();

            var json = RunOpenCodeStats(executable, days, token);
            if (json == null)
            {
                failures++;
                return;
            }

            var root = Json.ParseObject(json);
            if (root == null)
            {
                failures++;
                return;
            }

            var tokens = Json.Obj(Json.Get(root, "tokens"));
            var cache = Json.Obj(Json.Get(tokens, "cache"));

            var item = new Consumption
            {
                Period = period,
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
            target.Items.Add(item);
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

        // --------------------------------------------------------------- file scan

        private ConsumptionResult Scan(
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
                        HasRecords = parsed.HasRecords,
                        SkippedNoTimestamp = parsed.SkippedNoTimestamp,
                        LastSeenUtc = now
                    };
                }

                Merge(days, parsed.Days);
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

            var result = ConsumptionResult.Ok();
            result.Items.AddRange(Aggregate(days));
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

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                       64 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    token.ThrowIfCancellationRequested();

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
                    Bucket(outcome.Days, LocalKey(date.Value)).Add(new TokenTotals
                    {
                        Input = input,
                        Output = output,
                        Reasoning = reasoning,
                        CacheRead = cacheRead,
                        CacheWrite = cacheWrite,
                        Total = input + output
                    });
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

                    // Anthropic reports cache reads/writes outside input_tokens;
                    // they are disjoint billable buckets and must be added.
                    Bucket(outcome.Days, LocalKey(date.Value)).Add(new TokenTotals
                    {
                        Input = input,
                        Output = output,
                        CacheRead = cacheRead,
                        CacheWrite = cacheWrite,
                        Total = input + output + cacheRead + cacheWrite
                    });
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

        private List<Consumption> Aggregate(Dictionary<string, TokenTotals> days)
        {
            var today = _clock.LocalNow.Date;
            var todayKey = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var result = new List<Consumption>();
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

            if (!any) return result;

            result.Add(ToConsumption("today", todayTotals));
            result.Add(ToConsumption("7d", weekTotals));
            result.Add(ToConsumption("30d", monthTotals));
            return result;
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
