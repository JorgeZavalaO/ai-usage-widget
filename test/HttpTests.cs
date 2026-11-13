using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AiUsageWidget.Providers;

namespace AiUsageWidget.Tests
{
    public static class HttpTests
    {
        [Test]
        public static void LocalServiceUrl_AllowsOnlyLoopbackWithoutCredentialsInUrl()
        {
            string reason;

            Assert.True(Http.IsAllowedLocalServiceUrl("http://127.0.0.1:28894", out reason), reason);
            Assert.True(Http.IsAllowedLocalServiceUrl("https://localhost:8443", out reason), reason);
            Assert.True(Http.IsAllowedLocalServiceUrl("http://[::1]:1234", out reason), reason);

            Assert.False(Http.IsAllowedLocalServiceUrl("http://127.0.0.1.evil.com:1234", out reason));
            Assert.False(Http.IsAllowedLocalServiceUrl("http://evil.example.com:1234", out reason));
            Assert.False(Http.IsAllowedLocalServiceUrl("ftp://127.0.0.1:1234", out reason));
            Assert.False(Http.IsAllowedLocalServiceUrl("http://user:pass@127.0.0.1:1234", out reason));
            Assert.False(Http.IsAllowedLocalServiceUrl("http://127.0.0.1:1234/?x=1", out reason));
            Assert.False(Http.IsAllowedLocalServiceUrl("http://127.0.0.1:1234/#frag", out reason));
            Assert.False(Http.IsAllowedLocalServiceUrl("not a url", out reason));
            Assert.False(Http.IsAllowedLocalServiceUrl(null, out reason));
        }

        [Test]
        public static void SameOriginRedirect_KeepsAuthorizationHeader()
        {
            using (var server = new FakeHttpServer())
            {
                server.Handler = request =>
                {
                    if (request.Path == "/start")
                    {
                        var redirect = new FakeHttpServer.Response { Status = 302 };
                        redirect.Headers.Add(new KeyValuePair<string, string>("Location", "/next"));
                        return redirect;
                    }

                    return new FakeHttpServer.Response
                    {
                        Status = 200,
                        Body = request.Headers.ContainsKey("Authorization")
                            ? request.Headers["Authorization"]
                            : "missing"
                    };
                };

                var headers = new Dictionary<string, string> { { "Authorization", "Bearer secret-value" } };
                var result = Http.Get(server.Url("/start"), headers, 3000, CancellationToken.None);

                Assert.Equal(200, result.Status);
                Assert.Contains("Bearer secret-value", result.Body);
                Assert.Equal(2, server.RequestCount);
            }
        }

        [Test]
        public static void CrossOriginRedirect_IsRejectedWithoutSendingCredentials()
        {
            using (var destination = new FakeHttpServer())
            using (var origin = new FakeHttpServer())
            {
                destination.Handler = request => new FakeHttpServer.Response { Status = 200, Body = "stolen" };
                origin.Handler = request =>
                {
                    var redirect = new FakeHttpServer.Response { Status = 302 };
                    redirect.Headers.Add(new KeyValuePair<string, string>(
                        "Location", "http://127.0.0.1:" + destination.Port + "/steal"));
                    return redirect;
                };

                var headers = new Dictionary<string, string> { { "Authorization", "Bearer secret-value" } };
                var error = Assert.Throws<QuotaException>(
                    () => Http.Get(origin.Url("/start"), headers, 3000, CancellationToken.None));

                Assert.Equal("redirect_cross_origin", error.Code);
                Assert.Equal(0, destination.RequestCount);
            }
        }

        [Test]
        public static void RedirectLoop_IsBounded()
        {
            using (var server = new FakeHttpServer())
            {
                server.Handler = request =>
                {
                    var redirect = new FakeHttpServer.Response { Status = 302 };
                    redirect.Headers.Add(new KeyValuePair<string, string>("Location", "/loop"));
                    return redirect;
                };

                var error = Assert.Throws<QuotaException>(
                    () => Http.Get(server.Url("/loop"), null, 3000, CancellationToken.None));

                Assert.Equal("redirect_limit", error.Code);
                Assert.True(server.RequestCount <= 4);
            }
        }

        [Test]
        public static void OversizedResponse_IsRejected()
        {
            using (var server = new FakeHttpServer())
            {
                var body = new string('x', Http.MaxResponseBytes + 1024);
                server.Handler = request => new FakeHttpServer.Response { Status = 200, Body = body };

                var error = Assert.Throws<QuotaException>(
                    () => Http.Get(server.Url("/large"), null, 5000, CancellationToken.None));

                Assert.Equal("response_too_large", error.Code);
            }
        }

        [Test]
        public static void Timeout_IsReportedWithinABound()
        {
            using (var server = new FakeHttpServer())
            {
                server.Handler = request => new FakeHttpServer.Response
                {
                    Status = 200,
                    Body = "{}",
                    DelayMs = 3000
                };

                var watch = Stopwatch.StartNew();
                var error = Assert.Throws<QuotaException>(
                    () => Http.Get(server.Url("/slow"), null, 500, CancellationToken.None));
                watch.Stop();

                Assert.Equal("timeout", error.Code);
                Assert.True(watch.ElapsedMilliseconds < 5000,
                    "Timeout took " + watch.ElapsedMilliseconds + " ms.");
            }
        }

        [Test]
        public static void Cancellation_AbortsTheRequest()
        {
            using (var server = new FakeHttpServer())
            {
                server.Handler = request => new FakeHttpServer.Response
                {
                    Status = 200,
                    Body = "{}",
                    DelayMs = 3000
                };

                using (var cts = new CancellationTokenSource())
                {
                    cts.CancelAfter(300);
                    var watch = Stopwatch.StartNew();
                    Assert.Throws<OperationCanceledException>(
                        () => Http.Get(server.Url("/slow"), null, 30000, cts.Token));
                    watch.Stop();

                    Assert.True(watch.ElapsedMilliseconds < 3000,
                        "Cancellation took " + watch.ElapsedMilliseconds + " ms.");
                }
            }
        }

        [Test]
        public static void AppStateLog_DoesNotContainProviderMessageSecrets()
        {
            using (var temp = new TempDirectory())
            {
                Log.Init(temp.Path);
                var store = new ConfigStore(temp.Combine("config"));
                store.Current.OpenCodeEnabled = false;
                store.Current.CodexEnabled = false;
                store.Current.ClaudeEnabled = true;
                store.Save();

                var provider = new FakeProvider("claude")
                {
                    Handler = (call, context, token) =>
                    {
                        throw new QuotaException("http_500", "SECRET_SENTINEL", 500);
                    }
                };

                var providers = new List<IQuotaProvider> { provider };
                var state = new AppState(
                    store,
                    new FakeClock(),
                    providers,
                    new FakeConsumption(),
                    new FakeAutostart());

                try
                {
                    state.RefreshNow();
                    Assert.True(state.WaitForIdle(TimeSpan.FromSeconds(10)));
                    var log = File.ReadAllText(Path.Combine(temp.Path, "widget.log"));
                    Assert.DoesNotContain("SECRET_SENTINEL", log);
                    Assert.Contains("quota/claude: http_500", log);
                }
                finally
                {
                    state.Dispose();
                }
            }
        }
    }
}
