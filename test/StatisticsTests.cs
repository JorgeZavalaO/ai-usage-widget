using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AiUsageWidget.Providers;
using AiUsageWidget.UI;

namespace AiUsageWidget.Tests
{
    public static class StatisticsTests
    {
        private const string CatalogJson =
            "{\"x\":{\"id\":\"anthropic\"}," +
            "\"anthropic\":{\"id\":\"anthropic\",\"models\":{" +
            "\"claude-opus-5-5\":{\"cost\":{\"input\":4,\"output\":20,\"cache_read\":0.2,\"cache_write\":5}}," +
            "\"unpriced\":{\"name\":\"no cost block\"}}}," +
            "\"openai\":{\"id\":\"openai\",\"models\":{" +
            "\"gpt-6-luna\":{\"cost\":{\"input\":2,\"output\":8,\"cache_read\":0.5}}}}," +
            "\"other\":{\"id\":\"other\",\"models\":{" +
            "\"x\":{\"cost\":{\"input\":99,\"output\":99}}}}}";

        private static FetchContext Context(string home, IClock clock, string dataDirectory)
        {
            return new FetchContext
            {
                Home = home,
                Clock = clock,
                DataDirectory = dataDirectory,
                OpenCodeServiceRunning = true,
                OpenCodeExecutable = "opencode"
            };
        }

        private static DailyUsage Day(List<DailyUsage> daily, string date)
        {
            for (int i = 0; i < daily.Count; i++)
            {
                if (daily[i].Date == date) return daily[i];
            }
            throw new TestFailureException("Missing daily point: " + date);
        }

        private static string StatsJson(int days)
        {
            if (days == 0) days = 1;

            // S(k) is the cumulative window ending today: day k-1 ago contributes
            // 10k input, k output and 0.1 cost. S(0) and S(1) are both today.
            var input = 5L * days * (days + 1);
            var output = (long)days * (days + 1) / 2;
            var cost = 0.1 * days;
            return TestData.OpenCodeStats(input, output, 0, 0, 0, cost);
        }

        // ------------------------------------------------------------------ pricing

        [Test]
        public static void SubObject_SkipsMatchingStringValues()
        {
            var section = Json.SubObject(CatalogJson, "anthropic");

            Assert.NotNull(section);
            Assert.Contains("\"claude-opus-5-5\"", section);
            Assert.DoesNotContain("\"openai\"", section);
        }

        [Test]
        public static void PriceCatalogParse_KeepsOnlyPricedProviders()
        {
            var prices = PriceCatalog.Parse(CatalogJson);

            Assert.True(prices.ContainsKey("anthropic/claude-opus-5-5"));
            Assert.True(prices.ContainsKey("openai/gpt-6-luna"));
            Assert.False(prices.ContainsKey("other/x"));
            Assert.False(prices.ContainsKey("anthropic/unpriced"));
        }

        [Test]
        public static void NormalizeModel_StripsPrefixDateAndLatest()
        {
            Assert.Equal("claude-opus-5-5", PriceCatalog.NormalizeModel("anthropic/claude-opus-5-5"));
            Assert.Equal("claude-sonnet-4-5", PriceCatalog.NormalizeModel("claude-sonnet-4-5-20250929"));
            Assert.Equal("claude-sonnet-4-5", PriceCatalog.NormalizeModel("claude-sonnet-4-5-latest"));
            Assert.Equal("claude-opus-5-5", PriceCatalog.NormalizeModel("claude-opus-5-5[1m]"));
            Assert.Equal("gpt-6-luna", PriceCatalog.NormalizeModel("GPT-6-Luna"));
        }

        [Test]
        public static void ProviderOf_MapsWidgetIdsToCatalogProviders()
        {
            Assert.Equal("anthropic", PriceCatalog.ProviderOf("claude"));
            Assert.Equal("openai", PriceCatalog.ProviderOf("codex"));
            Assert.Equal("opencode-go", PriceCatalog.ProviderOf("opencode-go"));
        }

        [Test]
        public static void CostDisjoint_MatchesOpenCodeForTheSameSession()
        {
            // Real numbers: 8 input, 153596 output, 883136 cache read, 7938 cache
            // write at 4/20/0.2/5. OpenCode reports 3.2882692 for the same model.
            var price = new ModelPrice { Input = 4, Output = 20, CacheRead = 0.2, CacheWrite = 5 };

            var cost = price.CostDisjoint(8, 153596, 883136, 7938);

            Assert.Near(3.2882692, cost, 0.0000001);
        }

        [Test]
        public static void CostInclusiveInput_DoesNotBillCachedInputTwice()
        {
            var price = new ModelPrice { Input = 2, Output = 8, CacheRead = 0.5 };

            // 1M input of which 800k cached, plus 100k output.
            var cost = price.CostInclusiveInput(1000000, 100000, 800000);

            Assert.Near(1.6, cost, 0.000001);
        }

        [Test]
        public static void PriceCatalog_CachesFilteredDocumentOnDisk()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var transport = new FakeHttpTransport
                {
                    Handler = (url, headers) => new HttpResult { Status = 200, Body = CatalogJson }
                };

                var first = new PriceCatalog(temp.Path, transport, clock);
                first.EnsureLoaded(CancellationToken.None);

                Assert.Equal(1, transport.Calls);
                Assert.Equal(1, transport.Urls.Count);
                Assert.Contains("models.dev", transport.Urls[0]);
                Assert.True(File.Exists(temp.Combine("pricing.json")));

                var offline = new FakeHttpTransport();
                var second = new PriceCatalog(temp.Path, offline, clock);
                second.EnsureLoaded(CancellationToken.None);

                ModelPrice price;
                Assert.True(second.TryGet("claude", "claude-opus-5-5", out price));
                Assert.Near(20, price.Output, 0.0001);
                Assert.Equal(0, offline.Calls);
            }
        }

        [Test]
        public static void PriceCatalog_FailedRefreshKeepsStaleCache()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var online = new FakeHttpTransport
                {
                    Handler = (url, headers) => new HttpResult { Status = 200, Body = CatalogJson }
                };

                var catalog = new PriceCatalog(temp.Path, online, clock);
                catalog.EnsureLoaded(CancellationToken.None);

                clock.Advance(TimeSpan.FromHours(30));
                online.Handler = (url, headers) => new HttpResult { Status = 500 };

                catalog.EnsureLoaded(CancellationToken.None);

                ModelPrice price;
                Assert.True(catalog.TryGet("codex", "gpt-6-luna", out price));
                Assert.Contains("cached", catalog.Note);
            }
        }

        [Test]
        public static void PriceCatalog_NetworkFailureWithoutCacheStaysEmpty()
        {
            using (var temp = new TempDirectory())
            {
                var transport = new FakeHttpTransport
                {
                    Handler = (url, headers) => new HttpResult { Status = 503 }
                };

                var catalog = new PriceCatalog(temp.Path, transport, new FakeClock());
                catalog.EnsureLoaded(CancellationToken.None);

                ModelPrice price;
                Assert.False(catalog.TryGet("claude", "claude-opus-5-5", out price));
                Assert.Contains("unavailable", catalog.Note);
            }
        }

        // ------------------------------------------------------------------- daily

        [Test]
        public static void ClaudeDaily_HasThirtyPointsEndingToday()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var home = TestFile.NewHome(temp);
                temp.Write(".claude/projects/p/session.jsonl",
                    TestData.ClaudeLine(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                        "m1", 10, 1, 0, 0, "claude-opus-5-5"));
                TestFile.SetAllModified(temp.Path, clock.Utc);

                var service = new ConsumptionService(clock, new FakeProcessRunner(), new StaticPriceCatalog());
                var result = service.Fetch("claude", Context(home, clock, temp.Combine("data")), CancellationToken.None);

                Assert.Equal(30, result.Daily.Count);
                Assert.Equal("2026-10-15", result.Daily[0].Date);
                Assert.Equal("2026-11-13", result.Daily[29].Date);
                Assert.Equal(11, result.Daily[29].Billable);
                Assert.Equal(0, result.Daily[28].Billable);
                Assert.False(result.DailyIncomplete);
            }
        }

        [Test]
        public static void ClaudeDaily_EstimatesCostPerModel()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var home = TestFile.NewHome(temp);
                temp.Write(".claude/projects/p/session.jsonl",
                    TestData.ClaudeLine(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                        "m1", 1000000, 0, 0, 0, "claude-opus-5-5"));
                temp.Write(".claude/projects/p/second.jsonl",
                    TestData.ClaudeLine(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.FromHours(-5)),
                        "m2", 0, 500000, 0, 0, "claude-opus-5-5"));
                TestFile.SetAllModified(temp.Path, clock.Utc);

                var prices = new StaticPriceCatalog();
                prices.Add("claude", "claude-opus-5-5",
                    new ModelPrice { Input = 4, Output = 20, CacheRead = 0.2, CacheWrite = 5 });

                var service = new ConsumptionService(clock, new FakeProcessRunner(), prices);
                var result = service.Fetch("claude", Context(home, clock, temp.Combine("data")), CancellationToken.None);

                var today = Day(result.Daily, "2026-11-13");
                var yesterday = Day(result.Daily, "2026-11-12");

                Assert.NotNull(today.CostUsd);
                Assert.Near(4.0, today.CostUsd.Value, 0.0001);
                Assert.Near(10.0, yesterday.CostUsd.Value, 0.0001);
                Assert.Equal(0, today.UnpricedTokens);
            }
        }

        [Test]
        public static void ClaudeDaily_UnpricedModelIsCountedButNotCharged()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var home = TestFile.NewHome(temp);
                temp.Write(".claude/projects/p/session.jsonl",
                    TestData.ClaudeLine(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                        "m1", 10, 1, 0, 0, "mystery-model"));
                TestFile.SetAllModified(temp.Path, clock.Utc);

                var service = new ConsumptionService(clock, new FakeProcessRunner(), new StaticPriceCatalog());
                var result = service.Fetch("claude", Context(home, clock, temp.Combine("data")), CancellationToken.None);

                var today = Day(result.Daily, "2026-11-13");
                Assert.Null(today.CostUsd);
                Assert.Equal(11, today.UnpricedTokens);
            }
        }

        [Test]
        public static void CodexDaily_ExcludesCachedInputFromBillable()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var home = TestFile.NewHome(temp);
                var lines = new List<string>
                {
                    TestData.CodexTurnContext("gpt-6-luna"),
                    TestData.CodexLine(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                        1000000, 100000, 0, 800000)
                };
                temp.Write(".codex/sessions/2026/11/13/rollout-1.jsonl", string.Join("\n", lines.ToArray()));
                TestFile.SetAllModified(temp.Path, clock.Utc);

                var prices = new StaticPriceCatalog();
                prices.Add("codex", "gpt-6-luna", new ModelPrice { Input = 2, Output = 8, CacheRead = 0.5 });

                var service = new ConsumptionService(clock, new FakeProcessRunner(), prices);
                var result = service.Fetch("codex", Context(home, clock, temp.Combine("data")), CancellationToken.None);

                var today = Day(result.Daily, "2026-11-13");
                Assert.Equal(300000, today.Billable);
                Assert.Equal(1100000, today.TotalTokens);
                Assert.NotNull(today.CostUsd);
                Assert.Near(1.6, today.CostUsd.Value, 0.000001);
            }
        }

        [Test]
        public static void CodexModel_ComesFromTheMostRecentTurnContext()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var home = TestFile.NewHome(temp);
                var lines = new List<string>
                {
                    TestData.CodexTurnContext("first-model"),
                    TestData.CodexLine(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.FromHours(-5)),
                        1000, 0, 0, 0),
                    TestData.CodexTurnContext("second-model"),
                    TestData.CodexLine(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                        1000, 0, 0, 0)
                };
                temp.Write(".codex/sessions/2026/11/13/rollout-1.jsonl", string.Join("\n", lines.ToArray()));
                TestFile.SetAllModified(temp.Path, clock.Utc);

                var prices = new StaticPriceCatalog();
                prices.Add("codex", "first-model", new ModelPrice { Input = 1, Output = 0 });
                prices.Add("codex", "second-model", new ModelPrice { Input = 2, Output = 0 });

                var service = new ConsumptionService(clock, new FakeProcessRunner(), prices);
                var result = service.Fetch("codex", Context(home, clock, temp.Combine("data")), CancellationToken.None);

                var today = Day(result.Daily, "2026-11-13");
                Assert.NotNull(today.CostUsd);
                Assert.Near(0.003, today.CostUsd.Value, 0.0000001);
            }
        }

        // ---------------------------------------------------------------- opencode

        [Test]
        public static void OpenCode_BackfillReconstructsDaysByDifferencing()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var home = TestFile.NewHome(temp);
                var runner = new FakeProcessRunner
                {
                    Handler = (executable, arguments, timeout, token) => new ProcessResult
                    {
                        Started = true,
                        ExitCode = 0,
                        StandardOutput = StatsJson(DaysOf(arguments))
                    }
                };

                var context = Context(home, clock, temp.Combine("data"));
                context.AllowSlowBackfill = true;

                var service = new ConsumptionService(clock, runner);
                var result = service.Fetch("opencode-go", context, CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Ok, result.Status);
                Assert.Equal(30, result.Daily.Count);
                Assert.False(result.DailyIncomplete);

                var today = Day(result.Daily, "2026-11-13");
                var yesterday = Day(result.Daily, "2026-11-12");
                var twoDaysAgo = Day(result.Daily, "2026-11-11");

                Assert.Equal(11, today.Billable);
                Assert.Equal(22, yesterday.Billable);
                Assert.Equal(33, twoDaysAgo.Billable);

                Assert.NotNull(today.CostUsd);
                Assert.Near(0.1, today.CostUsd.Value, 0.000001);
                Assert.Near(0.1, yesterday.CostUsd.Value, 0.000001);

                Assert.True(File.Exists(temp.Combine("data", "usage-history.json")));
            }
        }

        [Test]
        public static void OpenCode_WithoutBackfillMarksHistoryIncomplete()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var home = TestFile.NewHome(temp);
                var runner = new FakeProcessRunner
                {
                    Handler = (executable, arguments, timeout, token) => new ProcessResult
                    {
                        Started = true,
                        ExitCode = 0,
                        StandardOutput = StatsJson(DaysOf(arguments))
                    }
                };

                var service = new ConsumptionService(clock, runner);
                var result = service.Fetch("opencode-go", Context(home, clock, temp.Combine("data")), CancellationToken.None);

                Assert.Equal(30, result.Daily.Count);
                Assert.True(result.DailyIncomplete);

                // Three period calls plus one to close yesterday exactly.
                Assert.Equal(4, runner.Calls.Count);
                Assert.Equal(22, Day(result.Daily, "2026-11-12").Billable);
            }
        }

        [Test]
        public static void OpenCode_HistorySurvivesARestart()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var home = TestFile.NewHome(temp);
                var data = temp.Combine("data");

                var runner = new FakeProcessRunner
                {
                    Handler = (executable, arguments, timeout, token) => new ProcessResult
                    {
                        Started = true,
                        ExitCode = 0,
                        StandardOutput = StatsJson(DaysOf(arguments))
                    }
                };

                var first = Context(home, clock, data);
                first.AllowSlowBackfill = true;
                new ConsumptionService(clock, runner).Fetch("opencode-go", first, CancellationToken.None);

                var second = new ConsumptionService(clock, runner);
                var result = second.Fetch("opencode-go", Context(home, clock, data), CancellationToken.None);

                Assert.False(result.DailyIncomplete);

                var yesterday = Day(result.Daily, "2026-11-12");
                Assert.Equal(22, yesterday.Billable);
            }
        }

        [Test]
        public static void OpenCode_BackfillIsNotRetriedOnTheSameDay()
        {
            using (var temp = new TempDirectory())
            {
                var clock = new FakeClock();
                var home = TestFile.NewHome(temp);
                var data = temp.Combine("data");
                var runner = new FakeProcessRunner
                {
                    Handler = (executable, arguments, timeout, token) => new ProcessResult
                    {
                        Started = true,
                        ExitCode = 0,
                        StandardOutput = StatsJson(DaysOf(arguments))
                    }
                };

                var service = new ConsumptionService(clock, runner);
                var context = Context(home, clock, data);
                context.AllowSlowBackfill = true;

                service.Fetch("opencode-go", context, CancellationToken.None);
                var afterFirst = runner.Calls.Count;
                service.Fetch("opencode-go", context, CancellationToken.None);

                Assert.Equal(afterFirst + 3, runner.Calls.Count);
            }
        }

        private static int DaysOf(string arguments)
        {
            const string marker = "--days ";
            var at = arguments.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return 0;

            var rest = arguments.Substring(at + marker.Length);
            var end = rest.IndexOf(' ');
            if (end >= 0) rest = rest.Substring(0, end);

            int days;
            return int.TryParse(rest, out days) ? days : 0;
        }

        // ------------------------------------------------------------------- chart

        [Test]
        public static void ChartMath_NiceMaxRoundsUpToAFriendlyScale()
        {
            Assert.Equal(1.0, ChartMath.NiceMax(0));
            Assert.Equal(1.0, ChartMath.NiceMax(0.7));
            Assert.Equal(2.0, ChartMath.NiceMax(1.2));
            Assert.Equal(500.0, ChartMath.NiceMax(340));
            Assert.Equal(1000.0, ChartMath.NiceMax(910));
        }

        [Test]
        public static void ChartMath_FormatsTokensAndMoney()
        {
            Assert.Equal("0", ChartMath.FormatCompact(0));
            Assert.Equal("850", ChartMath.FormatCompact(850));
            Assert.Equal("1k", ChartMath.FormatCompact(1200));
            Assert.Equal("1.5M", ChartMath.FormatCompact(1500000));
            Assert.Equal("$4.19", ChartMath.FormatMoney(4.193));
            Assert.Equal("$0.042", ChartMath.FormatMoney(0.042));
        }

        [Test]
        public static void UsageChart_IndexAtMapsTheSlotUnderThePointer()
        {
            Assert.Equal(-1, UsageChart.IndexAt(0, 620));

            var slot = UsageChart.SlotWidth(620);
            Assert.Equal(0, UsageChart.IndexAt(46 + slot / 2, 620));
            Assert.Equal(10, UsageChart.IndexAt(46 + slot * 10 + slot / 2, 620));
            Assert.Equal(29, UsageChart.IndexAt(46 + slot * 29 + slot / 2, 620));
            Assert.Equal(-1, UsageChart.IndexAt(620 + 10, 620));
        }
    }
}
