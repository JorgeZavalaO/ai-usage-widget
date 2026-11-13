using System;
using System.IO;
using System.Text;
using System.Threading;
using AiUsageWidget.Providers;

namespace AiUsageWidget.Tests
{
    public static class QuotaParsingTests
    {
        [Test]
        public static void ClaudeFixture_NormalizesLimitsArray()
        {
            var snapshot = QuotaParser.ParseClaude(TestPaths.FixtureText("claude-usage.json"), "Pro");

            Assert.Equal(ProviderStatus.Ok, snapshot.Status);
            Assert.Equal(2, snapshot.Windows.Count);
            Assert.Equal("session", snapshot.Windows[0].Id);
            Assert.Equal("5h", snapshot.Windows[0].Label);
            Assert.Near(6.0, snapshot.Windows[0].UsedPercent, 0.001);
            Assert.Equal("weekly_all", snapshot.Windows[1].Id);
            Assert.Near(13.0, snapshot.Windows[1].UsedPercent, 0.001);
            Assert.NotNull(snapshot.Windows[1].ResetsAt);
            Assert.Equal("Pro", snapshot.Plan);
        }

        [Test]
        public static void ClaudeLegacyFields_AreUsedWhenLimitsAreAbsent()
        {
            var body = "{\"five_hour\":{\"utilization\":7,\"resets_at\":\"2026-11-13T18:00:00Z\"}," +
                       "\"seven_day\":{\"utilization\":21,\"resets_at\":\"2026-11-18T18:00:00Z\"}," +
                       "\"seven_day_opus\":null,\"seven_day_sonnet\":null}";

            var snapshot = QuotaParser.ParseClaude(body, null);

            Assert.Equal(2, snapshot.Windows.Count);
            Assert.Equal("5h", snapshot.Windows[0].Label);
            Assert.Near(7, snapshot.Windows[0].UsedPercent, 0.001);
            Assert.Equal("Week", snapshot.Windows[1].Label);
        }

        [Test]
        public static void ClaudeUnusableLimits_FallBackToLegacyFields()
        {
            var body = "{\"limits\":[{\"kind\":\"session\"},{\"kind\":\"weekly_all\"}]," +
                       "\"five_hour\":{\"utilization\":9},\"seven_day\":{\"utilization\":30}}";

            var snapshot = QuotaParser.ParseClaude(body, null);

            Assert.Equal(ProviderStatus.Ok, snapshot.Status);
            Assert.Equal(2, snapshot.Windows.Count);
            Assert.Equal("5h", snapshot.Windows[0].Label);
        }

        [Test]
        public static void ClaudeEmptyLimitsAndNoLegacy_ProducesError()
        {
            var snapshot = QuotaParser.ParseClaude("{\"limits\":[{\"kind\":\"session\"}]}", null);

            Assert.Equal(ProviderStatus.Error, snapshot.Status);
            Assert.Equal("empty", snapshot.ErrorCode);
            Assert.Equal(0, snapshot.Windows.Count);
        }

        [Test]
        public static void ClaudeUnknownAndDuplicateKinds_AreHandled()
        {
            var body = "{\"limits\":[" +
                       "{\"kind\":\"session\",\"percent\":10}," +
                       "{\"kind\":\"session\",\"percent\":99}," +
                       "{\"kind\":\"mystery_window\",\"percent\":5}]}";

            var snapshot = QuotaParser.ParseClaude(body, null);

            Assert.Equal(2, snapshot.Windows.Count);
            Assert.Equal("session", snapshot.Windows[0].Id);
            Assert.Near(10, snapshot.Windows[0].UsedPercent, 0.001);
            Assert.Equal("Mystery window", snapshot.Windows[1].Label);
        }

        [Test]
        public static void ClaudePercent_IsClampedAndNonFiniteIsRejected()
        {
            var body = "{\"limits\":[" +
                       "{\"kind\":\"session\",\"percent\":-5}," +
                       "{\"kind\":\"weekly_all\",\"percent\":150}," +
                       "{\"kind\":\"weekly_opus\",\"percent\":\"NaN\"}]}";

            var snapshot = QuotaParser.ParseClaude(body, null);

            Assert.Equal(2, snapshot.Windows.Count);
            Assert.Near(0, snapshot.Windows[0].UsedPercent, 0.001);
            Assert.Near(100, snapshot.Windows[1].UsedPercent, 0.001);
        }

        [Test]
        public static void ClaudeMalformedJson_ThrowsParseError()
        {
            var error = Assert.Throws<QuotaException>(() => QuotaParser.ParseClaude("{oops", null));
            Assert.Equal("parse", error.Code);
        }

        [Test]
        public static void CodexFixture_NormalizesPrimaryAndSecondaryWindows()
        {
            var snapshot = QuotaParser.ParseCodex(TestPaths.FixtureText("codex-usage.json"));

            Assert.Equal(ProviderStatus.Ok, snapshot.Status);
            Assert.Equal("Plus", snapshot.Plan);
            Assert.Equal(2, snapshot.Windows.Count);
            Assert.Equal("primary", snapshot.Windows[0].Id);
            Assert.Equal("5h", snapshot.Windows[0].Label);
            Assert.Near(22, snapshot.Windows[0].UsedPercent, 0.001);
            Assert.Equal(300, snapshot.Windows[0].WindowMinutes);
            Assert.NotNull(snapshot.Windows[0].ResetsAt);
            Assert.Equal("secondary", snapshot.Windows[1].Id);
            Assert.Equal("Week", snapshot.Windows[1].Label);
        }

        [Test]
        public static void CodexPartialResponse_KeepsAvailableWindow()
        {
            var body = "{\"plan_type\":\"pro\",\"rate_limit\":{\"primary_window\":{\"used_percent\":12," +
                       "\"limit_window_seconds\":18000}}}";

            var snapshot = QuotaParser.ParseCodex(body);

            Assert.Equal(ProviderStatus.Ok, snapshot.Status);
            Assert.Equal(1, snapshot.Windows.Count);
            Assert.Equal("primary", snapshot.Windows[0].Id);
        }

        [Test]
        public static void CodexNoWindows_IsUnsupported()
        {
            var snapshot = QuotaParser.ParseCodex("{\"plan_type\":\"free\",\"rate_limit\":null}");
            Assert.Equal(ProviderStatus.Unsupported, snapshot.Status);
            Assert.Equal("no_rate_limit", snapshot.ErrorCode);
        }

        [Test]
        public static void CodexMalformedJson_ThrowsParseError()
        {
            var error = Assert.Throws<QuotaException>(() => QuotaParser.ParseCodex("[]"));
            Assert.Equal("parse", error.Code);
        }

        [Test]
        public static void OpenCodeFixture_NormalizesThreeWindows()
        {
            var snapshot = QuotaParser.ParseOpenCode(TestPaths.FixtureText("opencode-usage.json"));

            Assert.Equal(ProviderStatus.Ok, snapshot.Status);
            Assert.Equal(3, snapshot.Windows.Count);
            Assert.Equal("5h", snapshot.Windows[0].Label);
            Assert.Near(1, snapshot.Windows[0].UsedPercent, 0.001);
            Assert.Equal("Month", snapshot.Windows[2].Label);
            Assert.True(snapshot.Windows[2].IsCycle);
            Assert.NotNull(snapshot.Windows[2].ResetsAt);
        }

        [Test]
        public static void OpenCodeMissingUsage_ThrowsParseError()
        {
            var error = Assert.Throws<QuotaException>(() => QuotaParser.ParseOpenCode("{\"other\":1}"));
            Assert.Equal("parse", error.Code);
        }

        [Test]
        public static void OpenCodeInvalidPercent_PreservesOtherWindows()
        {
            var body = "{\"usage\":{\"rolling\":{\"percent\":\"NaN\"},\"weekly\":{\"percent\":3}," +
                       "\"monthly\":{\"percent\":\"Infinity\"}}}";

            var snapshot = QuotaParser.ParseOpenCode(body);

            Assert.Equal(ProviderStatus.Ok, snapshot.Status);
            Assert.Equal(1, snapshot.Windows.Count);
            Assert.Equal("weekly", snapshot.Windows[0].Id);
        }

        [Test]
        public static void OutOfRangeResetTimestamps_BecomeNullWithoutDroppingWindow()
        {
            var body = "{\"usage\":{\"rolling\":{\"percent\":4,\"resetsAt\":1e30}}}";
            var snapshot = QuotaParser.ParseOpenCode(body);
            Assert.Equal(1, snapshot.Windows.Count);
            Assert.Null(snapshot.Windows[0].ResetsAt);
        }

        [Test]
        public static void ClaudeProvider_ExpiredToken_ReturnsStaleAuthWithoutNetwork()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var clock = new FakeClock();
                var credentials = "{\"claudeAiOauth\":{\"accessToken\":\"fake-token\"," +
                                  "\"expiresAt\":" + (clock.UtcNow.ToUnixTimeMilliseconds() - 60000) +
                                  ",\"subscriptionType\":\"pro\"}}";
                temp.Write(".claude/.credentials.json", credentials);

                var transport = new FakeHttpTransport();
                var provider = new ClaudeProvider();

                var snapshot = provider.Fetch(
                    new FetchContext { Home = home, Transport = transport, Clock = clock },
                    CancellationToken.None);

                Assert.Equal(ProviderStatus.StaleAuth, snapshot.Status);
                Assert.Equal("token_expired", snapshot.ErrorCode);
                Assert.Equal(0, transport.Calls);
            }
        }

        [Test]
        public static void Providers_MissingCredentialFile_ReturnsNoCredentials()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var transport = new FakeHttpTransport();
                var context = new FetchContext { Home = home, Transport = transport, Clock = new FakeClock() };

                var claude = new ClaudeProvider().Fetch(context, CancellationToken.None);
                var codex = new CodexProvider().Fetch(context, CancellationToken.None);

                Assert.Equal(ProviderStatus.NoCredentials, claude.Status);
                Assert.Equal(ProviderStatus.NoCredentials, codex.Status);
                Assert.Equal(0, transport.Calls);
            }
        }

        [Test]
        public static void Providers_LockedCredentialFile_DoesNotThrowOrModify()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var path = temp.Write(".codex/auth.json",
                    "{\"tokens\":{\"access_token\":\"fake\",\"account_id\":\"acct\"}}");
                var before = File.ReadAllText(path);

                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var provider = new CodexProvider();
                    var snapshot = provider.Fetch(
                        new FetchContext { Home = home, Transport = new FakeHttpTransport(), Clock = new FakeClock() },
                        CancellationToken.None);

                    Assert.Equal(ProviderStatus.NoCredentials, snapshot.Status);
                }

                Assert.Equal(before, File.ReadAllText(path));
            }
        }

        [Test]
        public static void Providers_PartiallyReplacedCredentialFile_ReturnsNoCredentials()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                temp.Write(".claude/.credentials.json",
                    "{\"claudeAiOauth\":{\"accessToken\":\"fake\"");

                var snapshot = new ClaudeProvider().Fetch(
                    new FetchContext { Home = home, Transport = new FakeHttpTransport(), Clock = new FakeClock() },
                    CancellationToken.None);

                Assert.Equal(ProviderStatus.NoCredentials, snapshot.Status);
            }
        }

        [Test]
        public static void CodexProvider_403IsNotClassifiedAsExpiredAuth()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                temp.Write(".codex/auth.json",
                    "{\"tokens\":{\"access_token\":\"fake\",\"account_id\":\"acct\"}}");

                var transport = new FakeHttpTransport
                {
                    Handler = (url, headers) => new HttpResult { Status = 403, Body = "forbidden" }
                };

                var provider = new CodexProvider();
                var error = Assert.Throws<QuotaException>(() => provider.Fetch(
                    new FetchContext { Home = home, Transport = transport, Clock = new FakeClock() },
                    CancellationToken.None));

                Assert.Equal(403, error.HttpStatus);
            }
        }

        [Test]
        public static void OpenCodeProvider_Classifies401And403()
        {
            var transport = new FakeHttpTransport
            {
                Handler = (url, headers) => new HttpResult { Status = 401 }
            };
            var provider = new OpenCodeGoProvider();
            var context = new FetchContext
            {
                OpenCodeKey = "fake-key",
                Transport = transport,
                Clock = new FakeClock()
            };

            var unauthorized = provider.Fetch(context, CancellationToken.None);
            Assert.Equal(ProviderStatus.StaleAuth, unauthorized.Status);

            transport.Handler = (url, headers) => new HttpResult { Status = 403 };
            var forbidden = provider.Fetch(context, CancellationToken.None);
            Assert.Equal(ProviderStatus.Unsupported, forbidden.Status);
        }

        [Test]
        public static void OpenCodeProvider_NoKeyAnywhere_ReturnsNoCredentials()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                var previous = Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
                Environment.SetEnvironmentVariable("OPENCODE_API_KEY", null);
                try
                {
                    var snapshot = new OpenCodeGoProvider().Fetch(
                        new FetchContext
                        {
                            Home = home,
                            Transport = new FakeHttpTransport(),
                            Clock = new FakeClock()
                        },
                        CancellationToken.None);

                    Assert.Equal(ProviderStatus.NoCredentials, snapshot.Status);
                    Assert.Equal("no_key", snapshot.ErrorCode);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("OPENCODE_API_KEY", previous);
                }
            }
        }

        [Test]
        public static void OpenCodeProvider_DoesNotSendCredentialsToRemoteServiceUrl()
        {
            using (var temp = new TempDirectory())
            {
                var home = TestFile.NewHome(temp);
                TestFile.WriteServiceJson(home, "http://evil.example.com:1234", "secret");

                var transport = new FakeHttpTransport();
                var previous = Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
                Environment.SetEnvironmentVariable("OPENCODE_API_KEY", null);
                try
                {
                    var snapshot = new OpenCodeGoProvider().Fetch(
                        new FetchContext { Home = home, Transport = transport, Clock = new FakeClock() },
                        CancellationToken.None);

                    Assert.Equal(ProviderStatus.NoCredentials, snapshot.Status);
                    Assert.Equal(0, transport.Calls);
                }
                finally
                {
                    Environment.SetEnvironmentVariable("OPENCODE_API_KEY", previous);
                }
            }
        }
    }
}
