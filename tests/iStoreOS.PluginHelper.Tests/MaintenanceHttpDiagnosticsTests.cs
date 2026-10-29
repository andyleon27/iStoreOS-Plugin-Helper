using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class MaintenanceHttpDiagnosticsTests
    {
        private const string Commit = "0123456789abcdef0123456789abcdef01234567";
        private const string Secret = "Authorization: Bearer diagnostic-secret; Cookie=session-secret; proxy=https://user:password@proxy.invalid/?token=secret";

        public static void Run(Action<string, Action> test)
        {
            foreach (string endpoint in new[] { ThirdPartyScriptCatalog.RawUrl(Commit, "menu.sh"), ThirdPartyScriptCatalog.RawUrl(Commit, "LICENSE"),
                ThirdPartyScriptCatalog.MainMenuUrl, ThirdPartyScriptCatalog.MainMenuLicenseUrl })
            {
                string url = endpoint;
                test("Maintenance HTTP actual request retains valid User-Agent " + new Uri(url).AbsolutePath, () => {
                    var factory = typeof(WindowsThirdPartyHttpClient).GetMethod("CreateRequest", BindingFlags.NonPublic | BindingFlags.Static);
                    var request = (HttpWebRequest)factory.Invoke(null, new object[] { url, 65536 });
                    try {
                        Equal("iStoreOS-Plugin-Helper-Phase2A", request.UserAgent); Equal("GET", request.Method);
                        True(string.IsNullOrEmpty(request.Accept)); Equal(false, request.AllowAutoRedirect);
                        Equal(25000, request.Timeout); Equal(15000, request.ReadWriteTimeout);
                        Equal(RequestCacheLevel.NoCacheNoStore, request.CachePolicy.Level);
                        Equal<CookieContainer>(null, request.CookieContainer); Equal<ICredentials>(null, request.Credentials);
                        True(string.IsNullOrEmpty(request.Headers[HttpRequestHeader.Authorization]));
                    } finally { request.Abort(); }
                });
            }
            foreach (int step in new[] { 1, 2 })
            foreach (bool protocolException in new[] { false, true })
            {
                int at = step; bool useException = protocolException;
                test("Maintenance HTTP 403 identifies exact request " + at + (useException ? " WebException" : " returned status"), () => Async(async () => {
                    var http = new Http { FailAt = at, Error = useException ? Forbidden() : null };
                    var remote = new MaintenanceMenuSshTests.Remote();
                    var service = Service(http); var published = new ProgressSink();
                    var result = (await service.RunMenuAsync(remote, t => { throw new Exception("Must not open PTY."); }, published, CancellationToken.None)).Preparation;
                    string phase = at == 1 ? "Download/DownloadMenu" : "Download/DownloadLicense";
                    Failure(result, phase, http.Urls.Last(), "403"); Equal(at, http.Urls.Count);
                    Equal(0, remote.Creates); Equal(0, remote.Opens); Equal(0, remote.Uploads.Count); Equal(0, remote.PreviewCleanups); Equal(0, remote.FinishedCleanups);
                    Equal(false, result.ExecutionAttempted); Equal<string>(null, result.LocalDirectory);
                    True(result.Logs.Any(l => l.Message.Contains(result.Error)));
                    True(published.Entries.Any(l => l.Message.Contains(result.Error)));
                    True(service.LogBuffer.Snapshot().Any(l => l.Message.Contains(result.Error)));
                    Safe(result);
                }));
            }
            foreach (int status in new[] { 301, 404, 429, 500, 503 })
            {
                int code = status;
                test("Maintenance HTTP non-200 keeps status without retry or redirect " + code, () => Async(async () => {
                    var http = new Http { FailAt = 1, Status = code }; var remote = new MaintenanceMenuSshTests.Remote();
                    var result = await Service(http).PrepareMenuAsync(remote, null, CancellationToken.None);
                    Failure(result, "Download/DownloadMenu", http.Urls.Last(), code.ToString());
                    Equal(1, http.Urls.Count); Equal(ThirdPartyScriptCatalog.MainMenuUrl, http.Urls[0]); Equal(0, remote.Creates); Safe(result);
                }));
            }
            test("Maintenance HTTP no response distinguishes transport status from HTTP status", () => Async(async () => {
                var http = new Http { FailAt = 1, Error = new WebException(Secret, null, WebExceptionStatus.NameResolutionFailure, null) };
                var result = await Service(http).PrepareMenuAsync(new MaintenanceMenuSshTests.Remote(), null, CancellationToken.None);
                Failure(result, "Download/DownloadMenu", ThirdPartyScriptCatalog.MainMenuUrl, "unknown");
                True(result.Error.Contains("网络状态=NameResolutionFailure")); Safe(result);
            }));
            test("Maintenance HTTP timeout hides inner exceptions and never fabricates 403", () => Async(async () => {
                var http = new Http { FailAt = 2, Error = new TimeoutException(Secret, Forbidden()) };
                var result = await Service(http).PrepareMenuAsync(new MaintenanceMenuSshTests.Remote(), null, CancellationToken.None);
                Failure(result, "Download/DownloadLicense", http.Urls.Last(), "unknown");
                True(result.Error.Contains("错误类型=TimeoutException")); Equal(false, result.Error.Contains("HTTP=403")); Safe(result);
            }));
            test("Maintenance HTTP adapter IO errors do not expose arbitrary messages", () => Async(async () => {
                var http = new Http { FailAt = 1, Error = new IOException(Secret) };
                var result = await Service(http).PrepareMenuAsync(new MaintenanceMenuSshTests.Remote(), null, CancellationToken.None);
                Failure(result, "Download/DownloadMenu", http.Urls.Last(), "unknown"); Safe(result);
            }));
            test("Maintenance HTTP null response has explicit unknown status", () => Async(async () => {
                var http = new Http { FailAt = 1, NullResponse = true };
                var result = await Service(http).PrepareMenuAsync(new MaintenanceMenuSshTests.Remote(), null, CancellationToken.None);
                Failure(result, "Download/DownloadMenu", ThirdPartyScriptCatalog.MainMenuUrl, "unknown"); Safe(result);
            }));
            test("Maintenance HTTP cancellation remains cancellation without false HTTP failure", () => Async(async () => {
                var http = new Http { FailAt = 2, Error = new OperationCanceledException(Secret) };
                var result = await Service(http).PrepareMenuAsync(new MaintenanceMenuSshTests.Remote(), null, CancellationToken.None);
                Equal(MaintenancePreparationStatus.Cancelled, result.Status); Equal(2, http.Urls.Count);
                Equal(false, result.Error.Contains("HTTP 请求失败")); Safe(result);
            }));
            test("Maintenance HTTP explicit SHA skips API and diagnoses first raw request", () => Async(async () => {
                var http = new Http { FailAt = 1, Error = Forbidden() };
                var result = await Service(http).PrepareMenuAtCommitAsync(new MaintenanceMenuSshTests.Remote(), Commit, null, CancellationToken.None);
                Failure(result, "Download/DownloadMenu", ThirdPartyScriptCatalog.RawUrl(Commit, "menu.sh"), "403");
                Equal(1, http.Urls.Count); Equal(false, http.Urls.Contains(ComputerProxyPolicy.ApiUrl)); Safe(result);
            }));
            test("Maintenance HTTP successful preparation preserves all requests bytes and cleanup", () => Async(async () => {
                var http = new Http { FailAt = 0 }; var remote = new MaintenanceMenuSshTests.Remote();
                var result = await Service(http).PrepareMenuAsync(remote, null, CancellationToken.None);
                Equal(MaintenancePreparationStatus.PreparedExecutionDisabled, result.Status);
                Equal(ThirdPartyScriptCatalog.MainMenuUrl + "|" + ThirdPartyScriptCatalog.MainMenuLicenseUrl, string.Join("|", http.Urls));
                Equal("1048576|65536", string.Join("|", http.Limits)); Equal(false, http.Urls.Contains(ComputerProxyPolicy.ApiUrl));
                Equal(3, remote.Uploads.Count); Equal(1, remote.PreviewCleanups); True(result.IntegrityVerified);
                Equal<string>(null, result.Error); Equal(false, result.Logs.Any(l => l.Message.Contains("HTTP 请求失败")));
            }));
            test("Maintenance HTTP diagnostic redacts every nonallowlisted URL", () => {
                var method = typeof(ThirdPartyScriptSource).Assembly.GetType("iStoreOS.PluginHelper.Services.MaintenanceHttpDiagnostics")
                    .GetMethod("Failure", BindingFlags.NonPublic | BindingFlags.Static);
                foreach (string url in new[] { null, "https://user:password@api.github.com/private?token=secret#cookie", ComputerProxyPolicy.ApiUrl + "?token=secret",
                    ThirdPartyScriptCatalog.RawUrl(Commit, "menu.sh") + "#secret", ThirdPartyScriptCatalog.MainMenuUrl + "?token=secret",
                    ThirdPartyScriptCatalog.MainMenuLicenseUrl + "#secret", "https://evil.invalid/path-secret", "https://api.github.com/\r\nAuthorization: Bearer secret" }) {
                    string message = (string)method.Invoke(null, new object[] { MaintenancePreparationStage.Commit, url, Forbidden() });
                    True(message.Contains("域名=[redacted]")); True(message.Contains("URL=[redacted]"));
                    Equal(false, message.Contains("secret")); Equal(false, message.Contains("password")); Equal(false, message.Contains("\n"));
                }
            });
            test("Maintenance HTTP ordinary validation failure does not reuse stale request context", () => Async(async () => {
                var http = new Http { FailAt = 0, Menu = Encoding.UTF8.GetBytes("invalid shell") };
                var result = await Service(http).PrepareMenuAsync(new MaintenanceMenuSshTests.Remote(), null, CancellationToken.None);
                Equal(MaintenancePreparationStatus.Failed, result.Status); Equal(MaintenancePreparationStage.Validate, result.Stage);
                Equal(false, result.Error.Contains("HTTP 请求失败")); Equal(2, http.Urls.Count);
            }));
        }

        internal static WebException Forbidden(string remaining = null, string reset = null, int status = 403)
        {
            // Construct an inert .NET Framework response, never a socket/request.
            var response = (HttpWebResponse)FormatterServices.GetUninitializedObject(typeof(HttpWebResponse));
            var field = typeof(HttpWebResponse).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(HttpStatusCode));
            field.SetValue(response, (HttpStatusCode)status);
            var headers = new WebHeaderCollection();
            if (remaining != null) headers["x-ratelimit-remaining"] = remaining;
            if (reset != null) headers["x-ratelimit-reset"] = reset;
            headers["Set-Cookie"] = "session-secret"; headers["Authorization"] = "Bearer diagnostic-secret";
            typeof(HttpWebResponse).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Single(f => f.FieldType == typeof(WebHeaderCollection)).SetValue(response, headers);
            return new WebException(Secret, new IOException(Secret), WebExceptionStatus.ProtocolError, response);
        }
        private static void Failure(MaintenancePreparationResult result, string phase, string url, string status)
        {
            Equal(MaintenancePreparationStatus.Failed, result.Status);
            True(result.Error.Contains("阶段=" + phase)); True(result.Error.Contains("域名=" + new Uri(url).DnsSafeHost));
            True(result.Error.Contains("URL=" + url + " | HTTP=" + status + " |"));
        }
        private static void Safe(MaintenancePreparationResult result)
        {
            string text = result.Error + "\n" + string.Join("\n", result.Logs.Select(l => l.Message));
            foreach (string forbidden in new[] { "diagnostic-secret", "session-secret", "password", "proxy.invalid", "token=secret", "Bearer", "Cookie=" })
                Equal(false, text.Contains(forbidden));
        }
        private static PluginMaintenanceExecutionService Service(Http http) { return new PluginMaintenanceExecutionService(new ThirdPartyScriptSource(http)); }
        private sealed class Http : IThirdPartyHttpClient
        {
            public readonly List<string> Urls = new List<string>(); public readonly List<int> Limits = new List<int>();
            public int FailAt = 1, Status = 403; public bool NullResponse; public Exception Error;
            public byte[] Menu = Encoding.UTF8.GetBytes("#!/bin/sh\nprintf 'inert fixture only\\n'\n");
            public Task<ThirdPartyHttpContent> GetAsync(string url, int maxBytes, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Urls.Add(url); Limits.Add(maxBytes);
                if (Urls.Count == FailAt) {
                    if (Error != null) throw Error;
                    return Task.FromResult(NullResponse ? null : new ThirdPartyHttpContent { StatusCode = Status, Bytes = Encoding.UTF8.GetBytes(Secret) });
                }
                return Task.FromResult(new ThirdPartyHttpContent { StatusCode = 200, Bytes = url.EndsWith("/LICENSE", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(ThirdPartyScriptCatalog.BundledLicense()) : Menu });
            }
        }
        private sealed class ProgressSink : IProgress<MaintenanceOperationLog>
        { public readonly List<MaintenanceOperationLog> Entries = new List<MaintenanceOperationLog>(); public void Report(MaintenanceOperationLog entry) { Entries.Add(entry); } }
        private static void Async(Func<Task> action) { Task.Run(action).GetAwaiter().GetResult(); }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
    }
}
