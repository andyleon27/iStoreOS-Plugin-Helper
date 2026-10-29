using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Renci.SshNet;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class MaintenanceLaunchTests
    {
        internal static void SeedDirect(SshService service, SshClient client)
        {
            Set(service, "maintenanceNetworkClient", client); Set(service, "maintenanceNetwork", new MaintenanceNetworkSnapshot {
                State = MaintenanceNetworkState.Available, Path = MaintenanceNetworkPath.Direct, CheckedAt = DateTimeOffset.UtcNow, Code = "FixtureDirect" });
            Set(service, "maintenanceNetworkFactory", new Func<SshClient, Func<bool>, IMaintenanceNetworkSession>((primary, current) => new Network { Online = () => current() && primary.IsConnected }));
        }
        internal static void Run(Action<string, Action> test)
        {
            foreach (int api in new[] { 200, 403, 0 }) { int code = api; test("Maintenance Raw direct gate ignores API " + code, () => Async(async () => {
                var n = new Network { Api = code }; var r = await new MaintenanceNetworkService().CheckAsync(n, CancellationToken.None);
                True(r.Available); Equal(MaintenanceNetworkPath.Direct, r.Path); Equal(0, n.CandidateCalls); Equal(0, n.WindowsReads); Equal("DirectRaw|DirectApi", string.Join("|", n.Trace));
            })); }
            test("Maintenance API success cannot enable missing Raw paths", () => Async(async () => { var n = new Network { Raw = 0, Windows = false }; var r = await new MaintenanceNetworkService().CheckAsync(n, CancellationToken.None); Equal(false, r.Available); Equal(MaintenanceNetworkPath.None, r.Path); }));
            test("Maintenance computer proxy fallback checks localhost Raw only after router Raw fails", () => Async(async () => {
                var n = new Network { Raw = 0, Api = 403 }; var r = await new MaintenanceNetworkService().CheckAsync(n, CancellationToken.None);
                True(r.Available); Equal(MaintenanceNetworkPath.ComputerProxy, r.Path); Equal(7897, r.LocalProxyPort); Equal("DirectRaw|DirectApi|Candidates|Windows:7897", string.Join("|", n.Trace));
            }));
            test("Maintenance failed proxy candidates leave gate unavailable", () => Async(async () => { var n = new Network { Raw = 503, Windows = false, Ports = new[] { 7897, 8080 } }; var r = await new MaintenanceNetworkService().CheckAsync(n, CancellationToken.None); Equal(false, r.Available); Equal(2, n.WindowsReads); }));
            test("Maintenance API exception remains diagnostic with Raw success", () => Async(async () => { var n = new Network { FailApi = true }; var r = await new MaintenanceNetworkService().CheckAsync(n, CancellationToken.None); True(r.Available); Equal(0, r.RouterApi.HttpStatus); }));
            test("Maintenance Raw exception can use Windows fallback", () => Async(async () => { var n = new Network { FailRaw = true }; Equal(MaintenanceNetworkPath.ComputerProxy, (await new MaintenanceNetworkService().CheckAsync(n, CancellationToken.None)).Path); }));
            test("Maintenance direct probes clear all explicit proxy state and preserve TLS", () => {
                foreach (var c in new[] { ComputerProxyCommand.DirectRaw, ComputerProxyCommand.DirectApi }) { string command = ComputerProxyPolicy.GetCommand(c); True(command.StartsWith("http_proxy='' https_proxy='' ")); True(command.Contains("--proxy '' --noproxy '*'")); Equal(false, command.Contains("--insecure")); Equal(false, command.Contains("export ")); }
            });
            test("Maintenance Windows probe uses explicit credential-free loopback HTTP proxy", () => {
                var r = SshMaintenanceNetworkSession.WindowsRequest(7897); Equal("http://127.0.0.1:7897/", r.Proxy.GetProxy(new Uri(ComputerProxyPolicy.RawUrl)).AbsoluteUri);
                Equal(null, r.Proxy.Credentials); Equal(null, r.Credentials); Equal(false, r.UseDefaultCredentials); Equal(false, r.AllowAutoRedirect); Equal(ComputerProxyPolicy.RawUrl, r.RequestUri.AbsoluteUri); r.Abort();
            });
            test("Maintenance menu and LICENSE downloads scope the chosen proxy without changing global settings", () => {
                var original = System.Net.WebRequest.DefaultWebProxy; var client = new WindowsThirdPartyHttpClient(7897);
                foreach (string url in new[] { ComputerProxyPolicy.RawUrl, ThirdPartyScriptCatalog.DescribeMenuMain().DownloadUrl }) {
                    var request = client.CreateScopedRequest(url, 65536); Equal("http://127.0.0.1:7897/", request.Proxy.GetProxy(new Uri(url)).AbsoluteUri);
                    Equal(null, request.Proxy.Credentials); Equal(false, request.AllowAutoRedirect); Equal(false, request.UseDefaultCredentials); request.Abort();
                }
                True(ReferenceEquals(original, System.Net.WebRequest.DefaultWebProxy));
            });
            test("Maintenance stale snapshots refresh and checking snapshots never trigger duplicates", () => {
                True(new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.NotChecked }.NeedsRefresh);
                Equal(false, new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.Checking }.NeedsRefresh);
                True(new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.Available, Path = MaintenanceNetworkPath.Direct, CheckedAt = DateTimeOffset.UtcNow.AddSeconds(-31) }.NeedsRefresh);
                True(new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.Unavailable, CheckedAt = DateTimeOffset.UtcNow.AddSeconds(-11) }.NeedsRefresh);
            });
            test("Maintenance background detection holds mutual exclusion and never opens SSH two", () => Async(async () => {
                using (var f = new Fixture()) { f.Network.BlockRaw = true; Equal(MaintenanceMenuAvailabilityStatus.CheckingNetwork, f.Service.GetMaintenanceLaunchAvailability(f.Device).Status);
                    var checking = f.Service.CheckMaintenanceNetworkAsync(f.Device, CancellationToken.None); await f.Network.Entered.Task;
                    Equal(MaintenanceMenuAvailabilityStatus.CheckingNetwork, f.Service.GetMaintenanceLaunchAvailability(f.Device).Status); Equal(false, f.Service.CanChangeConnection); Equal(false, await f.Service.DisconnectAsync());
                    Throws(() => f.Service.RunMaintenanceMenuWithNetworkAsync(f.Device, t => { }, null, CancellationToken.None)); f.Network.Release.TrySetResult(0); await checking;
                    True(f.Service.GetMaintenanceLaunchAvailability(f.Device).CanOpen); Equal(0, f.ProxyFactories); Equal(0, f.Http.Urls.Count); True(f.Service.CanChangeConnection);
                }
            }));
            foreach (string flag in new[] { "connecting", "softwareBusy", "pending" }) { string value = flag; test("Maintenance detection defers while busy " + value, () => {
                using (var f = new Fixture()) { if (value == "pending") { var workflow = Field(f.Service, "installWorkflow"); var context = workflow.GetType().GetNestedType("Context", BindingFlags.NonPublic); Set(workflow, "unfinished", Activator.CreateInstance(context, true)); } else Set(f.Service, value, true);
                    Throws(() => f.Service.CheckMaintenanceNetworkAsync(f.Device, CancellationToken.None)); Equal(0, f.Network.RawReads); Equal(false, f.Service.GetMaintenanceLaunchAvailability(f.Device).CanOpen);
                }
            }); }
            test("Maintenance cancelled detection restores gate without stale available state", () => Async(async () => {
                using (var f = new Fixture()) using (var cancel = new CancellationTokenSource()) { f.Network.BlockRaw = true; var checking = f.Service.CheckMaintenanceNetworkAsync(f.Device, cancel.Token); await f.Network.Entered.Task; cancel.Cancel(); await Catch(checking); True(f.Service.CanChangeConnection); Equal(false, f.Service.GetMaintenanceLaunchAvailability(f.Device).CanOpen); }
            }));
            test("Maintenance disconnected client cannot retain old ready gate", () => Async(async () => {
                using (var f = new Fixture()) { await f.Service.CheckMaintenanceNetworkAsync(f.Device, CancellationToken.None); True(f.Service.GetMaintenanceLaunchAvailability(f.Device).CanOpen); f.Home.Live = false;
                    Equal(MaintenanceMenuAvailabilityStatus.NotConnected, f.Service.GetMaintenanceLaunchAvailability(f.Device).Status); await f.Service.DisconnectAsync(); Equal(MaintenanceNetworkState.NotChecked, f.Service.GetMaintenanceNetworkSnapshot().State);
                }
            }));
            test("Maintenance stale completion cannot update a replacement SSH client", () => Async(async () => {
                using (var f = new Fixture()) { f.Network.BlockRaw = true; var checking = f.Service.CheckMaintenanceNetworkAsync(f.Device, CancellationToken.None); await f.Network.Entered.Task;
                    using (var replacement = new Home()) { Set(f.Service, "client", replacement); f.Network.Release.TrySetResult(0); await checking; Equal(MaintenanceNetworkState.NotChecked, f.Service.GetMaintenanceNetworkSnapshot().State); Set(f.Service, "client", f.Home); }
                }
            }));
            test("Maintenance click rechecks previously direct route before any menu download", () => Async(async () => {
                using (var f = new Fixture()) { await f.Service.CheckMaintenanceNetworkAsync(f.Device, CancellationToken.None); f.Network.Raw = 0; f.Network.Windows = false;
                    var r = await f.Run(); Equal(MaintenancePreparationStatus.Rejected, r.Preparation.Status); Equal(2, f.Network.RawReads); Equal(0, f.Http.Urls.Count); Equal(0, f.ProxyFactories); Equal(false, f.Service.GetMaintenanceLaunchAvailability(f.Device).CanOpen);
                }
            }));
            test("Maintenance click switches cached proxy to now-working direct without SSH two", () => Async(async () => {
                using (var f = new Fixture()) { f.Network.Raw = 0; await f.Service.CheckMaintenanceNetworkAsync(f.Device, CancellationToken.None); Equal(MaintenanceMenuAvailabilityStatus.ReadyViaComputerProxy, f.Service.GetMaintenanceLaunchAvailability(f.Device).Status); f.Network.Raw = 200;
                    var r = await f.Run(); Equal(MaintenanceNetworkPath.Direct, r.NetworkPath); Equal(MaintenancePreparationStatus.Completed, r.Preparation.Status); Equal(0, f.ProxyFactories); True(f.Home.Live);
                }
            }));
            test("Maintenance click switches cached direct to computer proxy and validates Raw with API 403", () => Async(async () => {
                using (var f = new Fixture()) { await f.Service.CheckMaintenanceNetworkAsync(f.Device, CancellationToken.None); f.Network.Raw = 0; f.Proxy.Api = 403;
                    var r = await f.Run(); Equal(MaintenanceNetworkPath.ComputerProxy, r.NetworkPath); Equal(MaintenancePreparationStatus.Completed, r.Preparation.Status); True(r.ProxyCleanup.Succeeded); Equal(1, f.ProxyFactories); Equal(1, f.Proxy.Connects); Equal(1, f.Proxy.Closes); Equal(0, f.Home.Disposals); True(f.Home.Live);
                    string launcher = Encoding.UTF8.GetString(f.Menu.Uploads["helper-launcher.sh"]); True(launcher.Contains("http_proxy='http://127.0.0.1:17890' https_proxy='http://127.0.0.1:17890'")); True(launcher.Contains("NO_PROXY='' sh \"$menu\"")); Equal(4, f.Menu.Hashes.Count);
                }
            }));
            test("Maintenance direct launcher has no computer proxy and clears inherited explicit proxy", () => Async(async () => {
                using (var f = new Fixture()) { var r = await f.Run(); Equal(MaintenanceNetworkPath.Direct, r.NetworkPath); Equal(null, r.ProxyCleanup); Equal(0, f.ProxyFactories);
                    string launcher = Encoding.UTF8.GetString(f.Menu.Uploads["helper-launcher.sh"]); Equal(false, launcher.Contains("http://127.0.0.1:17890")); True(launcher.Contains("http_proxy='' https_proxy=''")); True(launcher.Contains("NO_PROXY='*' sh \"$menu\""));
                }
            }));
            foreach (string fault in new[] { "connect", "raw" }) { string value = fault; test("Maintenance proxy " + value + " failure never starts menu", () => Async(async () => {
                using (var f = new Fixture()) { f.Network.Raw = 0; f.Proxy.FailConnect = value == "connect"; f.Proxy.Raw = value == "raw" ? 503 : 200; var r = await f.Run(); Equal(0, f.Http.Urls.Count); Equal(0, f.Menu.Opens); True(f.Service.CanChangeConnection); Equal(false, f.Service.HasComputerProxyTunnel); True(f.Home.Live); Equal(false, f.Service.GetMaintenanceLaunchAvailability(f.Device).CanOpen); }
            })); }
            test("Maintenance cancellation waits for proxy release before relinquishing busy state", () => Async(async () => {
                using (var f = new Fixture()) using (var cancel = new CancellationTokenSource()) { f.Network.Raw = 0; f.Menu.AutoFinish = false; f.Proxy.BlockClose = true;
                    var running = f.Run(cancel.Token); await UntilAsync(() => f.Menu.Peer != null && f.Menu.Peer.Writes > 0); cancel.Cancel(); await f.Proxy.CloseEntered.Task;
                    True(f.Service.HasActiveMaintenanceMenu); Equal(false, f.Service.CanChangeConnection); Equal(false, running.IsCompleted); Equal(false, await f.Service.DisconnectAsync());
                    f.Proxy.CloseRelease.TrySetResult(0); var r = await running; True(r.ProxyCleanup.Succeeded); True(f.Service.CanChangeConnection); Equal(false, f.Service.HasActiveMaintenanceMenu); Equal(0, f.Home.Disposals);
                }
            }));
            test("Maintenance cancellation while connecting proxy closes resources before any menu download", () => Async(async () => {
                using (var f = new Fixture()) using (var cancel = new CancellationTokenSource()) { f.Network.Raw = 0; f.Proxy.CancelConnect = cancel; var r = await f.Run(cancel.Token);
                    Equal(MaintenancePreparationStatus.Cancelled, r.Preparation.Status); Equal(0, f.Http.Urls.Count); True(r.ProxyCleanup.Succeeded); Equal(1, f.Proxy.Closes); True(f.Service.CanChangeConnection); Equal(0, f.Home.Disposals);
                }
            }));
            test("Maintenance secondary loss cancels active menu without disconnecting homepage", () => Async(async () => {
                using (var f = new Fixture()) { f.Network.Raw = 0; f.Menu.AutoFinish = false; var running = f.Run(); await UntilAsync(() => f.Menu.Peer != null && f.Menu.Peer.Writes > 0);
                    f.Proxy.SecondaryLive = false; f.Proxy.Listening = false; f.Proxy.Lost(); var r = await running; True(r.ProxyCleanup.Succeeded); True(f.Home.Live); Equal(0, f.Home.Disposals); Equal(1, f.Menu.Opens); Equal(1, f.Proxy.Connects);
                }
            }));
            test("Maintenance primary disconnect closes proxy and reports unknown port release", () => Async(async () => {
                using (var f = new Fixture()) { f.Network.Raw = 0; f.Menu.AutoFinish = false; var running = f.Run(); await UntilAsync(() => f.Menu.Peer != null && f.Menu.Peer.Writes > 0);
                    f.Home.Live = false; f.Menu.HomeConnected = false; f.Proxy.Lost(); var r = await running; Equal(ComputerProxyRelease.Unknown, r.ProxyCleanup.Release); Equal(false, r.ProxyCleanup.Succeeded); True(r.Preparation.CleanupWarning.Contains("Unknown")); Equal(1, f.Proxy.Closes);
                }
            }));
            test("Maintenance service disposal cancels background check and cannot revive cache", () => Async(async () => {
                var f = new Fixture(); f.Network.BlockRaw = true; var running = f.Service.CheckMaintenanceNetworkAsync(f.Device, CancellationToken.None); await f.Network.Entered.Task; f.Service.Dispose(); await Catch(running); Equal(MaintenanceNetworkState.NotChecked, f.Service.GetMaintenanceNetworkSnapshot().State); Equal(1, f.Home.Disposals);
            }));
            RunUi(test);
            MaintenanceMenuRuntimeTests.RunNetworkShell(test);
        }
        static void RunUi(Action<string, Action> test)
        {
            test("Maintenance button stays grey without SSH", () => { using (var main = new MainForm()) { main.Show(); Application.DoEvents(); Equal(false, Entry(main).Enabled); main.Close(); } });
            test("Maintenance button waits for background Raw check then enables direct even with API 403", () => {
                using (var f = new Ui()) { f.Data.Network.BlockRaw = true; f.Data.Network.Api = 403; f.Main.Show(); PumpUntil(() => f.Data.Network.Entered.Task.IsCompleted); Equal(false, Entry(f.Main).Enabled); Equal(false, Control(f.Main, "connectButton").Enabled);
                    f.Data.Network.Release.TrySetResult(0); PumpUntil(() => Entry(f.Main).Enabled); Equal("插件维护 ›", Entry(f.Main).Text); Equal(0, f.Data.ProxyFactories);
                }
            });
            test("Maintenance button labels computer proxy without creating tunnel during detection", () => {
                using (var f = new Ui()) { f.Data.Network.Raw = 0; f.Main.Show(); PumpUntil(() => Entry(f.Main).Enabled); True(Entry(f.Main).Text.Contains("电脑代理")); Equal(0, f.Data.ProxyFactories);
                    Set(f.Main, "isInstallBusy", true); Invoke(f.Main, "RefreshMaintenanceEntry"); Equal(false, Entry(f.Main).Enabled); Set(f.Main, "isInstallBusy", false); Invoke(f.Main, "RefreshMaintenanceEntry"); True(Entry(f.Main).Enabled);
                }
            });
            test("Maintenance button remains grey if Raw and computer proxy both fail", () => {
                using (var f = new Ui()) { f.Data.Network.Raw = 0; f.Data.Network.Windows = false; f.Main.Show(); PumpUntil(() => !((bool)Field(f.Main, "maintenanceNetworkChecking")) && f.Data.Network.RawReads > 0); Equal(false, Entry(f.Main).Enabled); Equal(0, f.Data.ProxyFactories); }
            });
            test("Maintenance background check disables and then restores a ready install action", () => {
                using (var f = new Ui()) { SeedDirect(f.Data.Service, f.Data.Home); f.Main.Show(); Set(f.Main, "plugin", new PluginPackageInfo { CompatibilityStatus = CompatibilityStatus.Compatible }); Invoke(f.Main, "RefreshInstallButton"); True(Control(f.Main, "installButton").Enabled);
                    Set(f.Data.Service, "maintenanceNetworkFactory", new Func<SshClient, Func<bool>, IMaintenanceNetworkSession>((p, current) => f.Data.Network));
                    f.Data.Network.BlockRaw = true; Set(f.Data.Service, "maintenanceNetwork", new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.NotChecked }); Invoke(f.Main, "RefreshMaintenanceEntry"); PumpUntil(() => f.Data.Network.Entered.Task.IsCompleted);
                    Equal(false, Control(f.Main, "installButton").Enabled); Equal(false, Control(f.Main, "softwareButton").Enabled); Equal(false, Control(f.Main, "connectButton").Enabled);
                    f.Data.Network.Release.TrySetResult(0); PumpUntil(() => Entry(f.Main).Enabled); True(Control(f.Main, "installButton").Enabled); True(Control(f.Main, "softwareButton").Enabled);
                }
            });
            foreach (bool title in new[] { false, true }) { bool byTitle = title; test("Maintenance proxy window " + (byTitle ? "title close" : "return") + " waits for cleanup and preserves homepage", () => {
                using (var f = new Ui()) { f.Data.Network.Raw = 0; f.Data.Menu.AutoFinish = false; f.Main.Show(); PumpUntil(() => Entry(f.Main).Enabled); bool requested = false;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 20 }) { var watch = Stopwatch.StartNew(); timer.Tick += (s, e) => {
                        if (watch.ElapsedMilliseconds > 12000) throw new TimeoutException("Network menu UI timed out.");
                        var child = Application.OpenForms.OfType<PluginMaintenanceTerminalForm>().FirstOrDefault(w => w.Owner == f.Main);
                        if (requested || child == null || f.Data.Menu.Peer == null || f.Data.Menu.Peer.Writes == 0) return;
                        requested = true; Equal(false, Entry(f.Main).Enabled); Equal(false, f.Data.Service.CanChangeConnection);
                        if (byTitle) child.Close(); else ((Button)Control(child, "maintenanceTerminalReturn")).PerformClick();
                    }; timer.Start(); Click(f.Main); timer.Stop(); }
                    True(requested); True(f.Data.Service.LastComputerProxyClose.Succeeded); True(f.Data.Home.Live); Equal(0, f.Data.Home.Disposals); Equal(false, f.Data.Service.HasComputerProxyTunnel); True(Entry(f.Main).Enabled);
                }
            }); }
            test("Maintenance expired enabled click rechecks in launch lease instead of racing background detection", () => {
                using (var f = new Ui()) { f.Main.Show(); PumpUntil(() => Entry(f.Main).Enabled); Set(f.Data.Service, "maintenanceNetwork", new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.Available, Path = MaintenanceNetworkPath.Direct, CheckedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
                    int before = f.Data.Network.RawReads; f.Data.Network.Raw = 0; f.Data.Network.Windows = false; bool closed = false;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 20 }) { timer.Tick += (s, e) => { var child = Application.OpenForms.OfType<PluginMaintenanceTerminalForm>().FirstOrDefault(w => w.Owner == f.Main); if (child != null && child.Completion.IsCompleted && child.Result != null) { closed = true; child.Close(); } }; timer.Start(); Click(f.Main); timer.Stop(); }
                    True(closed); Equal(before + 1, f.Data.Network.RawReads); Equal(0, f.Data.Menu.Opens); Equal(false, Entry(f.Main).Enabled);
                }
            });
        }
        internal sealed class Network : IMaintenanceNetworkSession
        {
            public int Raw = 200, Api = 200, RawReads, WindowsReads, CandidateCalls; public bool Windows = true, FailRaw, FailApi, BlockRaw; public int[] Ports = { 7897 };
            public Func<bool> Online = () => true; public readonly List<string> Trace = new List<string>(); public readonly TaskCompletionSource<int> Entered = Signal(), Release = Signal();
            public bool Connected { get { return Online(); } }
            public async Task<ComputerProxyCommandResult> ReadAsync(ComputerProxyCommand command, CancellationToken token)
            {
                Trace.Add(command.ToString()); token.ThrowIfCancellationRequested(); if (command == ComputerProxyCommand.DirectRaw) { RawReads++; if (BlockRaw) using (token.Register(() => Release.TrySetCanceled())) { Entered.TrySetResult(0); await Release.Task.ConfigureAwait(false); } if (FailRaw) throw new IOException("secret"); }
                else if (FailApi) throw new IOException("secret");
                int status = command == ComputerProxyCommand.DirectRaw ? Raw : Api; return Reply(status == 0 ? 28 : 0, status, 0);
            }
            public IList<int> Candidates() { CandidateCalls++; Trace.Add("Candidates"); return Ports; }
            public Task<bool> CheckWindowsRawAsync(int port, CancellationToken token) { WindowsReads++; Trace.Add("Windows:" + port); token.ThrowIfCancellationRequested(); return Task.FromResult(Windows); }
        }
        sealed class Fixture : IDisposable
        {
            internal readonly Home Home = new Home(); internal readonly Network Network = new Network(); internal readonly Proxy Proxy;
            internal readonly MaintenanceMenuSshTests.Remote Menu = new MaintenanceMenuSshTests.Remote(); internal readonly MaintenanceMenuSshTests.Http Http = new MaintenanceMenuSshTests.Http();
            internal readonly SshService Service; internal int ProxyFactories;
            internal RouterEnvironment Device { get { string fp; using (var sha = SHA256.Create()) fp = "SHA256:" + Convert.ToBase64String(sha.ComputeHash(new byte[] { 1, 2, 3 })).TrimEnd('='); return new RouterEnvironment { IsConnected = true, HostKeyFingerprint = fp, PackageManager = PackageManagerKind.APK }; } }
            internal Fixture() { Proxy = new Proxy(Home); Service = new SshService(context => { ProxyFactories++; return Proxy; }, (primary, current) => { Network.Online = () => primary.IsConnected && current(); return Network; }, (primary, fp) => Menu);
                Set(Service, "client", Home); Set(Service, "connectionFactory", new Func<PasswordConnectionInfo>(() => new PasswordConnectionInfo("fixture.invalid", 22, "fixture", "inert"))); bool first; ((HostKeyTrustStore)Field(Service, "trustStore")).TryTrust("fixture.invalid", 22, Device.HostKeyFingerprint, out first); Set(Service, "maintenancePreparation", MaintenanceMenuSshTests.Service(Http)); }
            internal Task<MaintenanceMenuRunResult> Run(CancellationToken token = default(CancellationToken)) { return Service.RunMaintenanceMenuWithNetworkAsync(Device, t => { True(Service.HasActiveMaintenanceMenu); Equal(false, Service.CanChangeConnection); }, null, token); }
            public void Dispose() { Proxy.CloseRelease.TrySetResult(0); Service.Dispose(); }
        }
        sealed class Home : SshClient
        { public bool Live = true; public int Disposals; internal Home() : base("fixture.invalid", "fixture", "inert") { } public override bool IsConnected { get { return Live; } } protected override void Dispose(bool disposing) { Disposals++; Live = false; base.Dispose(disposing); } }
        sealed class Proxy : IComputerProxyTunnelSession
        {
            readonly Home home; internal int Api = 200, Raw = 200, Connects, Closes; internal bool SecondaryLive, Listening, FailConnect, BlockClose;
            internal CancellationTokenSource CancelConnect;
            internal readonly TaskCompletionSource<int> CloseEntered = Signal(), CloseRelease = Signal(); public event EventHandler ConnectionLost;
            internal Proxy(Home home) { this.home = home; }
            public bool PrimaryConnected { get { return home.Live; } } public bool PrimaryUnchanged { get { return home.Live; } } public bool SecondaryConnected { get { return SecondaryLive; } }
            public void Lost() { var h = ConnectionLost; if (h != null) h(this, EventArgs.Empty); }
            public Task<ComputerProxyCommandResult> ExecuteAsync(ComputerProxyCommand command, CancellationToken token) {
                token.ThrowIfCancellationRequested(); if (!home.Live) throw new IOException("offline");
                if (command == ComputerProxyCommand.Heartbeat) return Task.FromResult(new ComputerProxyCommandResult { Output = "IPH_PROXY_PRIMARY|OK\n" });
                if (command == ComputerProxyCommand.Preflight) return Task.FromResult(new ComputerProxyCommandResult { Output = "IPH_PROXY|CURL\n" });
                if (command == ComputerProxyCommand.PortState) return Task.FromResult(new ComputerProxyCommandResult { Output = Listening ? "IPH_PROXY_PORT|LISTEN|0100007F:45E2\nIPH_PROXY_PORT|END\n" : "IPH_PROXY_PORT|END\n" });
                return Task.FromResult(Listening ? Reply(0, command == ComputerProxyCommand.VerifyRaw ? Raw : Api, 200) : Reply(7, 0, 0));
            }
            public Task ConnectSecondaryAsync(CancellationToken token) { Connects++; if (FailConnect) throw new IOException("secret"); SecondaryLive = true; if (CancelConnect != null) { CancelConnect.Cancel(); token.ThrowIfCancellationRequested(); } return Task.FromResult(0); }
            public Task StartForwardAsync(int port, CancellationToken token) { Listening = true; return Task.FromResult(0); }
            public async Task CloseSecondaryAsync() { Closes++; CloseEntered.TrySetResult(0); if (BlockClose) await CloseRelease.Task.ConfigureAwait(false); SecondaryLive = false; Listening = false; }
            public void Dispose() { }
        }
        sealed class Ui : IDisposable
        {
            internal readonly MainForm Main = new MainForm(); internal readonly Fixture Data = new Fixture();
            internal Ui() { if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext)) SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext()); ((SshService)Field(Main, "ssh")).Dispose(); Set(Main, "ssh", Data.Service); Set(Main, "router", Data.Device); }
            public void Dispose() { Main.Close(); Main.Dispose(); Data.Dispose(); }
        }
        static ComputerProxyCommandResult Reply(int exit, int status, int connect) { return new ComputerProxyCommandResult { ExitCode = exit, Output = "IPH_PROXY_CURL|" + status.ToString("000") + "|" + connect.ToString("000") + "|0\n" }; }
        static TaskCompletionSource<int> Signal() { return new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); }
        static async Task Catch(Task task) { try { await task; } catch (OperationCanceledException) { } }
        static async Task UntilAsync(Func<bool> done) { var watch = Stopwatch.StartNew(); while (!done()) { if (watch.ElapsedMilliseconds > 10000) throw new TimeoutException("Maintenance async wait timed out."); await Task.Delay(5).ConfigureAwait(false); } }
        static void Async(Func<Task> action) { var task = Task.Run(action); if (Task.WhenAny(task, Task.Delay(15000)).GetAwaiter().GetResult() != task) throw new TimeoutException("Maintenance launch timed out."); task.GetAwaiter().GetResult(); }
        static Control Control(Control root, string name) { var field = root.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance); return field == null ? root.Controls.Find(name, true).Single() : (Control)field.GetValue(root); }
        static LinkLabel Entry(MainForm main) { return (LinkLabel)Control(main, "pluginMaintenanceLink"); }
        static void Click(MainForm main) { var link = Entry(main); typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(link, new object[] { new LinkLabelLinkClickedEventArgs(link.Links[0]) }); }
        static void Set(object owner, string field, object value) { owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(owner, value); }
        static object Field(object owner, string field) { return owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(owner); }
        static void Invoke(object owner, string method) { owner.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(owner, null); }
        static void PumpUntil(Func<bool> done) { var watch = Stopwatch.StartNew(); while (!done()) { if (watch.ElapsedMilliseconds > 12000) throw new TimeoutException("Maintenance UI wait timed out."); Application.DoEvents(); Thread.Sleep(1); } Application.DoEvents(); }
        static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection."); }
        static void True(bool value) { Equal(true, value); } static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
    }
}
