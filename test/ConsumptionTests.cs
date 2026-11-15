using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AiUsageWidget.Providers;

namespace AiUsageWidget.Tests
{
    public static class ConsumptionTests
    {
        private static FetchContext Context(string home, IClock clock)
        {
            return new FetchContext
            {
                Home = home,
                Clock = clock,
                OpenCodeServiceRunning = false
            };
        }

        private static string ClaudeRecord(DateTimeOffset timestamp, string id, long input, long output)
        {
            return TestData.ClaudeLine(timestamp, id, input, output, 2, 3);
        }

        [Test]
        public static void ClaudeDates_TodayAndSevenAndThirtyDayBoundaries()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var lines = new List<string>
                {
                    ClaudeRecord(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)), "a0", 10, 1),
                    ClaudeRecord(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.FromHours(-5)), "a6", 10, 1),
                    ClaudeRecord(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.FromHours(-5)), "a7", 10, 1),
                    ClaudeRecord(new DateTimeOffset(2026, 10, 15, 10, 0, 0, TimeSpan.FromHours(-5)), "a29", 10, 1),
                    ClaudeRecord(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.FromHours(-5)), "a30", 10, 1),
                    ClaudeRecord(new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.FromHours(-5)), "future", 10, 1)
                };
                temp.Write(".claude/projects/p/session.jsonl", string.Join("\n", lines.ToArray()));

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Ok, result.Status);
                Assert.Equal(16, TestData.Find(result.Items, "today").TotalTokens);
                Assert.Equal(32, TestData.Find(result.Items, "7d").TotalTokens);
                Assert.Equal(64, TestData.Find(result.Items, "30d").TotalTokens);
            }
        }

        [Test]
        public static void ClaudeDates_CrossMonthAndYearBoundaries()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock
                {
                    Utc = new DateTimeOffset(2027, 1, 1, 12, 0, 0, TimeSpan.Zero)
                };
                var lines = new List<string>
                {
                    ClaudeRecord(new DateTimeOffset(2027, 1, 1, 10, 0, 0, TimeSpan.FromHours(-5)), "jan1", 10, 0),
                    ClaudeRecord(new DateTimeOffset(2026, 12, 31, 10, 0, 0, TimeSpan.FromHours(-5)), "dec31", 10, 0),
                    ClaudeRecord(new DateTimeOffset(2026, 12, 26, 10, 0, 0, TimeSpan.FromHours(-5)), "dec26", 10, 0),
                    ClaudeRecord(new DateTimeOffset(2026, 12, 25, 10, 0, 0, TimeSpan.FromHours(-5)), "dec25", 10, 0),
                    ClaudeRecord(new DateTimeOffset(2026, 12, 2, 10, 0, 0, TimeSpan.FromHours(-5)), "dec2", 10, 0),
                    ClaudeRecord(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.FromHours(-5)), "dec1", 10, 0)
                };
                temp.Write(".claude/projects/p/session.jsonl", string.Join("\n", lines.ToArray()));
                TestFile.SetAllModified(temp.Path, clock.Utc);

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(15, TestData.Find(result.Items, "today").TotalTokens);
                Assert.Equal(45, TestData.Find(result.Items, "7d").TotalTokens);
                Assert.Equal(60, TestData.Find(result.Items, "30d").TotalTokens);
            }
        }

        [Test]
        public static void ClaudeDuplicates_AreCountedOnce()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var line = ClaudeRecord(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)), "same-id", 10, 1);
                temp.Write(".claude/projects/p/session.jsonl", line + "\n" + line + "\n");

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(16, TestData.Find(result.Items, "today").TotalTokens);
            }
        }

        [Test]
        public static void ClaudeMalformedLines_DoNotDiscardValidRecords()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var valid = ClaudeRecord(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)), "valid", 10, 1);
                temp.Write(".claude/projects/p/session.jsonl",
                    valid + "\n{\"type\":\"assistant\",\"usage\":\n" + valid.Replace("valid", "valid-2") + "\n");

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Ok, result.Status);
                Assert.Equal(32, TestData.Find(result.Items, "today").TotalTokens);
            }
        }

        [Test]
        public static void ClaudeMissingTimestamp_IsNotCountedAsToday()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var line = "{\"type\":\"assistant\",\"message\":{\"id\":\"no-date\",\"usage\":" +
                           "{\"input_tokens\":10,\"output_tokens\":1}}}";
                temp.Write(".claude/projects/p/session.jsonl", line);

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.NoData, result.Status);
                Assert.Equal("missing_timestamp", result.ErrorCode);
                Assert.Equal(0, result.Items.Count);
            }
        }

        [Test]
        public static void ClaudePartiallyWrittenFinalLine_PreservesEarlierRecords()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var valid = ClaudeRecord(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)), "valid", 10, 1);
                temp.Write(".claude/projects/p/session.jsonl",
                    valid + "\n{\"type\":\"assistant\",\"timestamp\":\"2026-11-13T15:00:00Z\",\"message\":{");

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Ok, result.Status);
                Assert.Equal(16, TestData.Find(result.Items, "today").TotalTokens);
            }
        }

        [Test]
        public static void ClaudeTokenBuckets_AreDisjointAndAuditable()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                temp.Write(".claude/projects/p/session.jsonl",
                    TestData.ClaudeLine(
                        new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                        "buckets", 10, 2, 3, 4));

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);
                var today = TestData.Find(result.Items, "today");

                Assert.Equal(10, today.Input);
                Assert.Equal(2, today.Output);
                Assert.Equal(3, today.CacheRead);
                Assert.Equal(4, today.CacheWrite);
                Assert.Equal(19, today.TotalTokens);
            }
        }

        [Test]
        public static void CodexTokenSemantics_DoNotDoubleCountSubsets()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                temp.Write(".codex/sessions/2026/11/13/rollout-a.jsonl",
                    TestData.CodexLine(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                        1000, 200, 50, 400));

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("codex", Context(home, clock), CancellationToken.None);
                var today = TestData.Find(result.Items, "today");

                Assert.Equal(1000, today.Input);
                Assert.Equal(200, today.Output);
                Assert.Equal(50, today.Reasoning);
                Assert.Equal(400, today.CacheRead);
                Assert.Equal(1200, today.TotalTokens);
            }
        }

        [Test]
        public static void CodexDuplicateEvents_AreCountedOnce()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var line = TestData.CodexLine(
                    new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                    100, 10);
                temp.Write(".codex/sessions/2026/11/13/rollout-a.jsonl", line + "\n" + line + "\n");

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("codex", Context(home, clock), CancellationToken.None);

                Assert.Equal(110, TestData.Find(result.Items, "today").TotalTokens);
            }
        }

        [Test]
        public static void CodexCumulativeOnlyEvents_AreIgnored()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                temp.Write(".codex/sessions/2026/11/13/rollout-a.jsonl",
                    "{\"timestamp\":\"2026-11-13T15:00:00Z\",\"payload\":{\"type\":\"token_count\"," +
                    "\"info\":{\"total_token_usage\":{\"input_tokens\":100}}}}");

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("codex", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.NoData, result.Status);
            }
        }

        [Test]
        public static void Cache_ReusesUnchangedFilesAndInvalidatesOnChange()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var path = temp.Write(".claude/projects/p/session.jsonl",
                    ClaudeRecord(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)), "c1", 10, 1));

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var first = service.Fetch("claude", Context(home, clock), CancellationToken.None);
                Assert.Equal(16, TestData.Find(first.Items, "today").TotalTokens);
                Assert.Equal(1, service.CacheMisses);

                var second = service.Fetch("claude", Context(home, clock), CancellationToken.None);
                Assert.Equal(1, service.CacheHits);
                Assert.Equal(16, TestData.Find(second.Items, "today").TotalTokens);

                File.AppendAllText(path,
                    "\n" + ClaudeRecord(new DateTimeOffset(2026, 11, 13, 11, 0, 0, TimeSpan.FromHours(-5)), "c2", 10, 1));

                var third = service.Fetch("claude", Context(home, clock), CancellationToken.None);
                Assert.Equal(32, TestData.Find(third.Items, "today").TotalTokens);
            }
        }

        [Test]
        public static void Cache_PrunesDeletedFiles()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var path = temp.Write(".claude/projects/p/session.jsonl",
                    ClaudeRecord(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)), "c1", 10, 1));

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                service.Fetch("claude", Context(home, clock), CancellationToken.None);
                Assert.Equal(1, service.CacheEntries);

                File.Delete(path);
                var afterDelete = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.NoData, afterDelete.Status);
                Assert.Equal(0, service.CacheEntries);
            }
        }

        [Test]
        public static void MissingSourceDirectory_IsUnavailableNotZeroUsage()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var service = new ConsumptionService(clock, new FakeProcessRunner());

                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Unavailable, result.Status);
                Assert.Equal("source_missing", result.ErrorCode);
            }
        }

        [Test]
        public static void EmptySourceDirectory_IsNoDataNotFailure()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                Directory.CreateDirectory(Path.Combine(home, ".claude", "projects"));
                var clock = new FakeClock();
                var service = new ConsumptionService(clock, new FakeProcessRunner());

                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.NoData, result.Status);
                Assert.Null(result.ErrorCode);
            }
        }

        [Test]
        public static void OpenCode_MapsCliPeriodsAndSumsDisjointBuckets()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var runner = new FakeProcessRunner();
                runner.Handler = (executable, arguments, timeout, token) =>
                {
                    var result = new ProcessResult { Started = true, ExitCode = 0 };
                    if (arguments.IndexOf("--days 0", StringComparison.Ordinal) >= 0)
                    {
                        result.StandardOutput = TestData.OpenCodeStats(10, 2, 3, 4, 5, 0.25);
                    }
                    else if (arguments.IndexOf("--days 7", StringComparison.Ordinal) >= 0)
                    {
                        result.StandardOutput = TestData.OpenCodeStats(100, 20, 30, 40, 50, 2.5);
                    }
                    else
                    {
                        result.StandardOutput = TestData.OpenCodeStats(1000, 200, 300, 400, 500, 25);
                    }
                    return result;
                };

                var service = new ConsumptionService(clock, runner);
                var context = Context(home, clock);
                context.OpenCodeExecutable = "opencode.cmd";
                context.OpenCodeServiceRunning = true;

                var result = service.Fetch("opencode-go", context, CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Ok, result.Status);
                Assert.Equal(3, result.Items.Count);
                Assert.Equal(24, TestData.Find(result.Items, "today").TotalTokens);
                Assert.Equal(240, TestData.Find(result.Items, "7d").TotalTokens);
                Assert.Equal(2400, TestData.Find(result.Items, "30d").TotalTokens);
                Assert.Near(0.25, TestData.Find(result.Items, "today").CostUsd.Value, 0.0001);

                // 0, 7, 30 plus one "--days 2" call that closes yesterday exactly.
                Assert.Equal(4, runner.Calls.Count);
            }
        }

        [Test]
        public static void OpenCode_NotRunningIsUnavailableWithoutSpawningCli()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var runner = new FakeProcessRunner();
                var service = new ConsumptionService(clock, runner);
                var context = Context(home, clock);
                context.OpenCodeExecutable = "opencode.cmd";

                var result = service.Fetch("opencode-go", context, CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Unavailable, result.Status);
                Assert.Equal("service_not_running", result.ErrorCode);
                Assert.Equal(0, runner.Calls.Count);
            }
        }

        [Test]
        public static void OpenCode_PartialPeriodFailureKeepsAvailablePeriods()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var runner = new FakeProcessRunner();
                runner.Handler = (executable, arguments, timeout, token) =>
                {
                    if (arguments.IndexOf("--days 7", StringComparison.Ordinal) >= 0)
                    {
                        return new ProcessResult { Started = true, ExitCode = 2 };
                    }
                    return new ProcessResult
                    {
                        Started = true,
                        ExitCode = 0,
                        StandardOutput = TestData.OpenCodeStats(10, 1, 0, 0, 0, 0.1)
                    };
                };

                var service = new ConsumptionService(clock, runner);
                var context = Context(home, clock);
                context.OpenCodeExecutable = "opencode.cmd";
                context.OpenCodeServiceRunning = true;

                var result = service.Fetch("opencode-go", context, CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Ok, result.Status);
                Assert.Equal(2, result.Items.Count);
                Assert.True(result.Partial);
                Assert.Equal("stats_partial", result.ErrorCode);
            }
        }

        [Test]
        public static void OpenCode_AllPeriodsFailIsFailure()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var runner = new FakeProcessRunner
                {
                    Handler = (executable, arguments, timeout, token) =>
                        new ProcessResult { Started = true, ExitCode = 1 }
                };

                var service = new ConsumptionService(clock, runner);
                var context = Context(home, clock);
                context.OpenCodeExecutable = "opencode.cmd";
                context.OpenCodeServiceRunning = true;

                var result = service.Fetch("opencode-go", context, CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Failed, result.Status);
                Assert.Equal("stats_failed", result.ErrorCode);
            }
        }

        [Test]
        public static void LargeTokenValues_DoNotOverflowIntoNegativeTotals()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                temp.Write(".claude/projects/p/session.jsonl",
                    TestData.ClaudeLine(
                        new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                        "large", 2000000000000000000L, 2000000000000000000L, 1000, 1000));

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.True(TestData.Find(result.Items, "today").TotalTokens > 0);
            }
        }

        [Test]
        public static void DaylightSavingTransition_StillBucketsByLocalCalendarDate()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                var clock = new ZoneClock(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero), zone);

                var lines = new List<string>
                {
                    ClaudeRecord(new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.FromHours(-4)), "dst-1", 10, 0),
                    ClaudeRecord(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), "dst-2", 10, 0),
                    ClaudeRecord(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-5)), "dst-3", 10, 0)
                };
                var path = temp.Write(".claude/projects/p/session.jsonl", string.Join("\n", lines.ToArray()));
                File.SetLastWriteTimeUtc(path, clock.Utc.UtcDateTime);

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Ok, result.Status);
                Assert.Equal(45, TestData.Find(result.Items, "today").TotalTokens);
                Assert.Equal(45, TestData.Find(result.Items, "7d").TotalTokens);
            }
        }

        [Test]
        public static void ZeroUsageRecords_AreDistinctFromNoRecords()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                temp.Write(".claude/projects/p/session.jsonl",
                    TestData.ClaudeLine(
                        new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)),
                        "zero", 0, 0, 0, 0));

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(ConsumptionStatus.Ok, result.Status);
                Assert.Equal(3, result.Items.Count);
                Assert.Equal(0, TestData.Find(result.Items, "today").TotalTokens);
            }
        }

        [Test]
        public static void Cache_HandlesFileRotation()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var original = temp.Write(".claude/projects/p/session.jsonl",
                    ClaudeRecord(new DateTimeOffset(2026, 11, 13, 10, 0, 0, TimeSpan.FromHours(-5)), "rotate", 10, 1));

                var service = new ConsumptionService(clock, new FakeProcessRunner());
                service.Fetch("claude", Context(home, clock), CancellationToken.None);

                var rotated = temp.Combine(".claude", "projects", "p", "session.1.jsonl");
                File.Move(original, rotated);
                File.SetLastWriteTimeUtc(rotated, clock.Utc.UtcDateTime);

                var result = service.Fetch("claude", Context(home, clock), CancellationToken.None);

                Assert.Equal(16, TestData.Find(result.Items, "today").TotalTokens);
                Assert.Equal(1, service.CacheEntries);
            }
        }

        [Test]
        public static void OpenCode_CancellationPropagatesWithoutRetainingPartialPeriods()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var runner = new FakeProcessRunner
                {
                    Handler = (executable, arguments, timeout, token) =>
                        new ProcessResult { Started = true, Canceled = true, ErrorCode = "canceled" }
                };

                var service = new ConsumptionService(clock, runner);
                var context = Context(home, clock);
                context.OpenCodeExecutable = "opencode.cmd";
                context.OpenCodeServiceRunning = true;

                using (var cts = new CancellationTokenSource())
                {
                    Assert.Throws<OperationCanceledException>(
                        () => service.Fetch("opencode-go", context, cts.Token));
                }
            }
        }

        [Test]
        public static void AccessErrorDuringEnumeration_IsIsolated()
        {
            using (var temp = new TempDirectory())
            {
                var file = temp.Write("not-a-directory.txt", "x");
                bool hadError;
                var none = ConsumptionService.SafeEnumerate(file, "*.jsonl", out hadError);
                Assert.True(hadError);
                Assert.Equal(0, none.Count);

                var root = temp.MakeDirectory("projects");
                File.WriteAllText(Path.Combine(root, "session.jsonl"), "{}");
                var found = ConsumptionService.SafeEnumerate(root, "*.jsonl", out hadError);
                Assert.False(hadError);
                Assert.Equal(1, found.Count);
            }
        }

        [Test]
        public static void OpenCodeLocator_IgnoresRelativePathEntriesAndPrefersNpm()
        {
            var appData = Path.Combine("C:", "Users", "test", "AppData", "Roaming");
            var npmShim = Path.Combine(appData, "npm", "opencode.cmd");

            var npmResolved = OpenCodeLocator.ResolveFrom(
                appData,
                @"relative\bin;C:\Tools",
                candidate => string.Equals(candidate, npmShim, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(npmShim, npmResolved);

            var pathResolved = OpenCodeLocator.ResolveFrom(
                appData,
                @"relative\bin;C:\Tools;",
                candidate => string.Equals(candidate, Path.Combine("C:\\Tools", "opencode.exe"),
                    StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.Combine("C:\\Tools", "opencode.exe"), pathResolved);

            var none = OpenCodeLocator.ResolveFrom(appData, @"relative\bin", candidate => false);
            Assert.Null(none);
        }
    }
}
