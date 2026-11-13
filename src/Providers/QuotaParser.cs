using System;
using System.Collections.Generic;

namespace AiUsageWidget.Providers
{
    /// <summary>
    /// Pure response normalization. Network calls and credential reads live in the
    /// provider adapters so every supported response shape can be tested offline.
    /// </summary>
    public static class QuotaParser
    {
        public static ProviderSnapshot ParseOpenCode(string body)
        {
            var root = Json.ParseObject(body);
            if (root == null)
            {
                throw new QuotaException("parse", "Unexpected OpenCode Go response.", 200);
            }

            var snapshot = new ProviderSnapshot
            {
                Id = "opencode-go",
                Name = "OpenCode Go",
                Plan = "Go",
                Status = ProviderStatus.Ok
            };

            var usage = Json.Obj(Json.Get(root, "usage"));
            if (usage == null)
            {
                throw new QuotaException("parse", "Unexpected OpenCode Go response.", 200);
            }

            ProviderUtil.AddWindow(snapshot, usage, "rolling", "5h", 300, false);
            ProviderUtil.AddWindow(snapshot, usage, "weekly", "Week", 10080, false);
            ProviderUtil.AddWindow(snapshot, usage, "monthly", "Month", null, true);

            if (snapshot.Windows.Count == 0)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.ErrorCode = "empty";
                snapshot.ErrorMessage = "OpenCode Go returned no usage windows.";
            }

            ProviderUtil.SortWindows(snapshot);
            return snapshot;
        }

        public static ProviderSnapshot ParseCodex(string body)
        {
            var root = Json.ParseObject(body);
            if (root == null)
            {
                throw new QuotaException("parse", "Unexpected Codex response.", 200);
            }

            var snapshot = new ProviderSnapshot
            {
                Id = "codex",
                Name = "ChatGPT / Codex",
                Plan = TitleCase(Json.Str(Json.Get(root, "plan_type"))),
                Status = ProviderStatus.Ok
            };

            var rateLimit = Json.Obj(Json.Get(root, "rate_limit"));
            AddCodexWindow(snapshot, Json.Obj(Json.Get(rateLimit, "primary_window")), "primary");
            AddCodexWindow(snapshot, Json.Obj(Json.Get(rateLimit, "secondary_window")), "secondary");

            if (snapshot.Windows.Count == 0)
            {
                snapshot.Status = ProviderStatus.Unsupported;
                snapshot.ErrorCode = "no_rate_limit";
                snapshot.ErrorMessage = "Codex did not report usage windows for this plan.";
            }

            ProviderUtil.SortWindows(snapshot);
            return snapshot;
        }

        public static ProviderSnapshot ParseClaude(string body, string plan)
        {
            var root = Json.ParseObject(body);
            if (root == null)
            {
                throw new QuotaException("parse", "Unexpected Claude response.", 200);
            }

            var snapshot = new ProviderSnapshot
            {
                Id = "claude",
                Name = "Claude",
                Plan = plan,
                Status = ProviderStatus.Ok
            };

            // Newer responses expose a forward-compatible `limits` array. If it is
            // present but yields no usable window, fall back to the legacy fields
            // instead of reporting an empty success.
            var limits = Json.Arr(Json.Get(root, "limits"));
            if (limits != null && limits.Length > 0)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < limits.Length; i++)
                {
                    var limit = Json.Obj(limits[i]);
                    if (limit == null) continue;

                    var kind = Json.Str(Json.Get(limit, "kind"));
                    if (string.IsNullOrEmpty(kind)) continue;
                    if (!seen.Add(kind)) continue;

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

                if (snapshot.Windows.Count == 0)
                {
                    snapshot.Windows.Clear();
                }
            }

            if (snapshot.Windows.Count == 0)
            {
                ProviderUtil.AddWindow(snapshot, root, "five_hour", "5h", 300, false);
                ProviderUtil.AddWindow(snapshot, root, "seven_day", "Week", 10080, false);
                ProviderUtil.AddWindow(snapshot, root, "seven_day_opus", "Week \u00b7 Opus", 10080, false);
                ProviderUtil.AddWindow(snapshot, root, "seven_day_sonnet", "Week \u00b7 Sonnet", 10080, false);
            }

            if (snapshot.Windows.Count == 0)
            {
                snapshot.Status = ProviderStatus.Error;
                snapshot.ErrorCode = "empty";
                snapshot.ErrorMessage = "Claude returned no usage windows.";
            }

            ProviderUtil.SortWindows(snapshot);
            return snapshot;
        }

        private static void AddCodexWindow(ProviderSnapshot snapshot, Dictionary<string, object> node, string id)
        {
            if (node == null) return;

            var percent = Json.Num(Json.Get(node, "used_percent"), double.NaN);
            if (double.IsNaN(percent)) return;

            var seconds = Json.Num(Json.Get(node, "limit_window_seconds"), 0);
            var resetAt = Json.Long(Json.Get(node, "reset_at"));

            DateTimeOffset? resetsAt = null;
            if (resetAt > 0 && resetAt <= 253402300799L)
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetAt);
            }

            int? minutes = null;
            if (seconds > 0 && seconds <= 2147483647.0 * 60.0)
            {
                minutes = (int)(seconds / 60.0);
            }

            ProviderUtil.AddWindowValue(
                snapshot,
                id,
                seconds > 0 ? ProviderUtil.MinutesLabel(seconds) : "Window",
                percent,
                resetsAt,
                minutes,
                false);
        }

        private static string TitleCase(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            return char.ToUpperInvariant(value[0]) + value.Substring(1);
        }
    }
}
