using System;
using System.Collections.Generic;

namespace AiUsageWidget
{
    public enum ProviderStatus
    {
        Ok,
        Stale,
        StaleAuth,
        NoCredentials,
        Unsupported,
        Disabled,
        Error
    }

    public sealed class UsageWindow
    {
        public string Id;
        public string Label;
        public double UsedPercent;
        public DateTimeOffset? ResetsAt;
        public int? WindowMinutes;
        public bool IsCycle;
    }

    public sealed class Consumption
    {
        public string Period;
        public double? CostUsd;
        public long Input;
        public long Output;
        public long Reasoning;
        public long CacheRead;
        public long CacheWrite;

        /// <summary>
        /// Non-overlapping total supplied by the provider adapter. A negative
        /// value means the fields are disjoint and may be summed.
        /// </summary>
        public long Total = -1;

        public long TotalTokens
        {
            get { return Total >= 0 ? Total : Input + Output + Reasoning + CacheRead + CacheWrite; }
        }
    }

    /// <summary>
    /// One local calendar date of usage. Unlike <see cref="Consumption"/> this is
    /// a single day, so it can be plotted.
    /// </summary>
    public sealed class DailyUsage
    {
        public string Date;
        public long Input;
        public long Output;
        public long Reasoning;
        public long CacheRead;
        public long CacheWrite;

        /// <summary>Non-overlapping total. Negative means the buckets are disjoint.</summary>
        public long Total = -1;

        /// <summary>
        /// Prompt tokens that were actually billed as fresh input plus output.
        /// Cache reads are excluded on purpose: they dominate raw totals and would
        /// flatten every other series in a chart.
        /// </summary>
        public long Billable;

        /// <summary>Cost in USD as reported or estimated. Null when unknown.</summary>
        public double? CostUsd;

        /// <summary>Tokens whose model had no list price, so cost is understated.</summary>
        public long UnpricedTokens;

        public long TotalTokens
        {
            get { return Total >= 0 ? Total : Input + Output + Reasoning + CacheRead + CacheWrite; }
        }

        public DailyUsage Clone()
        {
            return new DailyUsage
            {
                Date = Date,
                Input = Input,
                Output = Output,
                Reasoning = Reasoning,
                CacheRead = CacheRead,
                CacheWrite = CacheWrite,
                Total = Total,
                Billable = Billable,
                CostUsd = CostUsd,
                UnpricedTokens = UnpricedTokens
            };
        }
    }

    public sealed class ProviderSnapshot
    {
        public string Id;
        public string Name;
        public string Plan;
        public ProviderStatus Status = ProviderStatus.Ok;
        public readonly List<UsageWindow> Windows = new List<UsageWindow>();
        public readonly List<Consumption> Consumption = new List<Consumption>();

        /// <summary>
        /// Local daily series for the chart, oldest first. Empty when the provider
        /// reports no local history.
        /// </summary>
        public readonly List<DailyUsage> Daily = new List<DailyUsage>();

        /// <summary>True when at least one of the last 30 local dates is missing.</summary>
        public bool DailyIncomplete;

        /// <summary>Time of the last successful quota fetch. Never advanced by a failed attempt.</summary>
        public DateTimeOffset FetchedAt;

        /// <summary>Time of the most recent quota attempt, successful or not.</summary>
        public DateTimeOffset LastAttemptAt;

        public DateTimeOffset? ConsumptionUpdatedAt;
        public string ConsumptionError;
        public bool ConsumptionStale;

        public string ErrorCode;
        public string ErrorMessage;
        public bool FromCache;

        public double WorstPercent
        {
            get
            {
                double worst = -1;
                for (int i = 0; i < Windows.Count; i++)
                {
                    if (Windows[i].UsedPercent > worst) worst = Windows[i].UsedPercent;
                }
                return worst < 0 ? 0 : worst;
            }
        }

        /// <summary>True when the snapshot carries values that can be rendered.</summary>
        public bool HasData
        {
            get { return Status != ProviderStatus.Disabled && Windows.Count > 0; }
        }

        public bool IsStale
        {
            get
            {
                return Status == ProviderStatus.Stale ||
                       Status == ProviderStatus.StaleAuth ||
                       FromCache;
            }
        }
    }

    public sealed class AppConfig
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool LaunchAtLogin { get; set; }
        public bool Collapsed { get; set; }
        public bool ShowConsumption { get; set; }
        public string OpenCodeKeyEnc { get; set; }
        public bool OpenCodeEnabled { get; set; }
        public bool CodexEnabled { get; set; }
        public bool ClaudeEnabled { get; set; }
        public int QuotaMinutes { get; set; }
        public int ConsumptionMinutes { get; set; }
        public double WarnPercent { get; set; }
        public double CriticalPercent { get; set; }
        public double? WindowX { get; set; }
        public double? WindowY { get; set; }

        public static AppConfig CreateDefault()
        {
            return new AppConfig
            {
                Version = CurrentVersion,
                AlwaysOnTop = true,
                LaunchAtLogin = false,
                Collapsed = false,
                ShowConsumption = true,
                OpenCodeEnabled = true,
                CodexEnabled = true,
                ClaudeEnabled = true,
                QuotaMinutes = 5,
                ConsumptionMinutes = 30,
                WarnPercent = 50,
                CriticalPercent = 80
            };
        }

        public AppConfig Clone()
        {
            return new AppConfig
            {
                Version = Version,
                AlwaysOnTop = AlwaysOnTop,
                LaunchAtLogin = LaunchAtLogin,
                Collapsed = Collapsed,
                ShowConsumption = ShowConsumption,
                OpenCodeKeyEnc = OpenCodeKeyEnc,
                OpenCodeEnabled = OpenCodeEnabled,
                CodexEnabled = CodexEnabled,
                ClaudeEnabled = ClaudeEnabled,
                QuotaMinutes = QuotaMinutes,
                ConsumptionMinutes = ConsumptionMinutes,
                WarnPercent = WarnPercent,
                CriticalPercent = CriticalPercent,
                WindowX = WindowX,
                WindowY = WindowY
            };
        }
    }

    public static class ProviderShortName
    {
        public static string Of(string id)
        {
            switch (id)
            {
                case "opencode-go": return "OC";
                case "codex": return "CX";
                case "claude": return "CL";
                default: return id;
            }
        }
    }

    public sealed class QuotaException : Exception
    {
        public readonly string Code;
        public readonly int HttpStatus;

        public QuotaException(string code, string message, int httpStatus)
            : base(message)
        {
            Code = code;
            HttpStatus = httpStatus;
        }
    }
}
