using System;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class ThirdPartyHttpContent
    {
        public int StatusCode { get; set; }
        public byte[] Bytes { get; set; }
    }
    public interface IThirdPartyHttpClient
    { Task<ThirdPartyHttpContent> GetAsync(string url, int maxBytes, CancellationToken token); }
    public interface IThirdPartyScriptSource
    {
        Task<byte[]> DownloadAsync(string fixedUrl, int maxBytes, CancellationToken token);
    }
    public sealed class ThirdPartyScriptSource : IThirdPartyScriptSource
    {
        private readonly IThirdPartyHttpClient http;
        public ThirdPartyScriptSource(IThirdPartyHttpClient http) { if (http == null) throw new ArgumentNullException("http"); this.http = http; }
        internal IThirdPartyScriptSource WithLocalHttpProxy(int port)
        {
            ComputerProxyPolicy.ValidatePort(port);
            // Injected transports retain their own routing; only the built-in Windows
            // downloader receives a new, request-scoped loopback proxy instance.
            return http is WindowsThirdPartyHttpClient ? new ThirdPartyScriptSource(new WindowsThirdPartyHttpClient(port)) : this;
        }
        public async Task<byte[]> DownloadAsync(string fixedUrl, int maxBytes, CancellationToken token)
        {
            if (!ThirdPartyScriptCatalog.IsSourceUrl(fixedUrl) || maxBytes < 1 || maxBytes > 1048576) throw new ArgumentException("Unsupported third-party source URL or read size.");
            token.ThrowIfCancellationRequested();
            var response = await http.GetAsync(fixedUrl, maxBytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (response == null || response.StatusCode != 200)
            {
                var error = new IOException("Third-party download HTTP " + (response == null ? "unknown" : response.StatusCode.ToString()) + ".");
                // Diagnostic metadata only; preserve the exception type and rejection behavior.
                if (response != null) error.Data[MaintenanceHttpDiagnostics.StatusCodeKey] = response.StatusCode;
                throw error;
            }
            if (response.Bytes == null || response.Bytes.Length == 0 || response.Bytes.Length > maxBytes) throw new InvalidDataException("Third-party response is empty or exceeds size limit.");
            return response.Bytes;
        }
    }
    internal static class MaintenanceHttpDiagnostics
    {
        internal const string StatusCodeKey = "MaintenanceHttpStatusCode";
        internal static string Failure(MaintenancePreparationStage stage, string url, Exception error)
        {
            // Only our exact, public endpoint allowlist may enter a diagnostic URL.
            // Never log other headers, response bodies, ResponseUri, proxy settings,
            // credentials, exception messages or inner exceptions.
            bool allowed = ThirdPartyScriptCatalog.IsSourceUrl(url);
            string host = allowed ? new Uri(url).DnsSafeHost : "[redacted]";
            string request = !allowed ? "Unknown" : url.EndsWith("/LICENSE", StringComparison.Ordinal) ? "DownloadLicense" :
                url.EndsWith("/menu.sh", StringComparison.Ordinal) ? "DownloadMenu" : "DownloadScript";
            int? status = null;
            object recorded = error.Data[StatusCodeKey];
            if (recorded is int) status = (int)recorded;
            var web = error as WebException;
            var response = web == null ? null : web.Response as HttpWebResponse;
            if (response != null) status = (int)response.StatusCode;
            string code = status.HasValue && status >= 100 && status <= 599 ?
                status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
            return "HTTP 请求失败 | 阶段=" + stage + "/" + request + " | 域名=" + host +
                " | URL=" + (allowed ? url : "[redacted]") + " | HTTP=" + code +
                " | 错误类型=" + error.GetType().Name + (web == null ? "" : " | 网络状态=" + web.Status);
        }
    }
    // Windows download only: no shell, auth token, cookie jar, global TLS changes or redirects.
    public sealed class WindowsThirdPartyHttpClient : IThirdPartyHttpClient
    {
        readonly int? localProxyPort;
        public WindowsThirdPartyHttpClient() { }
        internal WindowsThirdPartyHttpClient(int port) { ComputerProxyPolicy.ValidatePort(port); localProxyPort = port; }
        internal HttpWebRequest CreateScopedRequest(string url, int maxBytes)
        {
            var request = CreateRequest(url, maxBytes);
            if (localProxyPort.HasValue) {
                request.Proxy = new WebProxy("http://127.0.0.1:" + localProxyPort.Value, false) { Credentials = null };
                request.UseDefaultCredentials = false; request.Credentials = null;
            }
            return request;
        }
        // Pure construction is independently inspectable without sending an HTTP request.
        internal static HttpWebRequest CreateRequest(string url, int maxBytes)
        {
            if (!ThirdPartyScriptCatalog.IsSourceUrl(url) || maxBytes < 1 || maxBytes > 1048576) throw new ArgumentException("Invalid third-party download.");
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET"; request.UserAgent = "iStoreOS-Plugin-Helper-Phase2A";
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            request.AllowAutoRedirect = false; request.Timeout = 25000; request.ReadWriteTimeout = 15000;
            // .NET Framework 4.8 uses its configured system TLS defaults. Never change
            // process-wide ServicePointManager settings or bypass certificate validation.
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return request;
        }
        public async Task<ThirdPartyHttpContent> GetAsync(string url, int maxBytes, CancellationToken token)
        {
            var request = CreateScopedRequest(url, maxBytes);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(25));
                using (timeout.Token.Register(request.Abort))
                {
                    try
                    {
                        using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                        {
                            if (response.StatusCode != HttpStatusCode.OK) return new ThirdPartyHttpContent { StatusCode = (int)response.StatusCode };
                            if (response.ContentLength > maxBytes) throw new InvalidDataException("Third-party response exceeds size limit.");
                            using (var input = response.GetResponseStream())
                            using (var output = new MemoryStream())
                            {
                                byte[] buffer = new byte[8192]; int count;
                                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                                { if (output.Length + count > maxBytes) throw new InvalidDataException("Third-party response exceeds size limit."); output.Write(buffer, 0, count); }
                                return new ThirdPartyHttpContent { StatusCode = 200, Bytes = output.ToArray() };
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        token.ThrowIfCancellationRequested();
                        if (timeout.IsCancellationRequested) throw new TimeoutException("Windows GitHub request timed out.", ex);
                        throw;
                    }
                }
            }
        }
    }
}
