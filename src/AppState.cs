using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Providers;

namespace AiUsageWidget
{
    public sealed class AppState : IDisposable
    {
        public readonly ConfigStore Store;

        private readonly List<IQuotaProvider> _quotaProviders = new List<IQuotaProvider>
        {
            new OpenCodeGoProvider(),
            new CodexProvider(),
            new ClaudeProvider()
        };

        private readonly List<ProviderSnapshot> _snapshots = new List<ProviderSnapshot>();
        private readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _blockedUntil = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly object _runGate = new object();

        private Timer _quotaTimer;
        private Timer _consumptionTimer;
        private bool _running;
        private bool _disposed;

        public bool Refreshing { get; private set; }
        public DateTimeOffset LastRefresh { get; private set; }

        public event Action Changed;

        public AppState(ConfigStore store)
        {
            Store = store;
            LastRefresh = DateTimeOffset.MinValue;
        }

        public AppConfig Config
        {
            get { return Store.Current; }
        }

        public List<ProviderSnapshot> Snapshots
        {
            get
            {
                lock (_snapshots)
                {
                    return new List<ProviderSnapshot>(_snapshots);
                }
            }
        }

        public void Start()
        {
            QueueRefresh(true, true, true);
            ScheduleTimers();
        }

        public void ScheduleTimers()
        {
            var quotaMinutes = Math.Max(1, Config.QuotaMinutes);
            var consumptionMinutes = Math.Max(5, Config.ConsumptionMinutes);

            if (_quotaTimer != null) _quotaTimer.Dispose();
            if (_consumptionTimer != null) _consumptionTimer.Dispose();

            _quotaTimer = new Timer(
                _ => QueueRefresh(true, false, false),
                null,
                TimeSpan.FromMinutes(quotaMinutes),
                TimeSpan.FromMinutes(quotaMinutes));

            _consumptionTimer = new Timer(
                _ => QueueRefresh(false, true, false),
                null,
                TimeSpan.FromMinutes(consumptionMinutes),
                TimeSpan.FromMinutes(consumptionMinutes));
        }

        public void RefreshNow()
        {
            QueueRefresh(true, true, true);
        }

        private void QueueRefresh(bool quota, bool consumption, bool force)
        {
            lock (_runGate)
            {
                if (_running || _disposed) return;
                _running = true;
            }

            Task.Run(() =>
            {
                try
                {
                    Execute(quota, consumption, force);
                }
                catch (Exception ex)
                {
                    Log.Error("refresh: " + ex.GetType().Name);
                }
                finally
                {
                    lock (_runGate) { _running = false; }
                }
            });
        }

        private void Execute(bool quota, bool consumption, bool force)
        {
            Refreshing = quota || consumption;
            Raise();

            var context = new FetchContext
            {
                Config = Config,
                OpenCodeKey = Store.GetOpenCodeKey()
            };

            if (quota)
            {
                var fresh = new List<ProviderSnapshot>(_quotaProviders.Count);
                foreach (var provider in _quotaProviders)
                {
                    fresh.Add(FetchOne(provider, context, force));
                }

                lock (_snapshots)
                {
                    _snapshots.Clear();
                    _snapshots.AddRange(fresh);
                }

                LastRefresh = DateTimeOffset.UtcNow;
            }

            Raise();

            if (consumption)
            {
                var current = Snapshots;
                var next = new List<ProviderSnapshot>(current.Count);
                var changed = false;

                for (int i = 0; i < current.Count; i++)
                {
                    var snapshot = current[i];
                    if (snapshot.Status == ProviderStatus.Disabled)
                    {
                        next.Add(snapshot);
                        continue;
                    }

                    var items = ConsumptionService.Fetch(snapshot.Id, context);
                    if (items == null || items.Count == 0)
                    {
                        next.Add(snapshot);
                        continue;
                    }

                    var clone = Clone(snapshot);
                    clone.Consumption.Clear();
                    clone.Consumption.AddRange(items);
                    next.Add(clone);
                    changed = true;
                }

                if (changed)
                {
                    lock (_snapshots)
                    {
                        _snapshots.Clear();
                        _snapshots.AddRange(next);
                    }
                }
            }

            Refreshing = false;
            Raise();
        }

        private ProviderSnapshot FetchOne(IQuotaProvider provider, FetchContext context, bool force)
        {
            if (!IsEnabled(provider.Id))
            {
                return new ProviderSnapshot
                {
                    Id = provider.Id,
                    Name = provider.Name,
                    Status = ProviderStatus.Disabled,
                    FetchedAt = DateTimeOffset.UtcNow
                };
            }

            DateTimeOffset blockedUntil;
            if (!force && _blockedUntil.TryGetValue(provider.Id, out blockedUntil) &&
                DateTimeOffset.UtcNow < blockedUntil)
            {
                return KeepPrevious(provider, "backoff", "Waiting before the next retry.");
            }

            try
            {
                var snapshot = provider.Fetch(context, _cancellation.Token);
                _failures[provider.Id] = 0;
                _blockedUntil.Remove(provider.Id);
                ProviderUtil.SortWindows(snapshot);
                PreserveConsumption(snapshot);
                return snapshot;
            }
            catch (Exception ex)
            {
                var quotaError = ex as QuotaException;
                var code = quotaError != null ? quotaError.Code : ex.GetType().Name;
                var message = ex.Message;

                if (quotaError != null && (quotaError.HttpStatus == 401 || quotaError.HttpStatus == 403))
                {
                    _blockedUntil[provider.Id] = DateTimeOffset.UtcNow.AddMinutes(30);
                    var stale = KeepPrevious(provider, code, message);
                    stale.Status = ProviderStatus.StaleAuth;
                    return stale;
                }

                int failures;
                _failures.TryGetValue(provider.Id, out failures);
                failures++;
                _failures[provider.Id] = failures;

                var delayMinutes = Math.Min(15, Math.Pow(2, Math.Min(failures, 4)));
                _blockedUntil[provider.Id] = DateTimeOffset.UtcNow.AddMinutes(delayMinutes);

                Log.Error("quota/" + provider.Id + ": " + code);

                var fallback = KeepPrevious(provider, code, message);
                fallback.Status = ProviderStatus.Stale;
                return fallback;
            }
        }

        private ProviderSnapshot KeepPrevious(IQuotaProvider provider, string code, string message)
        {
            ProviderSnapshot previous = null;
            lock (_snapshots)
            {
                for (int i = 0; i < _snapshots.Count; i++)
                {
                    if (_snapshots[i].Id == provider.Id) { previous = _snapshots[i]; break; }
                }
            }

            if (previous == null || (!previous.HasData && previous.Status != ProviderStatus.Stale))
            {
                return new ProviderSnapshot
                {
                    Id = provider.Id,
                    Name = provider.Name,
                    Status = ProviderStatus.Error,
                    ErrorCode = code,
                    ErrorMessage = message,
                    FetchedAt = DateTimeOffset.UtcNow
                };
            }

            var clone = Clone(previous);
            clone.Status = ProviderStatus.Stale;
            clone.ErrorCode = code;
            clone.ErrorMessage = message;
            clone.FromCache = true;
            return clone;
        }

        private void PreserveConsumption(ProviderSnapshot snapshot)
        {
            ProviderSnapshot previous = null;
            lock (_snapshots)
            {
                for (int i = 0; i < _snapshots.Count; i++)
                {
                    if (_snapshots[i].Id == snapshot.Id) { previous = _snapshots[i]; break; }
                }
            }

            if (previous == null || previous.Consumption.Count == 0) return;
            if (snapshot.Consumption.Count > 0) return;

            snapshot.Consumption.AddRange(previous.Consumption);
        }

        private bool IsEnabled(string id)
        {
            if (id == "opencode-go") return Config.OpenCodeEnabled;
            if (id == "codex") return Config.CodexEnabled;
            if (id == "claude") return Config.ClaudeEnabled;
            return false;
        }

        private static ProviderSnapshot Clone(ProviderSnapshot source)
        {
            var clone = new ProviderSnapshot
            {
                Id = source.Id,
                Name = source.Name,
                Plan = source.Plan,
                Status = source.Status,
                FetchedAt = source.FetchedAt,
                ErrorCode = source.ErrorCode,
                ErrorMessage = source.ErrorMessage,
                FromCache = source.FromCache
            };

            for (int i = 0; i < source.Windows.Count; i++)
            {
                var window = source.Windows[i];
                clone.Windows.Add(new UsageWindow
                {
                    Id = window.Id,
                    Label = window.Label,
                    UsedPercent = window.UsedPercent,
                    ResetsAt = window.ResetsAt,
                    WindowMinutes = window.WindowMinutes,
                    IsCycle = window.IsCycle
                });
            }

            for (int i = 0; i < source.Consumption.Count; i++)
            {
                var item = source.Consumption[i];
                clone.Consumption.Add(new Consumption
                {
                    Period = item.Period,
                    CostUsd = item.CostUsd,
                    Input = item.Input,
                    Output = item.Output,
                    Reasoning = item.Reasoning,
                    CacheRead = item.CacheRead,
                    CacheWrite = item.CacheWrite
                });
            }

            return clone;
        }

        private void Raise()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        public void Dispose()
        {
            _disposed = true;
            _cancellation.Cancel();
            if (_quotaTimer != null) _quotaTimer.Dispose();
            if (_consumptionTimer != null) _consumptionTimer.Dispose();
        }
    }
}
