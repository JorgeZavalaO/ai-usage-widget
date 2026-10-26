using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AiUsageWidget.Providers
{
    internal sealed class TokenTotals
    {
        public long Input;
        public long Output;
        public long Reasoning;
        public long CacheRead;
        public long CacheWrite;

        public void Add(TokenTotals other)
        {
            Input += other.Input;
            Output += other.Output;
            Reasoning += other.Reasoning;
            CacheRead += other.CacheRead;
            CacheWrite += other.CacheWrite;
        }
    }

    /// <summary>
    /// Local consumption plane. Best effort: a failure here never affects quota data.
    /// Files are cached by (size, mtime) so a 30-minute refresh re-reads nothing.
    /// </summary>
    public static class ConsumptionService
    {
        private sealed class FileCache
        {
            public long Size;
            public DateTime MTimeUtc;
            public Dictionary<string, TokenTotals> Days;
        }

        private static readonly Dictionary<string, FileCache> Cache =
            new Dictionary<string, FileCache>(StringComparer.OrdinalIgnoreCase);

        public static List<Consumption> Fetch(string providerId, FetchContext context)
        {
            try
            {
                if (providerId == "opencode-go") return FetchOpenCode();
                if (providerId == "codex") return FetchCodex();
                if (providerId == "claude") return FetchClaude();
            }
            catch (Exception ex)
            {
                Log.Error("consumption/" + providerId + ": " + ex.GetType().Name);
            }
            return new List<Consumption>();
        }

        // ---------------------------------------------------------------- opencode

        private static List<Consumption> FetchOpenCode()
        {
            // Never let the widget start the OpenCode background service just to
            // read numbers: only read when it is already running.
            if (!OpenCodeService.IsRunning()) return new List<Consumption>();

            var executable = ResolveOpenCode();
            if (executable == null) return new List<Consumption>();

            var result = new List<Consumption>();
            AddOpenCodePeriod(result, executable, 0, "today");
            AddOpenCodePeriod(result, executable, 7, "7d");
            AddOpenCodePeriod(result, executable, 30, "30d");
            return result;
        }

        private static void AddOpenCodePeriod(List<Consumption> target, string executable, int days, string period)
        {
            var json = RunOpenCodeStats(executable, days);
            if (json == null) return;

            var root = Json.ParseObject(json);
            if (root == null) return;

            var tokens = Json.Obj(Json.Get(root, "tokens"));
            var cache = Json.Obj(Json.Get(tokens, "cache"));
            var item = new Consumption
            {
                Period = period,
                CostUsd = Json.Num(Json.Get(root, "cost"), 0),
                Input = Json.Long(Json.Get(tokens, "input")),
                Output = Json.Long(Json.Get(tokens, "output")),
                Reasoning = Json.Long(Json.Get(tokens, "reasoning")),
                CacheRead = Json.Long(Json.Get(cache, "read")),
                CacheWrite = Json.Long(Json.Get(cache, "write"))
            };
            target.Add(item);
        }

        private static string RunOpenCodeStats(string executable, int days)
        {
            var arguments = "/s /c \"\"" + executable + "\" stats --days " +
                            days.ToString(CultureInfo.InvariantCulture) + " --json\"";

            var info = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            try
            {
                using (var process = Process.Start(info))
                {
                    if (process == null) return null;
                    var output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(25000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return null;
                    }
                    if (process.ExitCode != 0) return null;
                    return string.IsNullOrWhiteSpace(output) ? null : output;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ResolveOpenCode()
        {
            var npm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm",
                "opencode.cmd");
            return File.Exists(npm) ? npm : null;
        }

        // ------------------------------------------------------------------- codex

        private static List<Consumption> FetchCodex()
        {
            var root = Path.Combine(ProviderUtil.Home, ".codex", "sessions");
            if (!Directory.Exists(root)) return new List<Consumption>();

            var days = new Dictionary<string, TokenTotals>(StringComparer.Ordinal);
            var cutoff = DateTime.UtcNow.AddDays(-31);

            foreach (var file in SafeEnumerate(root, "rollout-*.jsonl"))
            {
                FileInfo info;
                try { info = new FileInfo(file); }
                catch (Exception) { continue; }

                if (info.LastWriteTimeUtc < cutoff) continue;

                FileCache cached;
                if (Cache.TryGetValue(file, out cached) &&
                    cached.Size == info.Length &&
                    cached.MTimeUtc == info.LastWriteTimeUtc)
                {
                    Merge(days, cached.Days);
                    continue;
                }

                var parsed = ParseCodexFile(file);
                Cache[file] = new FileCache
                {
                    Size = info.Length,
                    MTimeUtc = info.LastWriteTimeUtc,
                    Days = parsed
                };
                Merge(days, parsed);
            }

            return Aggregate(days);
        }

        private static Dictionary<string, TokenTotals> ParseCodexFile(string path)
        {
            var result = new Dictionary<string, TokenTotals>(StringComparer.Ordinal);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                       64 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
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
                    var key = (date ?? DateTimeOffset.Now).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                    Bucket(result, key).Add(new TokenTotals
                    {
                        Input = Json.Long(Json.Get(usage, "input_tokens")),
                        Output = Json.Long(Json.Get(usage, "output_tokens")),
                        Reasoning = Json.Long(Json.Get(usage, "reasoning_output_tokens")),
                        CacheRead = Json.Long(Json.Get(usage, "cached_input_tokens")),
                        CacheWrite = Json.Long(Json.Get(usage, "cache_write_input_tokens"))
                    });
                }
            }

            return result;
        }

        // ------------------------------------------------------------------ claude

        private static List<Consumption> FetchClaude()
        {
            var root = Path.Combine(ProviderUtil.Home, ".claude", "projects");
            if (!Directory.Exists(root)) return new List<Consumption>();

            var days = new Dictionary<string, TokenTotals>(StringComparer.Ordinal);
            var cutoff = DateTime.UtcNow.AddDays(-31);

            foreach (var file in SafeEnumerate(root, "*.jsonl"))
            {
                FileInfo info;
                try { info = new FileInfo(file); }
                catch (Exception) { continue; }

                if (info.LastWriteTimeUtc < cutoff) continue;

                FileCache cached;
                if (Cache.TryGetValue(file, out cached) &&
                    cached.Size == info.Length &&
                    cached.MTimeUtc == info.LastWriteTimeUtc)
                {
                    Merge(days, cached.Days);
                    continue;
                }

                var parsed = ParseClaudeFile(file);
                Cache[file] = new FileCache
                {
                    Size = info.Length,
                    MTimeUtc = info.LastWriteTimeUtc,
                    Days = parsed
                };
                Merge(days, parsed);
            }

            return Aggregate(days);
        }

        private static Dictionary<string, TokenTotals> ParseClaudeFile(string path)
        {
            var result = new Dictionary<string, TokenTotals>(StringComparer.Ordinal);

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                       64 * 1024, FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) continue;
                    if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) continue;

                    var root = Json.ParseObject(line);
                    if (root == null) continue;

                    var message = Json.Obj(Json.Get(root, "message"));
                    if (message == null) continue;

                    var usage = Json.Obj(Json.Get(message, "usage"));
                    if (usage == null) continue;

                    var date = Json.Date(Json.Get(root, "timestamp"));
                    var key = (date ?? DateTimeOffset.Now).ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                    Bucket(result, key).Add(new TokenTotals
                    {
                        Input = Json.Long(Json.Get(usage, "input_tokens")),
                        Output = Json.Long(Json.Get(usage, "output_tokens")),
                        CacheRead = Json.Long(Json.Get(usage, "cache_read_input_tokens")),
                        CacheWrite = Json.Long(Json.Get(usage, "cache_creation_input_tokens"))
                    });
                }
            }

            return result;
        }

        // ---------------------------------------------------------------- helpers

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

        private static List<Consumption> Aggregate(Dictionary<string, TokenTotals> days)
        {
            var today = DateTime.Now.Date;
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
                if (age < 0 || age > 30) continue;

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
                CacheWrite = totals.CacheWrite
            };
        }

        private static IEnumerable<string> SafeEnumerate(string root, string pattern)
        {
            try
            {
                return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                return new string[0];
            }
        }
    }
}
