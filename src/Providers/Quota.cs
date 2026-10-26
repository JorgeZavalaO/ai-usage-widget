using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace AiUsageWidget.Providers
{
    public sealed class FetchContext
    {
        public AppConfig Config;
        public string OpenCodeKey;
    }

    public interface IQuotaProvider
    {
        string Id { get; }
        string Name { get; }
        ProviderSnapshot Fetch(FetchContext context, CancellationToken token);
    }

    internal static class ProviderUtil
    {
        public static string Home
        {
            get { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        }

        public static string ReadText(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static ProviderSnapshot NewSnapshot(IQuotaProvider provider)
        {
            return new ProviderSnapshot
            {
                Id = provider.Id,
                Name = provider.Name,
                FetchedAt = DateTimeOffset.UtcNow
            };
        }

        public static void AddWindow(
            ProviderSnapshot snapshot,
            Dictionary<string, object> usage,
            string key,
            string label,
            int? windowMinutes,
            bool isCycle)
        {
            if (usage == null) return;

            var node = Json.Obj(Json.Get(usage, key));
            if (node == null) return;

            // Accept several field spellings so a provider-side rename does not break us.
            double percent = Json.Num(Json.Get(node, "percent"), double.NaN);
            if (double.IsNaN(percent)) percent = Json.Num(Json.Get(node, "used_percent"), double.NaN);
            if (double.IsNaN(percent)) percent = Json.Num(Json.Get(node, "utilization"), double.NaN);
            if (double.IsNaN(percent)) return;

            var resets = Json.Date(
                Json.Get(node, "resetsAt") ??
                Json.Get(node, "resets_at") ??
                Json.Get(node, "reset_at") ??
                Json.Get(node, "resetAt"));

            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            snapshot.Windows.Add(new UsageWindow
            {
                Id = key,
                Label = label,
                UsedPercent = percent,
                ResetsAt = resets,
                WindowMinutes = windowMinutes,
                IsCycle = isCycle
            });
        }

        public static void AddWindowValue(
            ProviderSnapshot snapshot,
            string id,
            string label,
            double percent,
            DateTimeOffset? resetsAt,
            int? windowMinutes,
            bool isCycle)
        {
            if (double.IsNaN(percent)) return;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            snapshot.Windows.Add(new UsageWindow
            {
                Id = id,
                Label = label,
                UsedPercent = percent,
                ResetsAt = resetsAt,
                WindowMinutes = windowMinutes,
                IsCycle = isCycle
            });
        }

        public static string MinutesLabel(double seconds)
        {
            if (seconds >= 604800) return "Week";
            if (seconds >= 86400) return ((int)Math.Round(seconds / 86400.0)) + "d";
            if (seconds >= 3600) return ((int)Math.Round(seconds / 3600.0)) + "h";
            return ((int)Math.Round(seconds / 60.0)) + "m";
        }

        /// <summary>Maps Claude limit kinds to a display label and window length.</summary>
        public static void LabelFor(string kind, out string label, out int? minutes)
        {
            switch (kind)
            {
                case "session":
                    label = "5h";
                    minutes = 300;
                    return;
                case "weekly_all":
                    label = "Week";
                    minutes = 10080;
                    return;
                case "weekly_opus":
                    label = "Week · Opus";
                    minutes = 10080;
                    return;
                case "weekly_sonnet":
                    label = "Week · Sonnet";
                    minutes = 10080;
                    return;
                default:
                    label = Prettify(kind);
                    minutes = null;
                    return;
            }
        }

        public static string Prettify(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return kind;
            var text = kind.Replace('_', ' ').Trim();
            if (text.Length == 0) return kind;
            return char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        public static void SortWindows(ProviderSnapshot snapshot)
        {
            snapshot.Windows.Sort((a, b) =>
                (a.WindowMinutes ?? int.MaxValue).CompareTo(b.WindowMinutes ?? int.MaxValue));
        }
    }

    public sealed class OpenCodeGoProvider : IQuotaProvider
    {
        private const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";
        public string Id { get { return "opencode-go"; } }
        public string Name { get { return "OpenCode Go"; } }

        public ProviderSnapshot Fetch(FetchContext context, CancellationToken token)
        {
            var snapshot = ProviderUtil.NewSnapshot(this);
            snapshot.Plan = "Go";

            var key = ResolveKey(context);
            if (string.IsNullOrEmpty(key))
            {
                snapshot.Status = ProviderStatus.NoCredentials;
                snapshot.ErrorCode = "no_key";
                snapshot.ErrorMessage = "No OpenCode Go key. Open Settings to add one.";
                return snapshot;
            }

            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + key },
                { "Accept", "application/json" },
                { "User-Agent", "ai-usage-widget/1.0" }
            };

            var result = Http.Get(UsageUrl, headers, 15000);

            if (result.Status == 401)
            {
                snapshot.Status = ProviderStatus.StaleAuth;
                snapshot.ErrorCode = "api_key_expired";
                snapshot.ErrorMessage = "OpenCode Go key expired. Create a new one in Console and paste it in Settings.";
                OpenCodeService.Invalidate();
                return snapshot;
            }

            if (result.Status == 403)
            {
                snapshot.Status = ProviderStatus.Unsupported;
                snapshot.ErrorCode = "entitlement";
                snapshot.ErrorMessage = "This key has no OpenCode Go subscription.";
                return snapshot;
            }

            if (result.Status != 200)
            {
                throw new QuotaException("http_" + result.Status,
                    "OpenCode Go returned HTTP " + result.Status + ".", result.Status);
            }

            var root = Json.ParseObject(result.Body);
            var usage = Json.Obj(Json.Get(root, "usage"));
            if (usage == null)
            {
                throw new QuotaException("parse", "Unexpected OpenCode Go response.", 200);
            }

            ProviderUtil.AddWindow(snapshot, usage, "rolling", "5h", 300, false);
            ProviderUtil.AddWindow(snapshot, usage, "weekly", "Week", 10080, false);
            ProviderUtil.AddWindow(snapshot, usage, "monthly", "Month", null, true);

            ApplyStatusFromWindows(snapshot);
            snapshot.FetchedAt = DateTimeOffset.UtcNow;
            return snapshot;
        }

        private static void ApplyStatusFromWindows(ProviderSnapshot snapshot)
        {
            if (snapshot.Windows.Count == 0)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.ErrorCode = "empty";
                snapshot.ErrorMessage = "OpenCode Go returned no usage windows.";
            }
        }

        private static string ResolveKey(FetchContext context)
        {
            // 1. Widget settings (decrypted once by ConfigStore, DPAPI at rest).
            if (!string.IsNullOrWhiteSpace(context.OpenCodeKey)) return context.OpenCodeKey.Trim();

            // 2. Environment variable.
            var env = Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            // 3. OpenCode v2 local service credential store (zero-config).
            var serviceKey = OpenCodeService.TryGetGoKey();
            if (!string.IsNullOrWhiteSpace(serviceKey)) return serviceKey.Trim();

            // 4. Legacy auth.json (may hold an expired key; last resort).
            var path = Path.Combine(
                ProviderUtil.Home, ".local", "share", "opencode", "auth.json");
            var text = ProviderUtil.ReadText(path);
            if (text == null) return null;

            var json = Json.ParseObject(text);
            var node = Json.Obj(Json.Get(json, "opencode-go"));
            return node == null ? null : Json.Str(Json.Get(node, "key"));
        }
    }

    /// <summary>
    /// Reads the OpenCode Go key from the running OpenCode v2 service.
    /// The registration file carries the URL and a Basic-auth password; the
    /// credential endpoint returns every integration, so we only ever extract
    /// the opencode-go key and never log the response body.
    /// </summary>
    internal static class OpenCodeService
    {
        private static string _cached;
        private static DateTime _cachedAt;

        public static void Invalidate()
        {
            _cached = null;
        }

        public static string TryGetGoKey()
        {
            if (_cached != null && (DateTime.UtcNow - _cachedAt).TotalMinutes < 10) return _cached;

            try
            {
                var path = Path.Combine(
                    ProviderUtil.Home, ".local", "state", "opencode", "service.json");
                var text = ProviderUtil.ReadText(path);
                if (text == null) return null;

                var info = Json.ParseObject(text);
                var url = Json.Str(Json.Get(info, "url"));
                var password = Json.Str(Json.Get(info, "password"));
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(password)) return null;

                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password));
                var headers = new Dictionary<string, string>
                {
                    { "Authorization", "Basic " + token },
                    { "Accept", "application/json" },
                    { "User-Agent", "ai-usage-widget/1.0" }
                };

                var result = Http.Get(url.TrimEnd('/') + "/api/credential", headers, 4000);
                if (result.Status != 200) return null;

                var root = Json.ParseObject(result.Body);
                var items = Json.Arr(Json.Get(root, "data"));
                if (items == null) return null;

                for (int i = 0; i < items.Length; i++)
                {
                    var credential = Json.Obj(items[i]);
                    if (credential == null) continue;
                    if (Json.Str(Json.Get(credential, "integrationID")) != "opencode-go") continue;

                    var active = Json.Get(credential, "active");
                    if (!(active is bool) || !(bool)active) continue;

                    var value = Json.Obj(Json.Get(credential, "value"));
                    var key = value == null ? null : Json.Str(Json.Get(value, "key"));
                    if (string.IsNullOrEmpty(key)) continue;

                    _cached = key;
                    _cachedAt = DateTime.UtcNow;
                    return key;
                }
            }
            catch (Exception)
            {
                // Service not running or not reachable: silently fall back.
            }

            return null;
        }
    }

    public sealed class CodexProvider : IQuotaProvider
    {
        private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

        public string Id { get { return "codex"; } }
        public string Name { get { return "ChatGPT / Codex"; } }

        private sealed class Credentials
        {
            public string AccessToken;
            public string AccountId;
        }

        public ProviderSnapshot Fetch(FetchContext context, CancellationToken token)
        {
            var snapshot = ProviderUtil.NewSnapshot(this);

            var credentials = ReadCredentials();
            if (credentials == null || string.IsNullOrEmpty(credentials.AccessToken))
            {
                snapshot.Status = ProviderStatus.NoCredentials;
                snapshot.ErrorCode = "no_auth";
                snapshot.ErrorMessage = "No Codex credentials. Run Codex once.";
                return snapshot;
            }

            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + credentials.AccessToken },
                { "Accept", "application/json" },
                { "User-Agent", "ai-usage-widget/1.0" }
            };
            if (!string.IsNullOrEmpty(credentials.AccountId))
            {
                headers["chatgpt-account-id"] = credentials.AccountId;
            }

            var result = Http.Get(UsageUrl, headers, 15000);

            if (result.Status == 401 || result.Status == 403)
            {
                snapshot.Status = ProviderStatus.StaleAuth;
                snapshot.ErrorCode = "http_" + result.Status;
                snapshot.ErrorMessage = "Codex token expired. Run Codex once to refresh it.";
                return snapshot;
            }

            if (result.Status != 200)
            {
                throw new QuotaException("http_" + result.Status,
                    "Codex returned HTTP " + result.Status + ".", result.Status);
            }

            var root = Json.ParseObject(result.Body);
            if (root == null)
            {
                throw new QuotaException("parse", "Unexpected Codex response.", 200);
            }

            snapshot.Plan = TitleCase(Json.Str(Json.Get(root, "plan_type")));

            var rateLimit = Json.Obj(Json.Get(root, "rate_limit"));
            AddWindow(snapshot, Json.Obj(Json.Get(rateLimit == null ? null : rateLimit, "primary_window")), "primary");
            AddWindow(snapshot, Json.Obj(Json.Get(rateLimit == null ? null : rateLimit, "secondary_window")), "secondary");

            if (snapshot.Windows.Count == 0)
            {
                snapshot.Status = ProviderStatus.Unsupported;
                snapshot.ErrorCode = "no_rate_limit";
                snapshot.ErrorMessage = "Codex did not report usage windows for this plan.";
            }

            snapshot.FetchedAt = DateTimeOffset.UtcNow;
            return snapshot;
        }

        private static void AddWindow(ProviderSnapshot snapshot, Dictionary<string, object> node, string id)
        {
            if (node == null) return;

            double percent = Json.Num(Json.Get(node, "used_percent"), double.NaN);
            if (double.IsNaN(percent)) return;

            double seconds = Json.Num(Json.Get(node, "limit_window_seconds"), 0);
            long resetAt = Json.Long(Json.Get(node, "reset_at"));

            DateTimeOffset? resetsAt = resetAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(resetAt)
                : (DateTimeOffset?)null;

            ProviderUtil.AddWindowValue(
                snapshot,
                id,
                seconds > 0 ? ProviderUtil.MinutesLabel(seconds) : "Window",
                percent,
                resetsAt,
                seconds > 0 ? (int?)(seconds / 60.0) : null,
                false);
        }

        private static string TitleCase(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private static Credentials ReadCredentials()
        {
            var path = Path.Combine(ProviderUtil.Home, ".codex", "auth.json");
            var text = ProviderUtil.ReadText(path);
            if (text == null) return null;

            var json = Json.ParseObject(text);
            var tokens = Json.Obj(Json.Get(json, "tokens"));
            if (tokens == null) return null;

            return new Credentials
            {
                AccessToken = Json.Str(Json.Get(tokens, "access_token")),
                AccountId = Json.Str(Json.Get(tokens, "account_id"))
            };
        }
    }

    public sealed class ClaudeProvider : IQuotaProvider
    {
        private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

        public string Id { get { return "claude"; } }
        public string Name { get { return "Claude"; } }

        private sealed class Credentials
        {
            public string AccessToken;
            public long ExpiresAtMs;
            public string SubscriptionType;
        }

        public ProviderSnapshot Fetch(FetchContext context, CancellationToken token)
        {
            var snapshot = ProviderUtil.NewSnapshot(this);

            var credentials = ReadCredentials();
            if (credentials == null || string.IsNullOrEmpty(credentials.AccessToken))
            {
                snapshot.Status = ProviderStatus.NoCredentials;
                snapshot.ErrorCode = "no_auth";
                snapshot.ErrorMessage = "No Claude credentials. Run Claude Code once.";
                return snapshot;
            }

            snapshot.Plan = TitleCase(credentials.SubscriptionType);

            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (credentials.ExpiresAtMs > 0 && nowMs >= credentials.ExpiresAtMs)
            {
                // Never refresh: rotating the refresh token would break the CLI login.
                snapshot.Status = ProviderStatus.StaleAuth;
                snapshot.ErrorCode = "token_expired";
                snapshot.ErrorMessage = "Claude token expired. Open Claude Code once to refresh it.";
                return snapshot;
            }

            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + credentials.AccessToken },
                { "anthropic-beta", "oauth-2025-04-20" },
                { "anthropic-version", "2023-06-01" },
                { "Accept", "application/json" },
                { "User-Agent", "claude-code/2.0.0" }
            };

            var result = Http.Get(UsageUrl, headers, 15000);

            if (result.Status == 401 || result.Status == 403)
            {
                snapshot.Status = ProviderStatus.StaleAuth;
                snapshot.ErrorCode = "http_" + result.Status;
                snapshot.ErrorMessage = "Claude token expired. Open Claude Code once to refresh it.";
                return snapshot;
            }

            if (result.Status != 200)
            {
                throw new QuotaException("http_" + result.Status,
                    "Claude returned HTTP " + result.Status + ".", result.Status);
            }

            var root = Json.ParseObject(result.Body);
            if (root == null)
            {
                throw new QuotaException("parse", "Unexpected Claude response.", 200);
            }

            // Newer responses expose a forward-compatible `limits` array; the
            // legacy top-level windows are still used as a fallback.
            var limits = Json.Arr(Json.Get(root, "limits"));
            if (limits != null && limits.Length > 0)
            {
                for (int i = 0; i < limits.Length; i++)
                {
                    var limit = Json.Obj(limits[i]);
                    if (limit == null) continue;

                    var kind = Json.Str(Json.Get(limit, "kind"));
                    if (string.IsNullOrEmpty(kind)) continue;

                    string label;
                    int? minutes;
                    ProviderUtil.LabelFor(kind, out label, out minutes);

                    ProviderUtil.AddWindowValue(
                        snapshot,
                        kind,
                        label,
                        Json.Num(Json.Get(limit, "percent"), double.NaN),
                        Json.Date(Json.Get(limit, "resets_at")),
                        minutes,
                        false);
                }
            }
            else
            {
                ProviderUtil.AddWindow(snapshot, root, "five_hour", "5h", 300, false);
                ProviderUtil.AddWindow(snapshot, root, "seven_day", "Week", 10080, false);
                ProviderUtil.AddWindow(snapshot, root, "seven_day_opus", "Week · Opus", 10080, false);
                ProviderUtil.AddWindow(snapshot, root, "seven_day_sonnet", "Week · Sonnet", 10080, false);
            }

            if (snapshot.Windows.Count == 0)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.ErrorCode = "empty";
                snapshot.ErrorMessage = "Claude returned no usage windows.";
            }

            snapshot.FetchedAt = DateTimeOffset.UtcNow;
            return snapshot;
        }

        private static string TitleCase(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private static Credentials ReadCredentials()
        {
            var path = Path.Combine(ProviderUtil.Home, ".claude", ".credentials.json");
            var text = ProviderUtil.ReadText(path);
            if (text == null) return null;

            var json = Json.ParseObject(text);
            var oauth = Json.Obj(Json.Get(json, "claudeAiOauth"));
            if (oauth == null) return null;

            return new Credentials
            {
                AccessToken = Json.Str(Json.Get(oauth, "accessToken")),
                ExpiresAtMs = Json.Long(Json.Get(oauth, "expiresAt")),
                SubscriptionType = Json.Str(Json.Get(oauth, "subscriptionType"))
            };
        }
    }
}
