using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace AiUsageWidget
{
    public sealed class HttpResult
    {
        public int Status;
        public string Body;
        public string RedirectLocation;
    }

    public interface IHttpTransport
    {
        HttpResult Get(string url, IDictionary<string, string> headers, int timeoutMs, CancellationToken token);
    }

    /// <summary>
    /// Minimal HTTP GET on HttpWebRequest. We deliberately avoid System.Net.Http
    /// (not present in the .NET Framework 4.0 reference assemblies) and keep
    /// connections short-lived so the widget holds no idle sockets.
    ///
    /// Security rules:
    ///  - redirects are followed manually and only within the same origin, so an
    ///    Authorization header can never be forwarded to another host;
    ///  - response bodies are size-limited;
    ///  - certificate revocation checking is left enabled;
    ///  - cancellation aborts the request instead of waiting out the timeout.
    /// </summary>
    public static class Http
    {
        public const int MaxResponseBytes = 2 * 1024 * 1024;
        private const int MaxRedirects = 2;

        static Http()
        {
            try
            {
                // TLS 1.2 + TLS 1.3 where the OS supports it.
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; }
                catch (Exception) { /* TLS 1.3 not available on this OS */ }
            }
            catch (Exception)
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            }

            ServicePointManager.DefaultConnectionLimit = 6;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.CheckCertificateRevocationList = true;
        }

        public static HttpResult Get(
            string url,
            IDictionary<string, string> headers,
            int timeoutMs,
            CancellationToken token)
        {
            if (string.IsNullOrEmpty(url)) throw new QuotaException("invalid_url", "Missing request URL.", 0);

            Uri current;
            if (!Uri.TryCreate(url, UriKind.Absolute, out current))
            {
                throw new QuotaException("invalid_url", "Invalid request URL.", 0);
            }

            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                token.ThrowIfCancellationRequested();
                var result = GetOnce(current, headers, timeoutMs, token);
                if (!IsRedirect(result.Status)) return result;

                var location = result.RedirectLocation;
                if (string.IsNullOrEmpty(location)) return result;

                Uri next;
                if (!Uri.TryCreate(current, location, out next))
                {
                    throw new QuotaException("redirect_invalid", "Received an invalid redirect.", 0);
                }

                if (!SameOrigin(current, next))
                {
                    throw new QuotaException(
                        "redirect_cross_origin",
                        "Refused to follow a redirect to a different origin.",
                        0);
                }

                current = next;
            }

            throw new QuotaException("redirect_limit", "Too many redirects.", 0);
        }

        private static bool IsRedirect(int status)
        {
            return status == 301 || status == 302 || status == 303 || status == 307 || status == 308;
        }

        private static bool SameOrigin(Uri a, Uri b)
        {
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
                   a.Port == b.Port;
        }

        private static HttpResult GetOnce(
            Uri uri,
            IDictionary<string, string> headers,
            int timeoutMs,
            CancellationToken token)
        {
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.KeepAlive = false;
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            // Local service traffic must never be routed through a system proxy.
            if (uri.IsLoopback) request.Proxy = null;

            if (headers != null)
            {
                foreach (var pair in headers)
                {
                    if (string.Equals(pair.Key, "Accept", StringComparison.OrdinalIgnoreCase))
                    {
                        request.Accept = pair.Value;
                    }
                    else if (string.Equals(pair.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                    {
                        request.UserAgent = pair.Value;
                    }
                    else
                    {
                        request.Headers[pair.Key] = pair.Value;
                    }
                }
            }

            using (token.Register(() => AbortQuietly(request)))
            {
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    {
                        return new HttpResult
                        {
                            Status = (int)response.StatusCode,
                            Body = ReadBody(stream),
                            RedirectLocation = FirstHeader(response, "Location")
                        };
                    }
                }
                catch (WebException ex)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);

                    var response = ex.Response as HttpWebResponse;
                    if (response == null)
                    {
                        if (ex.Status == WebExceptionStatus.Timeout || ex.Status == WebExceptionStatus.RequestCanceled)
                        {
                            throw new QuotaException("timeout", "The request timed out.", 0);
                        }
                        throw new QuotaException("network", "Network error.", 0);
                    }

                    using (response)
                    using (var stream = response.GetResponseStream())
                    {
                        var body = stream == null ? string.Empty : ReadBody(stream);
                        var redirect = FirstHeader(response, "Location");
                        return new HttpResult
                        {
                            Status = (int)response.StatusCode,
                            Body = body,
                            RedirectLocation = redirect
                        };
                    }
                }
                catch (IOException)
                {
                    if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                    throw new QuotaException("network", "Network error.", 0);
                }
            }
        }

        private static string FirstHeader(WebResponse response, string name)
        {
            try
            {
                var value = response.Headers[name];
                return value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void AbortQuietly(HttpWebRequest request)
        {
            try { request.Abort(); }
            catch (Exception) { }
        }

        private static string ReadBody(Stream stream)
        {
            if (stream == null) return string.Empty;

            using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                var total = 0;
                int read;
                while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += read;
                    if (total > MaxResponseBytes)
                    {
                        throw new QuotaException("response_too_large", "Response exceeded the size limit.", 0);
                    }
                    buffer.Write(chunk, 0, read);
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        /// <summary>
        /// Validates the OpenCode v2 service registration URL before Basic
        /// credentials are attached. Only plain loopback HTTP(S) origins without
        /// userinfo, query, or fragment are accepted.
        /// </summary>
        public static bool IsAllowedLocalServiceUrl(string url, out string reason)
        {
            reason = null;

            Uri uri;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                reason = "invalid_url";
                return false;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                reason = "unsupported_scheme";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                reason = "userinfo";
                return false;
            }

            if (!uri.IsLoopback)
            {
                reason = "not_loopback";
                return false;
            }

            if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                reason = "query_or_fragment";
                return false;
            }

            return true;
        }
    }

    public sealed class HttpTransport : IHttpTransport
    {
        public static readonly HttpTransport Default = new HttpTransport();

        public HttpResult Get(string url, IDictionary<string, string> headers, int timeoutMs, CancellationToken token)
        {
            return Http.Get(url, headers, timeoutMs, token);
        }
    }
}
