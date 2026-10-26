using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace AiUsageWidget
{
    public sealed class HttpResult
    {
        public int Status;
        public string Body;
    }

    /// <summary>
    /// Minimal HTTP GET on HttpWebRequest. We deliberately avoid System.Net.Http
    /// (not present in the .NET Framework 4.0 reference assemblies) and keep
    /// connections short-lived so the widget holds no idle sockets.
    /// </summary>
    public static class Http
    {
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
            ServicePointManager.CheckCertificateRevocationList = false;
        }

        public static HttpResult Get(string url, IDictionary<string, string> headers, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.KeepAlive = false;
            request.AllowAutoRedirect = true;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

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

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return new HttpResult
                    {
                        Status = (int)response.StatusCode,
                        Body = reader.ReadToEnd()
                    };
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null)
                {
                    throw new QuotaException("network", "Network error: " + ex.Message, 0);
                }

                using (response)
                using (var stream = response.GetResponseStream())
                using (var reader = stream == null ? null : new StreamReader(stream, Encoding.UTF8))
                {
                    return new HttpResult
                    {
                        Status = (int)response.StatusCode,
                        Body = reader == null ? string.Empty : reader.ReadToEnd()
                    };
                }
            }
        }
    }
}
