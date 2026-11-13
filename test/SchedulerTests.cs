using System;
using System.Collections.Generic;
using System.Threading;
using AiUsageWidget.Providers;

namespace AiUsageWidget.Tests
{
    public static class SchedulerTests
    {
        private static AppState NewState(
            TempDirectory temp,
            FakeClock clock,
            IList<IQuotaProvider> providers,
            IConsumptionService consumption,
            FakeAutostart autostart = null)
        {
            var store = new ConfigStore(temp.Combine("config"));
            return new AppState(store, clock, providers, consumption, autostart ?? new FakeAutostart());
        }

        private static void RefreshAndWait(AppState state)
        {
            state.RefreshNow();
            Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(15)), "Refresh did not finish.");
        }

        private static void PulseAndWait(AppState state)
        {
            state.Pulse();
            Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(15)), "Pulse did not finish.");
        }

        [Test]
        public static void ReturnedStaleAuth_BlocksAutomaticRetriesForThirtyMinutes()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude")
                {
                    Handler = (call, context, token) =>
                    {
                        var snapshot = TestData.OkSnapshot("claude", 50);
                        snapshot.Status = ProviderStatus.StaleAuth;
                        snapshot.ErrorCode = "auth";
                        snapshot.ErrorMessage = "expired";
                        return snapshot;
                    }
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                try
                {
                    state.Start();
                    Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(15)));
                    Assert.Equal(1, provider.Calls);

                    clock.AdvanceMinutes(10);
                    PulseAndWait(state);
                    Assert.Equal(1, provider.Calls, "Auth backoff was bypassed by the automatic timer.");
                    Assert.Equal(ProviderStatus.StaleAuth, state.Snapshots[0].Status);
                    Assert.Equal("auth", state.Snapshots[0].ErrorCode);

                    RefreshAndWait(state);
                    Assert.Equal(2, provider.Calls, "Manual refresh should be able to retry.");

                    state.RefreshAfterReset();
                    Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(15)));
                    Assert.Equal(2, provider.Calls, "Countdown refresh must not bypass auth backoff.");

                    clock.AdvanceMinutes(31);
                    PulseAndWait(state);
                    Assert.Equal(3, provider.Calls);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void Thrown401_IsAuthBackoffAndThrown403IsTransient()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("codex")
                {
                    Handler = (call, context, token) =>
                    {
                        throw new QuotaException("http_401", "expired", 401);
                    }
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                try
                {
                    state.Start();
                    Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(15)));
                    Assert.Equal(ProviderStatus.StaleAuth, state.Snapshots[0].Status);

                    clock.AdvanceMinutes(10);
                    PulseAndWait(state);
                    Assert.Equal(1, provider.Calls);

                    clock.AdvanceMinutes(21);
                    PulseAndWait(state);
                    Assert.Equal(2, provider.Calls);
                }
                finally
                {
                    state.Dispose();
                }

                clock = new FakeClock();
                provider = new FakeProvider("codex")
                {
                    Handler = (call, context, token) =>
                    {
                        throw new QuotaException("http_403", "forbidden", 403);
                    }
                };

                using (var temp2 = new TempDirectory())
                {
                    var state2 = NewState(temp2, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                    try
                    {
                        state2.Start();
                        Assert.True(state2.WaitForIdle(TimeSpan.FromSeconds(15)));
                        Assert.Equal(1, provider.Calls);

                        clock.AdvanceMinutes(1);
                        PulseAndWait(state2);
                        Assert.Equal(2, provider.Calls, "403 should be retried as a transient error.");
                    }
                    finally
                    {
                        state2.Dispose();
                    }
                }
            }
        }

        [Test]
        public static void TransientDelays_MatchOneTwoFourEightFifteenMinutes()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude")
                {
                    Handler = (call, context, token) =>
                    {
                        throw new QuotaException("network", "down", 0);
                    }
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                try
                {
                    state.Start();
                    Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(15)));
                    Assert.Equal(1, provider.Calls);

                    clock.Advance(TimeSpan.FromSeconds(59));
                    PulseAndWait(state);
                    Assert.Equal(1, provider.Calls);

                    clock.Advance(TimeSpan.FromSeconds(1));
                    PulseAndWait(state);
                    Assert.Equal(2, provider.Calls);

                    clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(59));
                    PulseAndWait(state);
                    Assert.Equal(2, provider.Calls);

                    clock.Advance(TimeSpan.FromSeconds(1));
                    PulseAndWait(state);
                    Assert.Equal(3, provider.Calls);

                    clock.Advance(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(59));
                    PulseAndWait(state);
                    Assert.Equal(3, provider.Calls);

                    clock.Advance(TimeSpan.FromSeconds(1));
                    PulseAndWait(state);
                    Assert.Equal(4, provider.Calls);

                    clock.Advance(TimeSpan.FromMinutes(7) + TimeSpan.FromSeconds(59));
                    PulseAndWait(state);
                    Assert.Equal(4, provider.Calls);

                    clock.Advance(TimeSpan.FromSeconds(1));
                    PulseAndWait(state);
                    Assert.Equal(5, provider.Calls);

                    clock.Advance(TimeSpan.FromMinutes(15));
                    PulseAndWait(state);
                    Assert.Equal(6, provider.Calls);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void SuccessResetsFailureSequence()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude");
                provider.Handler = (call, context, token) =>
                {
                    if (call <= 2) throw new QuotaException("network", "down", 0);
                    if (call == 3) return TestData.OkSnapshot("claude", 10);
                    throw new QuotaException("network", "down", 0);
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                try
                {
                    state.Start();
                    Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(15)));

                    clock.AdvanceMinutes(1);
                    PulseAndWait(state);
                    Assert.Equal(2, provider.Calls);

                    clock.AdvanceMinutes(2);
                    PulseAndWait(state);
                    Assert.Equal(3, provider.Calls);
                    Assert.Equal(ProviderStatus.Ok, state.Snapshots[0].Status);

                    clock.AdvanceMinutes(5);
                    PulseAndWait(state);
                    Assert.Equal(4, provider.Calls);

                    clock.AdvanceSeconds(30);
                    PulseAndWait(state);
                    Assert.Equal(4, provider.Calls, "Failures should restart at one minute after a success.");

                    clock.AdvanceSeconds(30);
                    PulseAndWait(state);
                    Assert.Equal(5, provider.Calls);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void Refreshing_IsClearedAfterProviderException()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude")
                {
                    Handler = (call, context, token) => { throw new InvalidOperationException("boom"); }
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                try
                {
                    RefreshAndWait(state);
                    Assert.False(state.Refreshing);
                    Assert.Equal(ProviderStatus.Stale, state.Snapshots[0].Status);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void QueueRefresh_CoalescesPendingWorkInsteadOfDroppingIt()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var gate = new ManualResetEventSlim(false);
                var started = new ManualResetEventSlim(false);
                var calls = 0;
                var provider = new FakeProvider("claude");
                provider.Handler = (call, context, token) =>
                {
                    Interlocked.Increment(ref calls);
                    if (call == 1)
                    {
                        started.Set();
                        gate.Wait(5000);
                    }
                    return TestData.OkSnapshot("claude", 10);
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                try
                {
                    state.Start();
                    Assert.True(started.Wait(5000), "The first provider call never started.");

                    for (int i = 0; i < 5; i++) state.RefreshNow();
                    gate.Set();

                    Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(15)));
                    Assert.Equal(2, calls, "provider calls=" + provider.Calls + " local=" + calls + " refreshing=" + state.Refreshing);
                }
                finally
                {
                    gate.Set();
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void DisposeDuringRefresh_CancelsWorkAndHidesLaterCallbacks()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude")
                {
                    Handler = (call, context, token) =>
                    {
                        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                        token.ThrowIfCancellationRequested();
                        return TestData.OkSnapshot("claude", 10);
                    }
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                var changes = 0;
                state.Changed += () => Interlocked.Increment(ref changes);

                state.RefreshNow();
                Thread.Sleep(150);
                state.Dispose();

                var baseline = changes;
                state.RefreshNow();
                state.Pulse();
                Thread.Sleep(200);

                Assert.Equal(baseline, changes);
                Assert.False(state.Refreshing);
            }
        }

        [Test]
        public static void DisabledProvider_IsNotFetched()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude");
                var store = new ConfigStore(temp.Combine("config"));
                store.Current.ClaudeEnabled = false;
                store.Save();

                var state = new AppState(store, clock, new List<IQuotaProvider> { provider },
                    new FakeConsumption(), new FakeAutostart());
                try
                {
                    RefreshAndWait(state);
                    Assert.Equal(0, provider.Calls);
                    Assert.Equal(ProviderStatus.Disabled, state.Snapshots[0].Status);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void UnsupportedPlan_DoesNotEnterLongBackoff()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude")
                {
                    Handler = (call, context, token) =>
                    {
                        var snapshot = TestData.OkSnapshot("claude", 0);
                        snapshot.Status = ProviderStatus.Unsupported;
                        snapshot.ErrorCode = "no_rate_limit";
                        return snapshot;
                    }
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                try
                {
                    state.Start();
                    Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(15)));
                    Assert.Equal(1, provider.Calls);

                    clock.AdvanceMinutes(5);
                    PulseAndWait(state);
                    Assert.Equal(2, provider.Calls);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void OneProviderFailure_DoesNotHideAnotherProvider()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var failing = new FakeProvider("claude")
                {
                    Handler = (call, context, token) => { throw new QuotaException("network", "down", 0); }
                };
                var healthy = new FakeProvider("codex");

                var state = NewState(temp, clock, new List<IQuotaProvider> { failing, healthy }, new FakeConsumption());
                try
                {
                    RefreshAndWait(state);
                    var snapshots = state.Snapshots;
                    Assert.Equal(ProviderStatus.Stale, snapshots[0].Status);
                    Assert.Equal(ProviderStatus.Ok, snapshots[1].Status);
                    Assert.True(state.LastRunHadErrors);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void FailureKeepsLastGoodDataButSeparatesLastAttemptFromLastSuccess()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude");
                provider.Handler = (call, context, token) =>
                {
                    if (call == 1) return TestData.OkSnapshot("claude", 42);
                    throw new QuotaException("network", "down", 0);
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                try
                {
                    RefreshAndWait(state);
                    var success = state.LastSuccessfulRefresh;
                    var fetchedAt = state.Snapshots[0].FetchedAt;

                    clock.AdvanceMinutes(10);
                    RefreshAndWait(state);

                    var stale = state.Snapshots[0];
                    Assert.Equal(ProviderStatus.Stale, stale.Status);
                    Assert.True(stale.FromCache);
                    Assert.Equal(42, (int)stale.Windows[0].UsedPercent);
                    Assert.Equal(fetchedAt, stale.FetchedAt);
                    Assert.Equal(success, state.LastSuccessfulRefresh);
                    Assert.True(state.LastAttempt > success);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void ConsumptionFailureKeepsPriorDataMarkedStale()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude");
                var consumption = new FakeConsumption
                {
                    Handler = (id, context) =>
                    {
                        var result = ConsumptionResult.Ok();
                        result.Items.Add(new Consumption { Period = "today", Total = 100 });
                        return result;
                    }
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, consumption);
                try
                {
                    RefreshAndWait(state);
                    var first = state.Snapshots[0];
                    Assert.Equal(100, first.Consumption[0].TotalTokens);
                    var updatedAt = first.ConsumptionUpdatedAt;

                    consumption.Handler = (id, context) => ConsumptionResult.Failed("read_failed");
                    RefreshAndWait(state);

                    var stale = state.Snapshots[0];
                    Assert.Equal(100, stale.Consumption[0].TotalTokens);
                    Assert.True(stale.ConsumptionStale);
                    Assert.Equal("read_failed", stale.ConsumptionError);
                    Assert.Equal(updatedAt, stale.ConsumptionUpdatedAt);

                    consumption.Handler = (id, context) => ConsumptionResult.Unavailable("service_not_running");
                    RefreshAndWait(state);

                    var unavailable = state.Snapshots[0];
                    Assert.Equal(0, unavailable.Consumption.Count);
                    Assert.Null(unavailable.ConsumptionUpdatedAt);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void SubscriberFailure_DoesNotLeaveRefreshingStuckOrSkipOtherHandlers()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude");
                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                var secondHandlerCalled = 0;

                state.Changed += () => { throw new InvalidOperationException("subscriber boom"); };
                state.Changed += () => Interlocked.Increment(ref secondHandlerCalled);

                try
                {
                    RefreshAndWait(state);
                    Assert.False(state.Refreshing);
                    Assert.True(secondHandlerCalled > 0);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void DisposeDuringConsumption_CancelsWithoutWaitingForTheDeadline()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var started = new ManualResetEventSlim(false);
                var provider = new FakeProvider("claude");
                var consumption = new FakeConsumption
                {
                    Handler = (id, context) =>
                    {
                        started.Set();
                        return ConsumptionResult.NoData(null);
                    }
                };

                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, consumption);
                state.RefreshNow();
                Assert.True(started.Wait(5000));

                var watch = System.Diagnostics.Stopwatch.StartNew();
                state.Dispose();
                watch.Stop();

                Assert.True(watch.ElapsedMilliseconds < 3000,
                    "Dispose waited " + watch.ElapsedMilliseconds + " ms.");
                Assert.False(state.Refreshing);
            }
        }

        [Test]
        public static void RepeatedScheduleTimers_DoesNotDuplicateRefreshes()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var provider = new FakeProvider("claude");
                var state = NewState(temp, clock, new List<IQuotaProvider> { provider }, new FakeConsumption());
                try
                {
                    for (int i = 0; i < 5; i++) state.ScheduleTimers();
                    RefreshAndWait(state);
                    Assert.Equal(1, provider.Calls);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void ConfigurationSnapshot_IsStableForTheDurationOfARun()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var started = new ManualResetEventSlim(false);
                var release = new ManualResetEventSlim(false);
                var observedQuota = -1;
                var provider = new FakeProvider("claude");
                provider.Handler = (call, context, token) =>
                {
                    observedQuota = context.Config.QuotaMinutes;
                    started.Set();
                    release.Wait(5000);
                    return TestData.OkSnapshot("claude", 10);
                };

                var store = new ConfigStore(temp.Combine("config"));
                var state = new AppState(store, clock, new List<IQuotaProvider> { provider },
                    new FakeConsumption(), new FakeAutostart());

                try
                {
                    state.RefreshNow();
                    Assert.True(started.Wait(5000));

                    store.Current.QuotaMinutes = 9;
                    Assert.True(store.Save().Success);
                    release.Set();

                    Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(10)));
                    Assert.Equal(5, observedQuota);
                }
                finally
                {
                    release.Set();
                    state.Dispose();
                }
            }
        }

        [Test]
        public static void ShowConsumptionDisabled_SkipsCollectionWork()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var store = new ConfigStore(temp.Combine("config"));
                store.Current.ShowConsumption = false;
                store.Save();

                var provider = new FakeProvider("claude");
                var consumption = new FakeConsumption();
                var state = new AppState(store, clock, new List<IQuotaProvider> { provider },
                    consumption, new FakeAutostart());
                try
                {
                    RefreshAndWait(state);
                    Assert.Equal(0, consumption.Calls);

                    clock.AdvanceMinutes(60);
                    PulseAndWait(state);
                    Assert.Equal(0, consumption.Calls);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }
    }
}
