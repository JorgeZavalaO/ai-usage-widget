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

        public long TotalTokens
        {
            get { return Input + Output + Reasoning + CacheRead + CacheWrite; }
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
        public DateTimeOffset FetchedAt;
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

        public bool HasData
        {
            get { return Status == ProviderStatus.Ok || Status == ProviderStatus.Stale; }
        }
    }

    public sealed class AppConfig
    {
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
                Version = 1,
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
    {        public readonly string Code;
        public readonly int HttpStatus;

        public QuotaException(string code, string message, int httpStatus)
            : base(message)
        {
            Code = code;
            HttpStatus = httpStatus;
        }
    }
}
