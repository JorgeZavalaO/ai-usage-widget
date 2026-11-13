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

        /// <summary>Overrides the user profile directory in tests.</summary>
        public string Home;
        public IHttpTransport Transport;
        public IClock Clock;
        public IProcessRunner ProcessRunner;

        /// <summary>Overrides the OpenCode CLI path in tests.</summary>
        public string OpenCodeExecutable;

        /// <summary>Overrides OpenCode service detection in tests.</summary>
        public bool? OpenCodeServiceRunning;

        public string ResolveHome()
        {
            return string.IsNullOrEmpty(Home) ? ProviderUtil.Home : Home;
        }

        public IHttpTransport ResolveTransport()
        {
            return Transport ?? HttpTransport.Default;
        }

        public IClock ResolveClock()
        {
            return Clock ?? SystemClock.Instance;
        }
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
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
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
                Name = provider.Name
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
            var percent = Json.Num(Json.Get(node, "percent"), double.NaN);
            if (double.IsNaN(percent)) percent = Json.Num(Json.Get(node, "used_percent"), double.NaN);
            if (double.IsNaN(percent)) percent = Json.Num(Json.Get(node, "utilization"), double.NaN);
            if (double.IsNaN(percent)) return;

            var resets = Json.Date(
                Json.Get(node, "resetsAt") ??
                Json.Get(node, "resets_at") ??
                Json.Get(node, "reset_at") ??
                Json.Get(node, "resetAt"));

            AddWindowValue(snapshot, key, label, percent, resets, windowMinutes, isCycle);
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
            if (!Json.IsFinite(percent)) return;
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
                    label = "Week \u00b7 Opus";
                    minutes = 10080;
                    return;
                case "weekly_sonnet":
                    label = "Week \u00b7 Sonnet";
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
            {
                var byLength = (a.WindowMinutes ?? int.MaxValue).CompareTo(b.WindowMinutes ?? int.MaxValue);
                if (byLength != 0) return byLength;
                return string.CompareOrdinal(a.Id, b.Id);
            });
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

            var key = ResolveKey(context, token);
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
                { "User-Agent", "ai-usage-widget/1.1" }
            };

            var result = context.ResolveTransport().Get(UsageUrl, headers, 15000, token);

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

            var parsed = QuotaParser.ParseOpenCode(result.Body);
            parsed.Plan = "Go";
            parsed.FetchedAt = context.ResolveClock().UtcNow;
            return parsed;
        }

        private static string ResolveKey(FetchContext context, CancellationToken token)
        {
            // 1. Widget settings (decrypted once by ConfigStore, DPAPI at rest).
            if (!string.IsNullOrWhiteSpace(context.OpenCodeKey)) return context.OpenCodeKey.Trim();

            // 2. Environment variable.
            var env = Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

            // 3. OpenCode v2 local service credential store (zero-config).
            var serviceKey = OpenCodeService.TryGetGoKey(context, token);
            if (!string.IsNullOrWhiteSpace(serviceKey)) return serviceKey.Trim();

            // 4. Legacy auth.json (may hold an expired key; last resort).
            var path = Path.Combine(context.ResolveHome(), ".local", "share", "opencode", "auth.json");
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
        private static string _cachedHome;
        private static DateTime _cachedAt;

        public static void Invalidate()
        {
            _cached = null;
            _cachedHome = null;
        }

        public static bool IsRunning(FetchContext context)
        {
            if (context != null && context.OpenCodeServiceRunning.HasValue)
            {
                return context.OpenCodeServiceRunning.Value;
            }

            try
            {
                var path = Path.Combine(context == null
                    ? ProviderUtil.Home
                    : context.ResolveHome(), ".local", "state", "opencode", "service.json");
                var text = ProviderUtil.ReadText(path);
                if (text == null) return false;

                var info = Json.ParseObject(text);
                var pid = (int)Json.Long(Json.Get(info, "pid"));
                if (pid <= 0) return false;

                using (var process = System.Diagnostics.Process.GetProcessById(pid))
                {
                    return !process.HasExited;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static string TryGetGoKey(FetchContext context, CancellationToken token)
        {
            var home = context == null ? ProviderUtil.Home : context.ResolveHome();
            if (_cached != null &&
                string.Equals(_cachedHome, home, StringComparison.OrdinalIgnoreCase) &&
                (DateTime.UtcNow - _cachedAt).TotalMinutes < 10) return _cached;

            try
            {
                var path = Path.Combine(home, ".local", "state", "opencode", "service.json");
                var text = ProviderUtil.ReadText(path);
                if (text == null) return null;

                var info = Json.ParseObject(text);
                var url = Json.Str(Json.Get(info, "url"));
                var password = Json.Str(Json.Get(info, "password"));
                if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(password)) return null;

                string reason;
                if (!Http.IsAllowedLocalServiceUrl(url, out reason))
                {
                    Log.Error("opencode-service: refused registration url (" + reason + ")");
                    return null;
                }

                var token64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password));
                var headers = new Dictionary<string, string>
                {
                    { "Authorization", "Basic " + token64 },
                    { "Accept", "application/json" },
                    { "User-Agent", "ai-usage-widget/1.1" }
                };

                var transport = context == null ? HttpTransport.Default : context.ResolveTransport();
                var result = transport.Get(url.TrimEnd('/') + "/api/credential", headers, 4000, token);
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
                    _cachedHome = home;
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

            var credentials = ReadCredentials(context);
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
                { "User-Agent", "ai-usage-widget/1.1" }
            };
            if (!string.IsNullOrEmpty(credentials.AccountId))
            {
                headers["chatgpt-account-id"] = credentials.AccountId;
            }

            var result = context.ResolveTransport().Get(UsageUrl, headers, 15000, token);

            // Only 401 proves the token is unusable. A 403 can mean account
            // entitlement or an edge block, so it is retried as a transient error.
            if (result.Status == 401)
            {
                snapshot.Status = ProviderStatus.StaleAuth;
                snapshot.ErrorCode = "http_401";
                snapshot.ErrorMessage = "Codex token expired. Run Codex once to refresh it.";
                return snapshot;
            }

            if (result.Status != 200)
            {
                throw new QuotaException("http_" + result.Status,
                    "Codex returned HTTP " + result.Status + ".", result.Status);
            }

            var parsed = QuotaParser.ParseCodex(result.Body);
            parsed.FetchedAt = context.ResolveClock().UtcNow;
            return parsed;
        }

        private static Credentials ReadCredentials(FetchContext context)
        {
            var path = Path.Combine(context.ResolveHome(), ".codex", "auth.json");
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

            var credentials = ReadCredentials(context);
            if (credentials == null || string.IsNullOrEmpty(credentials.AccessToken))
            {
                snapshot.Status = ProviderStatus.NoCredentials;
                snapshot.ErrorCode = "no_auth";
                snapshot.ErrorMessage = "No Claude credentials. Run Claude Code once.";
                return snapshot;
            }

            var plan = TitleCase(credentials.SubscriptionType);
            snapshot.Plan = plan;

            var nowMs = context.ResolveClock().UtcNow.ToUnixTimeMilliseconds();
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

            var result = context.ResolveTransport().Get(UsageUrl, headers, 15000, token);

            if (result.Status == 401)
            {
                snapshot.Status = ProviderStatus.StaleAuth;
                snapshot.ErrorCode = "http_401";
                snapshot.ErrorMessage = "Claude token expired. Open Claude Code once to refresh it.";
                return snapshot;
            }

            if (result.Status != 200)
            {
                throw new QuotaException("http_" + result.Status,
                    "Claude returned HTTP " + result.Status + ".", result.Status);
            }

            var parsed = QuotaParser.ParseClaude(result.Body, plan);
            parsed.FetchedAt = context.ResolveClock().UtcNow;
            return parsed;
        }

        private static string TitleCase(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }

        private static Credentials ReadCredentials(FetchContext context)
        {
            var path = Path.Combine(context.ResolveHome(), ".claude", ".credentials.json");
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
