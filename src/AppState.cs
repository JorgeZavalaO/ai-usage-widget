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
        public readonly IAutostart Autostart;

        private readonly IClock _clock;
        private readonly List<IQuotaProvider> _quotaProviders;
        private readonly IConsumptionService _consumption;

        private readonly List<ProviderSnapshot> _snapshots = new List<ProviderSnapshot>();
        private readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _blockedUntil =
            new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private readonly object _gate = new object();
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);

        private Timer _timer;
        private bool _running;
        private bool _disposed;
        private bool _pendingQuota;
        private bool _pendingConsumption;
        private bool _pendingManual;
        private DateTimeOffset _nextQuotaDue;
        private DateTimeOffset _nextConsumptionDue;

        private static readonly int[] TransientDelaysMinutes = { 1, 2, 4, 8, 15 };
        private const int AuthBackoffMinutes = 30;
        private const int SchedulerTickSeconds = 15;

        public bool Refreshing { get; private set; }
        public DateTimeOffset LastAttempt { get; private set; }
        public DateTimeOffset LastSuccessfulRefresh { get; private set; }
        public bool LastRunHadErrors { get; private set; }

        public event Action Changed;

        public AppState(ConfigStore store)
            : this(
                store,
                SystemClock.Instance,
                null,
                null,
                new RegistryAutostart())
        {
        }

        public AppState(
            ConfigStore store,
            IClock clock,
            IList<IQuotaProvider> providers,
            IConsumptionService consumption,
            IAutostart autostart)
        {
            Store = store;
            Autostart = autostart ?? new RegistryAutostart();
            _clock = clock ?? SystemClock.Instance;
            _consumption = consumption ?? new ConsumptionService(_clock, new ProcessRunner());

            _quotaProviders = new List<IQuotaProvider>();
            if (providers != null)
            {
                _quotaProviders.AddRange(providers);
            }
            else
            {
                _quotaProviders.Add(new OpenCodeGoProvider());
                _quotaProviders.Add(new CodexProvider());
                _quotaProviders.Add(new ClaudeProvider());
            }

            LastAttempt = DateTimeOffset.MinValue;
            LastSuccessfulRefresh = DateTimeOffset.MinValue;
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
            ScheduleTimers();
            QueueRefresh(true, true, false);
        }

        public void ScheduleTimers()
        {
            var config = Store.Current;
            var now = _clock.UtcNow;

            lock (_gate)
            {
                if (_disposed) return;
                _nextQuotaDue = now.AddMinutes(ClampQuota(config.QuotaMinutes));
                foreach (var pair in _blockedUntil)
                {
                    if (pair.Value < _nextQuotaDue) _nextQuotaDue = pair.Value;
                }
                _nextConsumptionDue = now.AddMinutes(ClampConsumption(config.ConsumptionMinutes));
            }

            if (_timer != null) _timer.Dispose();
            _timer = new Timer(
                _ => Pulse(),
                null,
                TimeSpan.FromSeconds(SchedulerTickSeconds),
                TimeSpan.FromSeconds(SchedulerTickSeconds));
        }

        /// <summary>Explicit user action. Bypasses automatic backoff once.</summary>
        public void RefreshNow()
        {
            QueueRefresh(true, true, true);
        }

        /// <summary>
        /// Timer-driven refresh after a countdown expires. It must not bypass an
        /// authentication backoff that automatic polling is honoring.
        /// </summary>
        public void RefreshAfterReset()
        {
            QueueRefresh(true, true, false);
        }

        /// <summary>
        /// Runs due timer work. Public so tests can drive a controllable clock;
        /// production calls it from the scheduler timer.
        /// </summary>
        internal void Pulse()
        {
            if (_disposed) return;

            var config = Store.Current;
            var now = _clock.UtcNow;
            bool quotaDue;
            bool consumptionDue;

            lock (_gate)
            {
                if (_disposed) return;
                quotaDue = now >= _nextQuotaDue;
                consumptionDue = config.ShowConsumption && now >= _nextConsumptionDue;

                if (quotaDue) _nextQuotaDue = now.AddMinutes(ClampQuota(config.QuotaMinutes));
                if (consumptionDue) _nextConsumptionDue = now.AddMinutes(ClampConsumption(config.ConsumptionMinutes));
            }

            if (quotaDue || consumptionDue)
            {
                QueueRefresh(quotaDue, consumptionDue, false);
            }
        }

        internal bool WaitForIdle(TimeSpan timeout)
        {
            return _idle.Wait(timeout);
        }

        private void QueueRefresh(bool quota, bool consumption, bool manual)
        {
            if (!quota && !consumption) return;

            lock (_gate)
            {
                if (_disposed) return;

                _pendingQuota |= quota;
                _pendingConsumption |= consumption;
                _pendingManual |= manual;

                if (_running) return;

                _running = true;
                _idle.Reset();
            }

            Task.Run(new Action(RunLoop));
        }

        private void RunLoop()
        {
            try
            {
                while (true)
                {
                    bool quota;
                    bool consumption;
                    bool manual;

                    lock (_gate)
                    {
                        if (_disposed) return;
                        if (!_pendingQuota && !_pendingConsumption) return;

                        quota = _pendingQuota;
                        consumption = _pendingConsumption;
                        manual = _pendingManual;

                        _pendingQuota = false;
                        _pendingConsumption = false;
                        _pendingManual = false;
                    }

                    Execute(quota, consumption, manual);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("refresh: " + ex.GetType().Name);
            }
            finally
            {
                lock (_gate)
                {
                    _running = false;
                    _idle.Set();
                }
            }
        }

        private void Execute(bool quota, bool consumption, bool manual)
        {
            var token = _cancellation.Token;

            try
            {
                if (token.IsCancellationRequested) return;

                Refreshing = quota || consumption;
                Raise();

                var config = Store.Current.Clone();
                var context = new FetchContext
                {
                    Config = config,
                    OpenCodeKey = Store.GetOpenCodeKey(),
                    Home = ProviderUtil.Home,
                    Transport = HttpTransport.Default,
                    Clock = _clock,
                    ProcessRunner = new ProcessRunner()
                };

                if (quota)
                {
                    var now = _clock.UtcNow;
                    LastAttempt = now;

                    var fresh = new List<ProviderSnapshot>(_quotaProviders.Count);
                    var anySuccess = false;
                    var anyError = false;

                    for (int i = 0; i < _quotaProviders.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        var snapshot = FetchOne(_quotaProviders[i], context, config, manual, now);
                        fresh.Add(snapshot);

                        if (snapshot.Status == ProviderStatus.Ok) anySuccess = true;
                        if (snapshot.Status == ProviderStatus.Error ||
                            snapshot.Status == ProviderStatus.Stale ||
                            snapshot.Status == ProviderStatus.StaleAuth) anyError = true;
                    }

                    if (!token.IsCancellationRequested)
                    {
                        lock (_snapshots)
                        {
                            _snapshots.Clear();
                            _snapshots.AddRange(fresh);
                        }

                        if (anySuccess) LastSuccessfulRefresh = now;
                        LastRunHadErrors = anyError;
                        _nextQuotaDue = ComputeNextQuotaDue(now, config);
                    }
                }

                Raise();
                token.ThrowIfCancellationRequested();

                if (consumption && config.ShowConsumption)
                {
                    FetchConsumption(context, token, config);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("refresh: " + ex.GetType().Name);
            }
            finally
            {
                Refreshing = false;
                Raise();
            }
        }

        private ProviderSnapshot FetchOne(
            IQuotaProvider provider,
            FetchContext context,
            AppConfig config,
            bool manual,
            DateTimeOffset now)
        {
            if (!IsEnabled(provider.Id, config))
            {
                ClearBackoff(provider.Id);
                return new ProviderSnapshot
                {
                    Id = provider.Id,
                    Name = provider.Name,
                    Status = ProviderStatus.Disabled,
                    FetchedAt = DateTimeOffset.MinValue,
                    LastAttemptAt = now
                };
            }

            DateTimeOffset blockedUntil;
            if (!manual && _blockedUntil.TryGetValue(provider.Id, out blockedUntil) && now < blockedUntil)
            {
                var previous = FindPrevious(provider.Id);
                if (previous != null)
                {
                    var waiting = Clone(previous);
                    waiting.LastAttemptAt = now;
                    return waiting;
                }

                return new ProviderSnapshot
                {
                    Id = provider.Id,
                    Name = provider.Name,
                    Status = ProviderStatus.Error,
                    ErrorCode = "backoff",
                    ErrorMessage = "Waiting before the next retry.",
                    FetchedAt = DateTimeOffset.MinValue,
                    LastAttemptAt = now
                };
            }

            try
            {
                var snapshot = provider.Fetch(context, _cancellation.Token);
                snapshot.LastAttemptAt = now;

                if (snapshot.Status == ProviderStatus.Ok)
                {
                    ClearBackoff(provider.Id);
                    PreserveConsumption(snapshot);
                    ProviderUtil.SortWindows(snapshot);
                    return snapshot;
                }

                if (snapshot.Status == ProviderStatus.StaleAuth)
                {
                    BlockFor(provider.Id, now, AuthBackoffMinutes);
                    PreserveConsumption(snapshot);
                    return snapshot;
                }

                if (snapshot.Status == ProviderStatus.Error || snapshot.Status == ProviderStatus.Stale)
                {
                    BlockTransient(provider.Id, now);
                    PreserveConsumption(snapshot);
                    return snapshot;
                }

                // NoCredentials / Unsupported / Disabled: a retry cannot fix these,
                // so no backoff is recorded and normal polling continues.
                ClearBackoff(provider.Id);
                PreserveConsumption(snapshot);
                return snapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var quotaError = ex as QuotaException;
                var code = quotaError != null ? quotaError.Code : ex.GetType().Name;
                var message = quotaError != null ? quotaError.Message : "Unexpected provider failure.";

                ProviderSnapshot fallback;

                if (quotaError != null && quotaError.HttpStatus == 401)
                {
                    BlockFor(provider.Id, now, AuthBackoffMinutes);
                    fallback = KeepPrevious(provider, code, message, now);
                    fallback.Status = ProviderStatus.StaleAuth;
                    return fallback;
                }

                BlockTransient(provider.Id, now);
                Log.Error("quota/" + provider.Id + ": " + code);

                fallback = KeepPrevious(provider, code, message, now);
                fallback.Status = ProviderStatus.Stale;
                return fallback;
            }
        }

        private void FetchConsumption(FetchContext context, CancellationToken token, AppConfig config)
        {
            var current = Snapshots;
            var next = new List<ProviderSnapshot>(current.Count);
            var changed = false;
            var now = _clock.UtcNow;

            for (int i = 0; i < current.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var snapshot = current[i];
                if (snapshot.Status == ProviderStatus.Disabled)
                {
                    next.Add(snapshot);
                    continue;
                }

                var updated = ApplyConsumption(snapshot, context, token, now);
                next.Add(updated);
                if (!ConsumptionEquals(snapshot, updated)) changed = true;
            }

            if (!changed || token.IsCancellationRequested) return;

            lock (_snapshots)
            {
                _snapshots.Clear();
                _snapshots.AddRange(next);
            }
        }

        private ProviderSnapshot ApplyConsumption(
            ProviderSnapshot snapshot,
            FetchContext context,
            CancellationToken token,
            DateTimeOffset now)
        {
            var clone = Clone(snapshot);
            var result = _consumption.Fetch(snapshot.Id, context, token);

            clone.Consumption.Clear();

            if (result.Status == ConsumptionStatus.Ok)
            {
                clone.Consumption.AddRange(result.Items);
                clone.ConsumptionUpdatedAt = now;
                clone.ConsumptionError = result.Partial ? result.ErrorCode : null;
                clone.ConsumptionStale = false;
                return clone;
            }

            if (result.Status == ConsumptionStatus.NoData)
            {
                clone.ConsumptionUpdatedAt = now;
                clone.ConsumptionError = result.ErrorCode;
                clone.ConsumptionStale = false;
                return clone;
            }

            if (result.Status == ConsumptionStatus.Unavailable)
            {
                // The source disappeared (for example the OpenCode service stopped).
                // Do not present previous totals as current.
                clone.ConsumptionUpdatedAt = null;
                clone.ConsumptionError = result.ErrorCode;
                clone.ConsumptionStale = false;
                return clone;
            }

            // Failed: keep last good totals but mark them stale, never as fresh.
            clone.Consumption.AddRange(snapshot.Consumption);
            clone.ConsumptionUpdatedAt = snapshot.ConsumptionUpdatedAt;
            clone.ConsumptionError = result.ErrorCode;
            clone.ConsumptionStale = snapshot.Consumption.Count > 0;
            return clone;
        }

        private ProviderSnapshot FindPrevious(string providerId)
        {
            lock (_snapshots)
            {
                for (int i = 0; i < _snapshots.Count; i++)
                {
                    if (_snapshots[i].Id == providerId) return _snapshots[i];
                }
            }
            return null;
        }

        private ProviderSnapshot KeepPrevious(
            IQuotaProvider provider,
            string code,
            string message,
            DateTimeOffset now)
        {
            var previous = FindPrevious(provider.Id);

            if (previous == null || !previous.HasData)
            {
                return new ProviderSnapshot
                {
                    Id = provider.Id,
                    Name = provider.Name,
                    Status = ProviderStatus.Error,
                    ErrorCode = code,
                    ErrorMessage = message,
                    FetchedAt = DateTimeOffset.MinValue,
                    LastAttemptAt = now
                };
            }

            var clone = Clone(previous);
            clone.Status = ProviderStatus.Stale;
            clone.ErrorCode = code;
            clone.ErrorMessage = message;
            clone.FromCache = true;
            clone.LastAttemptAt = now;
            return clone;
        }

        private void PreserveConsumption(ProviderSnapshot snapshot)
        {
            if (snapshot.Consumption.Count > 0) return;

            var previous = FindPrevious(snapshot.Id);
            if (previous == null || previous.Consumption.Count == 0) return;

            snapshot.Consumption.AddRange(previous.Consumption);
            snapshot.ConsumptionUpdatedAt = previous.ConsumptionUpdatedAt;
            snapshot.ConsumptionError = previous.ConsumptionError;
            snapshot.ConsumptionStale = previous.ConsumptionStale;
        }

        private void BlockTransient(string providerId, DateTimeOffset now)
        {
            int failures;
            _failures.TryGetValue(providerId, out failures);
            failures++;
            _failures[providerId] = failures;

            var index = Math.Min(failures - 1, TransientDelaysMinutes.Length - 1);
            BlockFor(providerId, now, TransientDelaysMinutes[index]);
        }

        private void BlockFor(string providerId, DateTimeOffset now, int minutes)
        {
            _blockedUntil[providerId] = now.AddMinutes(minutes);
        }

        private void ClearBackoff(string providerId)
        {
            _failures.Remove(providerId);
            _blockedUntil.Remove(providerId);
        }

        /// <summary>
        /// The next automatic quota wake-up is the normal poll interval, pulled
        /// earlier when a provider's retry backoff expires first. This is what
        /// makes 1/2/4/8/15 minutes observable with a 5-minute poll cadence.
        /// </summary>
        private DateTimeOffset ComputeNextQuotaDue(DateTimeOffset now, AppConfig config)
        {
            var next = now.AddMinutes(ClampQuota(config.QuotaMinutes));

            lock (_gate)
            {
                foreach (var pair in _blockedUntil)
                {
                    if (pair.Value <= now) return now;
                    if (pair.Value < next) next = pair.Value;
                }
            }

            return next;
        }

        private static int ClampQuota(int minutes)
        {
            if (minutes < 1) return 1;
            if (minutes > 1440) return 1440;
            return minutes;
        }

        private static int ClampConsumption(int minutes)
        {
            if (minutes < 5) return 5;
            if (minutes > 1440) return 1440;
            return minutes;
        }

        private bool IsEnabled(string id, AppConfig config)
        {
            if (id == "opencode-go") return config.OpenCodeEnabled;
            if (id == "codex") return config.CodexEnabled;
            if (id == "claude") return config.ClaudeEnabled;
            return false;
        }

        internal static ProviderSnapshot Clone(ProviderSnapshot source)
        {
            var clone = new ProviderSnapshot
            {
                Id = source.Id,
                Name = source.Name,
                Plan = source.Plan,
                Status = source.Status,
                FetchedAt = source.FetchedAt,
                LastAttemptAt = source.LastAttemptAt,
                ConsumptionUpdatedAt = source.ConsumptionUpdatedAt,
                ConsumptionError = source.ConsumptionError,
                ConsumptionStale = source.ConsumptionStale,
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
                clone.Consumption.Add(CloneConsumption(source.Consumption[i]));
            }

            return clone;
        }

        private static Consumption CloneConsumption(Consumption item)
        {
            return new Consumption
            {
                Period = item.Period,
                CostUsd = item.CostUsd,
                Input = item.Input,
                Output = item.Output,
                Reasoning = item.Reasoning,
                CacheRead = item.CacheRead,
                CacheWrite = item.CacheWrite,
                Total = item.Total
            };
        }

        private static bool ConsumptionEquals(ProviderSnapshot a, ProviderSnapshot b)
        {
            if (a.ConsumptionStale != b.ConsumptionStale) return false;
            if (!string.Equals(a.ConsumptionError, b.ConsumptionError, StringComparison.Ordinal)) return false;
            if (a.ConsumptionUpdatedAt != b.ConsumptionUpdatedAt) return false;
            if (a.Consumption.Count != b.Consumption.Count) return false;

            for (int i = 0; i < a.Consumption.Count; i++)
            {
                var left = a.Consumption[i];
                var right = b.Consumption[i];
                if (left.Period != right.Period) return false;
                if (left.CostUsd != right.CostUsd) return false;
                if (left.TotalTokens != right.TotalTokens) return false;
                if (left.Input != right.Input || left.Output != right.Output) return false;
                if (left.Reasoning != right.Reasoning) return false;
                if (left.CacheRead != right.CacheRead || left.CacheWrite != right.CacheWrite) return false;
            }

            return true;
        }

        private void Raise()
        {
            if (_disposed) return;

            var handler = Changed;
            if (handler == null) return;

            var invocations = handler.GetInvocationList();
            for (int i = 0; i < invocations.Length; i++)
            {
                try
                {
                    ((Action)invocations[i])();
                }
                catch (Exception ex)
                {
                    Log.Error("changed: " + ex.GetType().Name);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            try { _cancellation.Cancel(); }
            catch (Exception) { }

            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }

            // Do not block shutdown for a full network timeout; cancellation
            // aborts the in-flight request. A short grace period is enough for
            // the worker to observe it.
            _idle.Wait(2000);
        }
    }
}
