using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class MaintenanceCleanupTests
    {
        private const string Commit = "0123456789abcdef0123456789abcdef01234567";
        internal static void Run(Action<string, Action> test)
        {
            test("Cleanup product assembly excludes old seven-plugin page and exclusive dependency tree", () => {
                var names = typeof(MainForm).Assembly.GetTypes().Select(t => t.Name).ToArray();
                foreach (string removed in new[] { "PluginMaintenanceForm", "MaintenanceActionConfirmationForm", "SoftwareSourceForm",
                    "MaintenancePluginKind", "MaintenanceReadStatus", "PluginPresence", "MaintenanceVersionRelation", "MaintenanceUpdateKind",
                    "PluginMaintenanceUpdate", "MaintenanceHttpResponse", "MaintenancePluginInfo", "PluginMaintenanceResult", "SoftwareMirrorInfo",
                    "MaintenanceScriptPurpose", "MaintenanceScriptRequest", "MaintenanceExecutionPlan", "MaintenanceScriptOutput",
                    "IMaintenanceExecutionSession", "MaintenanceScriptStreamRelay", "GitHubConnectivityChecker", "GitHubConnectivityResult",
                    "IMaintenanceReadOnlySession", "MaintenanceReadOnlyCommands", "SshMaintenanceReadOnlySession", "MaintenanceRelease",
                    "MaintenanceReleaseReader", "MaintenanceRequestPool", "PluginDetector", "MaintenancePluginDefinition", "PluginMaintenanceCatalog",
                    "PluginMaintenanceService", "SoftwareMirrorService" }) Equal(false, names.Contains(removed));
            });
            test("Cleanup old SSH action APIs and online-approval cache are absent", () => {
                foreach (string method in new[] { "ReadPluginMaintenanceAsync", "PrepareMaintenanceScriptAsync", "RunMaintenanceScriptAsync",
                    "ProcessMaintenanceScriptAsync", "RecordMaintenanceApproval", "ClearMaintenanceApproval", "ReadSoftwareMirrorsAsync",
                    "DetectSoftwareMirrorsAsync", "SwitchSoftwareMirrorAsync", "WithSoftwareMirrors" })
                    Equal<MethodInfo>(null, typeof(SshService).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
                Equal<FieldInfo>(null, typeof(SshService).GetField("maintenanceOnlineClient", BindingFlags.NonPublic | BindingFlags.Instance));
            });
            test("Cleanup shared downloader cannot resolve branch SHA or execute an action", () => {
                Equal("DownloadAsync", string.Join(",", typeof(IThirdPartyScriptSource).GetMethods().Select(m => m.Name)));
                foreach (string method in new[] { "PrepareScriptAsync", "RunScriptAsync", "ExecuteScriptAsync" })
                    Equal<MethodInfo>(null, typeof(PluginMaintenanceExecutionService).GetMethod(method));
                Equal<MethodInfo>(null, typeof(RemoteMaintenanceWorkspace).GetMethod("Plan"));
                Equal(0, typeof(RemoteMaintenanceWorkspace).GetConstructors().Length);
                Equal<PropertyInfo>(null, typeof(ThirdPartyScriptDescriptor).GetProperty("Arguments"));
                Equal<PropertyInfo>(null, typeof(MaintenancePreparationResult).GetProperty("Plan"));
            });
            test("Cleanup homepage exposes one terminal maintenance entry and no per-plugin action buttons", () => {
                using (var form = new MainForm()) {
                    var entries = form.Controls.Find("pluginMaintenanceLink", true); Equal(1, entries.Length); Equal(true, entries[0] is LinkLabel);
                    Equal(false, entries[0].Enabled);
                    Equal(0, form.Controls.Find("maintenanceRetry", true).Length);
                    Equal(0, form.Controls.Find("maintenanceSoftwareSources", true).Length);
                    foreach (string plugin in new[] { "OpenClash", "PassWall", "PassWall2", "Nikki", "SmartDNS", "MosDNS", "daed" })
                        foreach (string action in new[] { "安装", "更新", "卸载" }) Equal(0, form.Controls.Find("maintenanceAction" + plugin + action, true).Length);
                    Equal(true, typeof(MainForm).GetMethod("OpenMaintenanceTerminal", BindingFlags.NonPublic | BindingFlags.Instance) != null);
                }
            });
            foreach (string script in new[] { "install.sh", "passwall.sh", "passwall2.sh", "nikki.sh", "smartdns.sh", "mosdns.sh", "daed.sh", "check-updates.sh", "uninstall.sh" })
            {
                string file = script;
                test("Cleanup downloader rejects retired direct action endpoint " + file, () => {
                    foreach (string reference in new[] { "main", Commit })
                        Equal(false, ThirdPartyScriptCatalog.IsSourceUrl("https://raw.githubusercontent.com/" + ThirdPartyScriptCatalog.Project + "/" + reference + "/" + file));
                    Throws<ArgumentException>(() => ThirdPartyScriptCatalog.RawUrl(Commit, file));
                });
            }
            test("Cleanup commit API cannot enter Windows menu download adapter", () => {
                var http = new Http(); var source = new ThirdPartyScriptSource(http);
                Throws<ArgumentException>(() => source.DownloadAsync(ComputerProxyPolicy.ApiUrl, 65536, CancellationToken.None).GetAwaiter().GetResult());
                Throws<ArgumentException>(() => WindowsThirdPartyHttpClient.CreateRequest(ComputerProxyPolicy.ApiUrl, 65536));
                Equal(0, http.Requests);
            });
            test("Cleanup direct and fixed SHA menu downloads retain valid HTTP transport", () => {
                foreach (string url in new[] { ThirdPartyScriptCatalog.MainMenuUrl, ThirdPartyScriptCatalog.MainMenuLicenseUrl,
                    ThirdPartyScriptCatalog.RawUrl(Commit, "menu.sh"), ThirdPartyScriptCatalog.RawUrl(Commit, "LICENSE") }) {
                    Equal(true, ThirdPartyScriptCatalog.IsSourceUrl(url));
                    var request = WindowsThirdPartyHttpClient.CreateRequest(url, 65536);
                    try { Equal(false, request.AllowAutoRedirect); Equal(true, !string.IsNullOrEmpty(request.UserAgent)); }
                    finally { request.Abort(); }
                }
            });
            test("Cleanup API remains diagnostic and preserves safe quota evidence in network path", () => {
                var probe = ComputerProxyPolicy.ParseProbe(ComputerProxyCommand.DirectApi, new ComputerProxyCommandResult { ExitCode = 0,
                    Output = "HTTP/2 403\nx-ratelimit-remaining: 0\nx-ratelimit-reset: 1793181600\nSet-Cookie: secret\n\nIPH_PROXY_CURL|403|000|0\n" });
                Equal(403, probe.HttpStatus); Equal("0", probe.RateRemaining); Equal("1793181600", probe.RateReset);
                Equal(ComputerProxyPolicy.ApiUrl, probe.Url);
            });
            test("Cleanup shared local SHA and remote path binding keep their original boundaries", () => {
                string path = Path.GetTempFileName();
                const string hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
                try { File.WriteAllBytes(path, Encoding.ASCII.GetBytes("abc")); Equal(hash, MaintenanceScriptIntegrity.HashFile(path)); }
                finally { File.Delete(path); }
                const string remote = "/tmp/task/menu.sh";
                Equal(hash, MaintenanceScriptIntegrity.ParseRemoteHash(hash.ToUpperInvariant() + "  " + remote + "\n", remote));
                foreach (string invalid in new[] { hash + "  /tmp/other\n", hash + "  " + remote + "\nextra\n", "not-a-hash " + remote })
                    Throws<InvalidDataException>(() => MaintenanceScriptIntegrity.ParseRemoteHash(invalid, remote));
            });
            test("Cleanup shared menu logs reject stale operations and remain bounded", () => {
                var buffer = new MaintenanceOperationLogBuffer(); Guid first = Guid.NewGuid(), next = Guid.NewGuid(); buffer.Begin(first);
                for (int i = 0; i < 1100; i++) Equal(true, buffer.TryAppend(new MaintenanceOperationLog(first, MaintenanceLogOrigin.PluginHelper, i.ToString())));
                Equal(1000, buffer.Snapshot().Count); Equal("100", buffer.Snapshot()[0].Message);
                var saved = buffer.Snapshot(); buffer.Begin(next); Equal(0, buffer.Snapshot().Count); Equal(1000, saved.Count);
                Equal(false, buffer.TryAppend(new MaintenanceOperationLog(first, MaintenanceLogOrigin.PluginHelper, "stale")));
                Equal(true, buffer.TryAppend(new MaintenanceOperationLog(next, MaintenanceLogOrigin.PluginHelper, "current\nline")));
                Equal("current line", buffer.Snapshot()[0].Message);
            });
        }
        private sealed class Http : IThirdPartyHttpClient
        {
            public int Requests;
            public Task<ThirdPartyHttpContent> GetAsync(string url, int maxBytes, CancellationToken token)
            { Requests++; throw new Exception("No HTTP request permitted."); }
        }
        private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
        private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    }
}
