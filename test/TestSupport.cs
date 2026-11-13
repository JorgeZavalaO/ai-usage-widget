using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AiUsageWidget.Providers;

namespace AiUsageWidget.Tests
{
    public sealed class FakeClock : IClock
    {
        public FakeClock()
        {
            Utc = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
            Offset = TimeSpan.FromHours(-5);
        }

        public DateTimeOffset Utc { get; set; }
        public TimeSpan Offset { get; set; }

        public DateTimeOffset UtcNow
        {
            get { return Utc; }
        }

        public DateTime LocalNow
        {
            get { return Utc.ToOffset(Offset).DateTime; }
        }

        public DateTimeOffset ToLocal(DateTimeOffset value)
        {
            return value.ToOffset(Offset);
        }

        public void Advance(TimeSpan span)
        {
            Utc = Utc.Add(span);
        }

        public void AdvanceMinutes(double minutes)
        {
            Advance(TimeSpan.FromMinutes(minutes));
        }

        public void AdvanceSeconds(double seconds)
        {
            Advance(TimeSpan.FromSeconds(seconds));
        }
    }

    public sealed class ZoneClock : IClock
    {
        public ZoneClock(DateTimeOffset utc, TimeZoneInfo zone)
        {
            Utc = utc;
            Zone = zone;
        }

        public DateTimeOffset Utc { get; set; }
        public TimeZoneInfo Zone { get; set; }

        public DateTimeOffset UtcNow
        {
            get { return Utc; }
        }

        public DateTime LocalNow
        {
            get { return TimeZoneInfo.ConvertTime(Utc, Zone).DateTime; }
        }

        public DateTimeOffset ToLocal(DateTimeOffset value)
        {
            return TimeZoneInfo.ConvertTime(value, Zone);
        }

        public void Advance(TimeSpan span)
        {
            Utc = Utc.Add(span);
        }
    }

    public sealed class FakeHttpTransport : IHttpTransport
    {
        private readonly object _gate = new object();
        private readonly List<string> _urls = new List<string>();
        private readonly List<IDictionary<string, string>> _headers = new List<IDictionary<string, string>>();

        public Func<string, IDictionary<string, string>, HttpResult> Handler;

        public int Calls
        {
            get { lock (_gate) { return _urls.Count; } }
        }

        public List<string> Urls
        {
            get { lock (_gate) { return new List<string>(_urls); } }
        }

        public List<IDictionary<string, string>> Headers
        {
            get { lock (_gate) { return new List<IDictionary<string, string>>(_headers); } }
        }

        public HttpResult Get(string url, IDictionary<string, string> headers, int timeoutMs, CancellationToken token)
        {
            lock (_gate)
            {
                _urls.Add(url);
                _headers.Add(headers == null ? null : new Dictionary<string, string>(headers));
            }

            token.ThrowIfCancellationRequested();

            if (Handler == null) return new HttpResult { Status = 500 };
            return Handler(url, headers);
        }
    }

    public sealed class FakeProvider : IQuotaProvider
    {
        private int _calls;

        public FakeProvider(string id)
        {
            Id = id;
            Name = id;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }

        public Func<int, FetchContext, CancellationToken, ProviderSnapshot> Handler;

        public int Calls
        {
            get { return Volatile.Read(ref _calls); }
        }

        public ProviderSnapshot Fetch(FetchContext context, CancellationToken token)
        {
            var call = Interlocked.Increment(ref _calls);
            if (Handler == null)
            {
                return TestData.OkSnapshot(Id, 10);
            }

            token.ThrowIfCancellationRequested();
            return Handler(call, context, token);
        }
    }

    public sealed class FakeConsumption : IConsumptionService
    {
        private int _calls;

        public Func<string, FetchContext, ConsumptionResult> Handler;

        public int Calls
        {
            get { return Volatile.Read(ref _calls); }
        }

        public ConsumptionResult Fetch(string providerId, FetchContext context, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            token.ThrowIfCancellationRequested();
            if (Handler == null) return ConsumptionResult.NoData(null);
            return Handler(providerId, context);
        }
    }

    public sealed class FakeAutostart : IAutostart
    {
        public bool Enabled;
        public bool FailSet;
        public bool FailGet;
        public string LastPath;

        public bool IsEnabled()
        {
            if (FailGet) return false;
            return Enabled;
        }

        public bool IsEnabledFor(string executablePath)
        {
            if (FailGet) return false;
            if (!Enabled) return false;
            return LastPath == null || string.Equals(LastPath, executablePath, StringComparison.OrdinalIgnoreCase);
        }

        public bool Set(bool enabled, string executablePath)
        {
            if (FailSet) return false;
            Enabled = enabled;
            LastPath = executablePath;
            return true;
        }
    }

    public sealed class FakeProcessRunner : IProcessRunner
    {
        public Func<string, string, int, CancellationToken, ProcessResult> Handler;
        public readonly List<string> Calls = new List<string>();

        public ProcessResult Run(string executable, string arguments, int timeoutMs, CancellationToken token)
        {
            lock (Calls) Calls.Add(executable + "|" + arguments);
            if (Handler == null) return new ProcessResult { Started = true, ExitCode = 1 };
            return Handler(executable, arguments, timeoutMs, token);
        }
    }

    public static class TestData
    {
        public static ProviderSnapshot OkSnapshot(string id, double percent)
        {
            var snapshot = new ProviderSnapshot
            {
                Id = id,
                Name = id,
                Status = ProviderStatus.Ok,
                FetchedAt = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)
            };
            snapshot.Windows.Add(new UsageWindow
            {
                Id = "window",
                Label = "5h",
                UsedPercent = percent,
                WindowMinutes = 300
            });
            return snapshot;
        }

        public static string ClaudeLine(
            DateTimeOffset timestamp,
            string messageId,
            long input,
            long output,
            long cacheRead = 0,
            long cacheWrite = 0)
        {
            return "{\"type\":\"assistant\",\"timestamp\":\"" +
                   timestamp.ToString("o", CultureInfo.InvariantCulture) +
                   "\",\"message\":{\"id\":\"" + messageId +
                   "\",\"usage\":{\"input_tokens\":" + input +
                   ",\"output_tokens\":" + output +
                   ",\"cache_read_input_tokens\":" + cacheRead +
                   ",\"cache_creation_input_tokens\":" + cacheWrite + "}}}";
        }

        public static string CodexLine(
            DateTimeOffset timestamp,
            long input,
            long output,
            long reasoning = 0,
            long cacheRead = 0)
        {
            return "{\"timestamp\":\"" + timestamp.ToString("o", CultureInfo.InvariantCulture) +
                   "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{" +
                   "\"input_tokens\":" + input +
                   ",\"output_tokens\":" + output +
                   ",\"reasoning_output_tokens\":" + reasoning +
                   ",\"cached_input_tokens\":" + cacheRead + "}}}}";
        }

        public static string OpenCodeStats(long input, long output, long reasoning, long cacheRead, long cacheWrite, double cost)
        {
            return "{\"cost\":" + cost.ToString("0.####", CultureInfo.InvariantCulture) +
                   ",\"tokens\":{\"input\":" + input +
                   ",\"output\":" + output +
                   ",\"reasoning\":" + reasoning +
                   ",\"cache\":{\"read\":" + cacheRead + ",\"write\":" + cacheWrite + "}}}";
        }

        public static Consumption Find(List<Consumption> items, string period)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Period == period) return items[i];
            }
            throw new TestFailureException("Missing consumption period: " + period);
        }
    }

    public sealed class FakeHttpServer : IDisposable
    {
        public sealed class Request
        {
            public string Method;
            public string Path;
            public readonly Dictionary<string, string> Headers =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public sealed class Response
        {
            public int Status = 200;
            public string Body = string.Empty;
            public int DelayMs;
            public readonly List<KeyValuePair<string, string>> Headers =
                new List<KeyValuePair<string, string>>();
        }

        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly object _gate = new object();
        private readonly List<Request> _requests = new List<Request>();
        private volatile bool _disposed;

        public FakeHttpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "fake-http-server" };
            _thread.Start();
        }

        public int Port { get; private set; }

        public Func<Request, Response> Handler { get; set; }

        public int RequestCount
        {
            get { lock (_gate) { return _requests.Count; } }
        }

        public List<Request> Requests
        {
            get { lock (_gate) { return new List<Request>(_requests); } }
        }

        public string Url(string path)
        {
            return "http://127.0.0.1:" + Port + path;
        }

        private void AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var request = ReadRequest(stream);
                        if (request == null) continue;

                        lock (_gate) _requests.Add(request);

                        var response = Handler == null
                            ? new Response { Status = 200, Body = "{}" }
                            : Handler(request);

                        if (response.DelayMs > 0) Thread.Sleep(response.DelayMs);

                        WriteResponse(stream, response);
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private static Request ReadRequest(NetworkStream stream)
        {
            var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            var requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine)) return null;

            var parts = requestLine.Split(' ');
            var request = new Request { Method = parts[0], Path = parts.Length > 1 ? parts[1] : "/" };

            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                request.Headers[name] = value;
            }

            return request;
        }

        private static void WriteResponse(NetworkStream stream, Response response)
        {
            var body = Encoding.UTF8.GetBytes(response.Body ?? string.Empty);
            var builder = new StringBuilder();
            builder.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n");
            builder.Append("Connection: close\r\n");
            builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            for (int i = 0; i < response.Headers.Count; i++)
            {
                builder.Append(response.Headers[i].Key).Append(": ").Append(response.Headers[i].Value).Append("\r\n");
            }
            builder.Append("\r\n");

            var head = Encoding.ASCII.GetBytes(builder.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 301: return "Moved Permanently";
                case 302: return "Found";
                case 403: return "Forbidden";
                case 500: return "Internal Server Error";
                default: return "Status";
            }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _listener.Stop(); }
            catch (Exception) { }
            try { _thread.Join(3000); }
            catch (Exception) { }
        }
    }

    public static class TestFile
    {
        public static string NewHome(TempDirectory temp)
        {
            // Use the temporary root itself so tests can write both dot-directories
            // and service registration files without a second path prefix.
            return temp.Path;
        }

        public static void SetAllModified(string root, DateTimeOffset value)
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(file, value.UtcDateTime);
            }
        }

        public static string WriteServiceJson(string home, string url, string password)
        {
            var path = Path.Combine(home, ".local", "state", "opencode", "service.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,
                "{\"url\":\"" + url + "\",\"password\":\"" + password + "\",\"pid\":" +
                System.Diagnostics.Process.GetCurrentProcess().Id + "}",
                new UTF8Encoding(false));
            return path;
        }
    }
}
