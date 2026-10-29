using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Renci.SshNet;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class ComputerProxyTunnelTests
    {
        internal static void Run(Action<string, Action> test)
        {
            foreach (int port in new[] { 0, -1, 65536, int.MaxValue }) { int value = port; test("Proxy rejects invalid local port " + value, () => Throws(() => ComputerProxyPolicy.ValidatePort(value))); }
            test("Proxy fixed commands preserve per-process environment and TLS boundaries", () => {
                foreach (var command in new[] { ComputerProxyCommand.VerifyApi, ComputerProxyCommand.VerifyRaw }) {
                    string text = ComputerProxyPolicy.GetCommand(command); True(text.StartsWith("http_proxy='http://127.0.0.1:17890' https_proxy='http://127.0.0.1:17890' "));
                    True(text.Contains("curl -q ") && text.Contains("NO_PROXY=''"));
                    foreach (string bad in new[] { "export ", "uci ", "sed ", "opkg ", "apk ", "/etc/", "--insecure", "--location", " > /tmp", "Authorization:" }) Equal(false, text.Contains(bad));
                }
                Throws(() => ComputerProxyPolicy.GetCommand((ComputerProxyCommand)999));
            });
            test("Proxy remote binding uses literal loopback at both ends", () => {
                using (var port = new ForwardedPortRemote(System.Net.IPAddress.Loopback, ComputerProxyPolicy.RemotePort, System.Net.IPAddress.Loopback, 7897)) {
                    Equal("127.0.0.1", port.BoundHost); Equal("127.0.0.1", port.Host); Equal((uint)17890, port.BoundPort);
                }
            });
            foreach (string output in new[] { "", "IPH_PROXY_PORT|UNKNOWN\n", "IPH_PROXY_PORT|END\nIPH_PROXY_PORT|END\n", "IPH_PROXY_PORT|LISTEN|BAD\nIPH_PROXY_PORT|END\n" }) {
                string value = output; test("Proxy unknown or malformed port state is not free " + value.Length, () => Equal(false, Port(value).Free));
            }
            test("Proxy supports IPv4 and dual loopback without accepting wildcard LAN or duplicates", () => {
                True(Port(Listener(false, false)).LoopbackOnly); True(Port(Listener(false, true)).LoopbackOnly);
                foreach (string address in new[] { "00000000", "0164A8C0", "00000000000000000000000000000000", "0100007F" })
                    Equal(false, Port("IPH_PROXY_PORT|LISTEN|0100007F:45E2\nIPH_PROXY_PORT|LISTEN|" + address + ":45E2\nIPH_PROXY_PORT|END\n").LoopbackOnly);
                Equal(false, ComputerProxyPortState.Parse(new ComputerProxyCommandResult { ExitCode = 1, Output = "IPH_PROXY_PORT|END\n" }).Known);
            });
            test("Proxy target 403 differs from proxy 407 and TLS failure", () => {
                var forbidden = Probe(0, "403", "200", "0"); True(forbidden.TransportOk); Equal("TargetHttp403", forbidden.Code);
                Equal("ProxyAuthenticationRequired", Probe(56, "000", "407", "0").Code);
                var tls = Probe(60, "000", "200", "20"); Equal(false, tls.TransportOk); Equal("TlsFailure", tls.Code);
                Equal("Timeout", Probe(28, "000", "000", "0").Code);
            });
            test("Proxy malformed or duplicate curl footer cannot be verified", () => {
                foreach (string output in new[] { "IPH_PROXY_CURL|200|200|0\nIPH_PROXY_CURL|200|200|0\n", "IPH_PROXY_CURL|200|200|0\nextra", "IPH_PROXY_CURL|200|200|9999999999999999\n", "" })
                    Equal(false, ComputerProxyPolicy.ParseProbe(ComputerProxyCommand.VerifyApi, new ComputerProxyCommandResult { Output = output }).TransportOk);
            });
            test("Proxy retains only numeric GitHub quota evidence", () => {
                var r = ComputerProxyPolicy.ParseProbe(ComputerProxyCommand.VerifyApi, new ComputerProxyCommandResult { Output = "Set-Cookie: secret\nx-ratelimit-remaining: 0\nx-ratelimit-reset: Bearer secret\nIPH_PROXY_CURL|403|200|0\n" });
                Equal("0", r.RateRemaining); Equal("[invalid]", r.RateReset); Equal(false, new JavaScriptSerializer().Serialize(r).Contains("secret"));
            });
            test("Proxy candidate detection filters credentials remote hosts and inactive ports", () => {
                Equal("7897|8080", string.Join("|", WindowsComputerProxyDetector.Candidates("http=127.0.0.1:7897;https=localhost:8080;socks=localhost:1080", new[] { 7897, 8080 })));
                Equal(0, WindowsComputerProxyDetector.Candidates("http=user:secret@127.0.0.1:12345;https=remote.invalid:12345", new[] { 12345 }).Count);
                Equal(0, WindowsComputerProxyDetector.Candidates("localhost:7897", new int[0]).Count);
            });
            test("Proxy create verify and close leave primary connected and port released", () => Async(async () => {
                var fake = new Remote(); var service = Controller(fake); Equal(ComputerProxyOutcome.Ready, (await service.CreateAsync(CancellationToken.None)).Outcome);
                Equal(ComputerProxyTunnelState.Active, service.State); True(fake.SecondaryConnected);
                var verify = await service.VerifyAsync(CancellationToken.None); Equal(ComputerProxyOutcome.Verified, verify.Outcome); Equal(2, verify.Probes.Count);
                var close = await service.CloseAsync(); True(close.Succeeded && close.PrimaryHealthy && close.ClosedPortConfirmed); Equal(ComputerProxyRelease.Released, close.Release);
                True(fake.PrimaryConnected); Equal(1, fake.Connects); Equal(1, fake.Forwards); Equal(1, fake.Closes); Equal(1, fake.Disposals); Equal(ComputerProxyTunnelState.Closed, service.State);
            }));
            test("Proxy dual loopback is accepted by actual lifecycle", () => Async(async () => { var f = new Remote { Dual = true }; var c = Controller(f); Equal(ComputerProxyOutcome.Ready, (await c.CreateAsync(CancellationToken.None)).Outcome); True((await c.CloseAsync()).Succeeded); }));
            foreach (string condition in new[] { "occupied", "unknown", "nocurl", "disconnected", "changed", "heartbeat" }) { string value = condition; test("Proxy preflight refuses " + value + " without second login", () => Async(async () => {
                var f = new Remote { Occupied = value == "occupied", Unknown = value == "unknown", NoCurl = value == "nocurl", PrimaryLive = value != "disconnected", Unchanged = value != "changed", BadHeartbeat = value == "heartbeat" };
                var r = await Controller(f).CreateAsync(CancellationToken.None); Equal(ComputerProxyOutcome.Failed, r.Outcome); Equal(0, f.Connects); Equal(0, f.Forwards); Equal(ComputerProxyRelease.NotCreated, r.Cleanup.Release);
            })); }
            test("Proxy binding wildcard is rejected and cleaned before target request", () => Async(async () => {
                var f = new Remote { Wildcard = true }; var r = await Controller(f).CreateAsync(CancellationToken.None); Equal("UnsafeOrUnknownListener", r.Code); True(r.Cleanup.Succeeded); Equal(0, f.LiveProbes);
            }));
            foreach (string condition in new[] { "connect", "bind" }) { string value = condition; test("Proxy " + value + " failure still disposes second SSH", () => Async(async () => {
                var f = new Remote { FailConnect = value == "connect", FailBind = value == "bind" }; var r = await Controller(f).CreateAsync(CancellationToken.None);
                Equal(ComputerProxyOutcome.Failed, r.Outcome); True(r.Cleanup.Succeeded); Equal(1, f.Closes); Equal(1, f.Disposals); True(f.PrimaryConnected);
            })); }
            test("Proxy pre-cancelled creation never connects", () => Async(async () => { var f = new Remote(); using (var c = new CancellationTokenSource()) { c.Cancel(); var r = await Controller(f).CreateAsync(c.Token); Equal(ComputerProxyOutcome.Cancelled, r.Outcome); Equal(0, f.Connects); } }));
            test("Proxy cancellation during bind uses independent cleanup", () => Async(async () => {
                using (var cancel = new CancellationTokenSource()) { var f = new Remote { CancelOnBind = cancel }; var r = await Controller(f).CreateAsync(cancel.Token); Equal(ComputerProxyOutcome.Cancelled, r.Outcome); True(r.Cleanup.Succeeded && f.PrimaryConnected); }
            }));
            test("Proxy repeated creation cannot replace an active owned tunnel", () => Async(async () => {
                var f = new Remote(); var c = Controller(f); await c.CreateAsync(CancellationToken.None); Equal(ComputerProxyOutcome.Rejected, (await c.CreateAsync(CancellationToken.None)).Outcome); Equal(1, f.Connects); True(f.SecondaryConnected); await c.CloseAsync();
            }));
            test("Proxy target 403 closes tunnel and logs safe HTTP diagnostics", () => Async(async () => {
                var logs = new Logs(); var f = new Remote { Http403 = true }; var c = Controller(f, logs); await c.CreateAsync(CancellationToken.None);
                var r = await c.VerifyAsync(CancellationToken.None); Equal(ComputerProxyOutcome.Failed, r.Outcome); Equal(403, r.Probes[0].HttpStatus); True(r.Probes[0].TransportOk && r.Cleanup.Succeeded);
                var entry = logs.Items.First(x => x.Phase == ComputerProxyPhase.VerifyApi); Equal("api.github.com", entry.Domain); Equal(ComputerProxyPolicy.ApiUrl, entry.Url); Equal<int?>(403, entry.HttpStatus);
                Equal(false, new JavaScriptSerializer().Serialize(logs.Items).Contains("secret"));
            }));
            test("Proxy verify exception is sanitized and always closes secondary", () => Async(async () => {
                var logs = new Logs(); var f = new Remote { FailProbe = true }; var c = Controller(f, logs); await c.CreateAsync(CancellationToken.None); var r = await c.VerifyAsync(CancellationToken.None);
                Equal("IOException", r.ErrorType); True(r.Cleanup.Succeeded); Equal(false, new JavaScriptSerializer().Serialize(r).Contains("secret")); Equal(false, new JavaScriptSerializer().Serialize(logs.Items).Contains("secret"));
                var failure = logs.Items.First(x => x.ErrorType == "IOException"); Equal(ComputerProxyPhase.VerifyRaw, failure.Phase); Equal("raw.githubusercontent.com", failure.Domain); Equal(ComputerProxyPolicy.RawUrl, failure.Url);
            }));
            test("Proxy progress consumer cannot prevent cleanup", () => Async(async () => { var f = new Remote(); var c = Controller(f, new Logs { Throw = true }); await c.CreateAsync(CancellationToken.None); True((await c.CloseAsync()).Succeeded); }));
            test("Proxy verify cancellation closes active tunnel", () => Async(async () => { var f = new Remote(); var c = Controller(f); await c.CreateAsync(CancellationToken.None); using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); var r = await c.VerifyAsync(cancel.Token); Equal(ComputerProxyOutcome.Cancelled, r.Outcome); True(r.Cleanup.Succeeded); } }));
            test("Proxy close is idempotent under concurrent callers", () => Async(async () => {
                var f = new Remote(); int release = 0; var c = Controller(f, null, r => release++); await c.CreateAsync(CancellationToken.None);
                var a = c.CloseAsync(); var b = c.CloseAsync(); True(ReferenceEquals(a, b)); await Task.WhenAll(a, b); Equal(1, f.Closes); Equal(1, f.Disposals); Equal(1, release);
            }));
            test("Proxy close before scheduled create cannot resurrect closed resources", () => Async(async () => { var f = new Remote(); var c = Controller(f); await c.CloseAsync(); var opening = await c.CreateAsync(CancellationToken.None); Equal(ComputerProxyOutcome.Cancelled, opening.Outcome); Equal(0, f.Connects); Equal(ComputerProxyTunnelState.Closed, c.State); Equal(1, f.Disposals); }));
            test("Proxy released port without final healthy primary is not overall success", () => Async(async () => { var f = new Remote(); var c = Controller(f); await c.CreateAsync(CancellationToken.None); f.BadHeartbeat = true; var close = await c.CloseAsync(); Equal(ComputerProxyRelease.Released, close.Release); Equal(false, close.PrimaryHealthy); Equal(false, close.Succeeded); }));
            test("Proxy close during preparation cancels and waits before releasing ownership", () => Async(async () => {
                var f = new Remote { Block = ComputerProxyCommand.Preflight }; var c = Controller(f); var opening = c.CreateAsync(CancellationToken.None); await f.Entered.Task;
                var closing = c.CloseAsync(); var r = await opening; Equal(ComputerProxyOutcome.Cancelled, r.Outcome); True((await closing).Succeeded); Equal(0, f.Connects);
            }));
            test("Proxy close during verification cancels in-flight curl and releases once", () => Async(async () => {
                var f = new Remote(); var c = Controller(f); await c.CreateAsync(CancellationToken.None); f.Block = ComputerProxyCommand.VerifyApi;
                var verifying = c.VerifyAsync(CancellationToken.None); await f.Entered.Task; var close = c.CloseAsync(); Equal(ComputerProxyOutcome.Cancelled, (await verifying).Outcome); True((await close).Succeeded); Equal(1, f.Closes);
            }));
            test("Proxy throwing cancellation callback cannot bypass awaited secondary cleanup", () => Async(async () => {
                var f = new Remote { ThrowCancelCallback = true }; var logs = new Logs(); var c = Controller(f, logs); await c.CreateAsync(CancellationToken.None); f.Block = ComputerProxyCommand.VerifyApi;
                var verifying = c.VerifyAsync(CancellationToken.None); await f.Entered.Task; var close = c.CloseAsync(); await verifying;
                True((await close).Succeeded); Equal(1, f.Closes); Equal(1, f.Disposals); True(logs.Items.Any(x => x.Code == "CancellationCallbackFailed")); Equal(false, new JavaScriptSerializer().Serialize(logs.Items).Contains("secret"));
            }));
            test("Proxy delayed release is observed instead of assumed", () => Async(async () => { var f = new Remote { DelayRelease = 2 }; var c = Controller(f); await c.CreateAsync(CancellationToken.None); var close = await c.CloseAsync(); True(close.Succeeded); Equal(3, close.ReleaseChecks); }));
            test("Proxy residual listener fails release verification without killing anything", () => Async(async () => { var f = new Remote { Residual = true }; var c = Controller(f); await c.CreateAsync(CancellationToken.None); var close = await c.CloseAsync(); Equal(ComputerProxyRelease.StillListening, close.Release); Equal(false, close.Succeeded); Equal(false, close.ClosedPortConfirmed); True(f.PrimaryConnected); }));
            test("Proxy unreadable release state remains unknown", () => Async(async () => { var f = new Remote(); var c = Controller(f); await c.CreateAsync(CancellationToken.None); f.Unknown = true; var close = await c.CloseAsync(); Equal(ComputerProxyRelease.Unknown, close.Release); Equal(false, close.Succeeded); }));
            test("Proxy failed old-port refusal confirmation is not released success", () => Async(async () => { var f = new Remote { FalseClosedProbe = true }; var c = Controller(f); await c.CreateAsync(CancellationToken.None); var close = await c.CloseAsync(); Equal(false, close.Succeeded); Equal(false, close.ClosedPortConfirmed); }));
            test("Proxy secondary disconnect exception still disposes and reports cleanup failure", () => Async(async () => { var f = new Remote { FailClose = true }; var c = Controller(f); await c.CreateAsync(CancellationToken.None); var close = await c.CloseAsync(); Equal(false, close.Succeeded); True(close.Errors.Any(x => x.Contains("IOException"))); Equal(1, f.Disposals); True(f.PrimaryConnected); }));
            test("Proxy lost primary closes secondary but cannot claim remote release", () => Async(async () => {
                var f = new Remote(); var c = Controller(f); await c.CreateAsync(CancellationToken.None); f.PrimaryLive = false; f.Lost(); var close = await c.CloseAsync();
                True(close.SecondaryClosed); Equal(ComputerProxyRelease.Unknown, close.Release); Equal(false, close.PrimaryHealthy); Equal(false, close.Succeeded);
            }));
            test("Proxy lost secondary automatically closes session", () => Async(async () => { var f = new Remote(); var c = Controller(f); await c.CreateAsync(CancellationToken.None); f.SecondaryLive = false; f.Listening = false; f.Lost(); True((await c.CloseAsync()).Succeeded); Equal(1, f.Disposals); }));
            test("Proxy heartbeat or identity change after open fails verification", () => Async(async () => { var f = new Remote(); var c = Controller(f); await c.CreateAsync(CancellationToken.None); f.Unchanged = false; var r = await c.VerifyAsync(CancellationToken.None); Equal(ComputerProxyOutcome.Failed, r.Outcome); Equal(ComputerProxyRelease.Unknown, r.Cleanup.Release); True(f.PrimaryConnected); }));
            test("Proxy verification after close rejects without new connection", () => Async(async () => { var f = new Remote(); var c = Controller(f); await c.CreateAsync(CancellationToken.None); await c.CloseAsync(); var r = await c.VerifyAsync(CancellationToken.None); True(r.Outcome != ComputerProxyOutcome.Verified); Equal(1, f.Connects); }));
            ServiceTests(test);
        }
        static void ServiceTests(Action<string, Action> test)
        {
            test("SSH proxy official context supplies same target fresh authentication and existing trust", () => Async(async () => {
                using (var f = new Fixture()) {
                    await f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, CancellationToken.None);
                    True(ReferenceEquals(f.Home, f.Context.Primary)); True(f.Context.IsCurrent()); Equal("fixture.invalid", f.Context.Endpoint.DeviceHost); Equal(22, f.Context.Endpoint.DevicePort);
                    using (var info = f.Context.Authentication()) { Equal(false, ReferenceEquals(info, f.Home.ConnectionInfo)); Equal("fixture", info.Username); }
                    True(f.Context.TrustHostKey(Fixture.Key)); Equal(false, f.Context.TrustHostKey(new byte[] { 9 })); await f.Service.CloseComputerProxyTunnelAsync(); True(f.Home.Live);
                }
            }));
            test("SSH proxy holds existing busy gate across create verify and active lifetime", () => Async(async () => {
                using (var f = new Fixture()) {
                    await f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, CancellationToken.None); True(f.Service.HasComputerProxyTunnel); Equal(false, f.Service.CanChangeConnection);
                    Equal(false, await f.Service.DisconnectAsync()); Throws(() => f.Service.ConnectAndDetectAsync("unused.invalid", "u", "secret", null, CancellationToken.None));
                    Throws(() => f.Service.InstallOneClickAsync(f.Device, "unused.run", null, CancellationToken.None));
                    Throws(() => f.Service.CheckSoftwareEnvironmentAsync(f.Device, null, null, CancellationToken.None));
                    Throws(() => f.Service.RunMaintenanceMenuAsync(f.Device, t => { }, null, CancellationToken.None));
                    Throws(() => f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, CancellationToken.None)); Equal(false, f.Service.GetMaintenanceMenuAvailability(f.Device).CanOpen);
                    Equal(ComputerProxyOutcome.Verified, (await f.Service.VerifyComputerProxyTunnelAsync(CancellationToken.None)).Outcome);
                    await f.Service.CloseComputerProxyTunnelAsync(); True(f.Service.CanChangeConnection); True(f.Service.GetMaintenanceMenuAvailability(f.Device).CanOpen); True(f.Home.Live); Equal(0, f.Home.Disposals);
                }
            }));
            foreach (string gate in new[] { "connecting", "softwareBusy", "menuLifetime", "pending" }) { string value = gate; test("SSH proxy refuses existing gate " + value, () => {
                using (var f = new Fixture()) {
                    if (value == "menuLifetime") Set(f.Service, value, new CancellationTokenSource());
                    else if (value == "pending") { var workflow = (InstallationWorkflowService)Field(f.Service, "installWorkflow"); var context = workflow.GetType().GetNestedType("Context", BindingFlags.NonPublic); Set(workflow, "unfinished", Activator.CreateInstance(context, true)); }
                    else Set(f.Service, value, true);
                    Throws(() => f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, CancellationToken.None)); Equal(0, f.FactoryCalls);
                }
            }); }
            foreach (string condition in new[] { "identity", "auth", "disconnected" }) { string value = condition; test("SSH proxy rejects untrusted context " + value, () => Async(async () => {
                using (var f = new Fixture()) { var device = f.Device; if (value == "identity") device.HostKeyFingerprint = "SHA256:other"; if (value == "auth") Set(f.Service, "connectionFactory", null); if (value == "disconnected") f.Home.Live = false;
                    Equal(ComputerProxyOutcome.Rejected, (await f.Service.CreateComputerProxyTunnelAsync(device, 7897, null, CancellationToken.None)).Outcome); Equal(0, f.FactoryCalls); True(f.Service.CanChangeConnection);
                }
            })); }
            test("SSH proxy cancelled request has no lease or factory side effects", () => Async(async () => { using (var f = new Fixture()) using (var c = new CancellationTokenSource()) { c.Cancel(); Equal(ComputerProxyOutcome.Cancelled, (await f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, c.Token)).Outcome); Equal(0, f.FactoryCalls); True(f.Service.CanChangeConnection); } }));
            test("SSH proxy cancellation during preparation restores busy state", () => Async(async () => {
                using (var f = new Fixture()) using (var c = new CancellationTokenSource()) { f.Remote.Block = ComputerProxyCommand.Preflight; var opening = f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, c.Token); await f.Remote.Entered.Task; Equal(false, f.Service.CanChangeConnection); c.Cancel(); var r = await opening; Equal(ComputerProxyOutcome.Cancelled, r.Outcome); True(f.Service.CanChangeConnection); Equal(false, f.Service.HasComputerProxyTunnel); }
            }));
            test("SSH proxy repeated close cannot release another operation's busy flag", () => Async(async () => { using (var f = new Fixture()) { await f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, CancellationToken.None); await f.Service.CloseComputerProxyTunnelAsync(); Set(f.Service, "softwareBusy", true); await f.Service.CloseComputerProxyTunnelAsync(); Equal(false, f.Service.CanChangeConnection); } }));
            test("SSH proxy service Dispose always closes second SSH before primary", () => Async(async () => {
                var f = new Fixture(); await f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, CancellationToken.None); f.Home.BeforeDispose = () => Equal(1, f.Remote.Closes);
                f.Service.Dispose(); Equal(1, f.Remote.Closes); Equal(1, f.Home.Disposals); Equal(false, f.Service.HasComputerProxyTunnel); Equal(false, f.Service.CanChangeConnection);
            }));
            test("SSH proxy Dispose during pending create cancels before primary release", () => Async(async () => {
                var f = new Fixture(); f.Remote.Block = ComputerProxyCommand.Preflight; var opening = f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, CancellationToken.None); await f.Remote.Entered.Task; f.Service.Dispose();
                Equal(ComputerProxyOutcome.Cancelled, (await opening).Outcome); Equal(0, f.Remote.Connects); Equal(1, f.Remote.Closes); Equal(1, f.Home.Disposals);
            }));
            test("SSH proxy network error auto-releases busy state with unknown remote status", () => Async(async () => {
                using (var f = new Fixture()) { await f.Service.CreateComputerProxyTunnelAsync(f.Device, 7897, null, CancellationToken.None); f.Remote.PrimaryLive = false; f.Remote.Lost(); var close = await f.Service.CloseComputerProxyTunnelAsync(); Equal(ComputerProxyRelease.Unknown, close.Release); True(f.Service.CanChangeConnection); Equal(false, f.Service.HasComputerProxyTunnel); }
            }));
            test("SSH proxy official trust remains scoped by host port and expected fingerprint", () => {
                using (var f = new Fixture()) { True(f.Service.IsComputerProxyHostKeyTrusted("fixture.invalid", 22, f.Device.HostKeyFingerprint, Fixture.Key)); Equal(false, f.Service.IsComputerProxyHostKeyTrusted("fixture.invalid", 23, f.Device.HostKeyFingerprint, Fixture.Key)); Equal(false, f.Service.IsComputerProxyHostKeyTrusted("other.invalid", 22, f.Device.HostKeyFingerprint, Fixture.Key)); Equal(false, f.Service.IsComputerProxyHostKeyTrusted("fixture.invalid", 22, "SHA256:other", Fixture.Key)); }
            });
            test("SSH proxy real adapter rejects alias settings without disposing primary info", () => Async(async () => {
                using (var home = new Home()) { var context = Context(home); context.Authentication = () => (PasswordConnectionInfo)home.ConnectionInfo;
                    using (var session = new SshComputerProxyTunnelSession(context)) { await ThrowsAsync(() => session.ConnectSecondaryAsync(CancellationToken.None)); await session.CloseSecondaryAsync(); True(home.Live); Equal("fixture.invalid", home.ConnectionInfo.Host); }
                    Equal(0, home.Disposals);
                }
            }));
            test("SSH proxy real adapter rejects wrong account or host before any network", () => Async(async () => {
                using (var home = new Home()) { var context = Context(home); context.Authentication = () => new PasswordConnectionInfo("other.invalid", 22, "other", "secret");
                    using (var session = new SshComputerProxyTunnelSession(context)) { await ThrowsAsync(() => session.ConnectSecondaryAsync(CancellationToken.None)); await session.CloseSecondaryAsync(); True(home.Live); }
                    Equal(0, home.Disposals);
                }
            }));
        }
        static ComputerProxyConnectionContext Context(Home home)
        { return new ComputerProxyConnectionContext { Primary = home, Endpoint = new ComputerProxyEndpoint { DeviceHost = "fixture.invalid", DevicePort = 22, LocalProxyPort = 7897 }, Account = "fixture", Authentication = () => new PasswordConnectionInfo("fixture.invalid", 22, "fixture", "inert"), IsCurrent = () => home.Live, TrustHostKey = key => true }; }
        sealed class Fixture : IDisposable
        {
            public static readonly byte[] Key = { 1, 2, 3, 4 }; public readonly Home Home = new Home(); public readonly Remote Remote = new Remote();
            public readonly SshService Service; public ComputerProxyConnectionContext Context; public int FactoryCalls;
            public RouterEnvironment Device { get { string fp; using (var sha = SHA256.Create()) fp = "SHA256:" + Convert.ToBase64String(sha.ComputeHash(Key)).TrimEnd('='); return new RouterEnvironment { IsConnected = true, HostKeyFingerprint = fp }; } }
            public Fixture() { Service = new SshService(context => { FactoryCalls++; Context = context; return Remote; }); Set(Service, "client", Home); Set(Service, "connectionFactory", new Func<PasswordConnectionInfo>(() => new PasswordConnectionInfo("fixture.invalid", 22, "fixture", "inert"))); bool first; ((HostKeyTrustStore)Field(Service, "trustStore")).TryTrust("fixture.invalid", 22, Device.HostKeyFingerprint, out first); }
            public void Dispose() { Service.Dispose(); }
        }
        sealed class Home : SshClient
        {
            public bool Live = true; public int Disposals; public Action BeforeDispose;
            public Home() : base("fixture.invalid", "fixture", "inert") { }
            public override bool IsConnected { get { return Live; } }
            protected override void Dispose(bool disposing) { if (BeforeDispose != null) BeforeDispose(); Disposals++; Live = false; base.Dispose(disposing); }
        }
        sealed class Logs : IProgress<ComputerProxyLog>
        { public bool Throw; public readonly List<ComputerProxyLog> Items = new List<ComputerProxyLog>(); public void Report(ComputerProxyLog value) { if (Throw) throw new Exception("secret"); lock (Items) Items.Add(value); } }
        sealed class Remote : IComputerProxyTunnelSession
        {
            public bool PrimaryLive = true, Unchanged = true, SecondaryLive, Listening, Occupied, Unknown, NoCurl, BadHeartbeat, Dual, Wildcard;
            public bool FailConnect, FailBind, Http403, FailProbe, FailClose, Residual, FalseClosedProbe, ThrowCancelCallback;
            public int Connects, Forwards, Closes, Disposals, LiveProbes, DelayRelease; public CancellationTokenSource CancelOnBind;
            public ComputerProxyCommand? Block; public readonly TaskCompletionSource<int> Entered = Signal(); public readonly TaskCompletionSource<int> Release = Signal();
            public event EventHandler ConnectionLost;
            public bool PrimaryConnected { get { return PrimaryLive; } } public bool PrimaryUnchanged { get { return Unchanged; } } public bool SecondaryConnected { get { return SecondaryLive; } }
            public void Lost() { var handler = ConnectionLost; if (handler != null) handler(this, EventArgs.Empty); }
            public async Task<ComputerProxyCommandResult> ExecuteAsync(ComputerProxyCommand command, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); if (!PrimaryLive) throw new IOException("secret lost");
                if (Block == command && Closes == 0) { using (token.Register(() => { Release.TrySetCanceled(); if (ThrowCancelCallback) throw new Exception("secret callback"); })) { Entered.TrySetResult(0); await Release.Task.ConfigureAwait(false); } }
                token.ThrowIfCancellationRequested();
                if (command == ComputerProxyCommand.Heartbeat) return new ComputerProxyCommandResult { Output = BadHeartbeat ? "BROKEN" : "IPH_PROXY_PRIMARY|OK\n" };
                if (command == ComputerProxyCommand.Preflight) return new ComputerProxyCommandResult { Output = NoCurl ? "IPH_PROXY|NO_CURL\n" : "IPH_PROXY|CURL\n" };
                if (command == ComputerProxyCommand.PortState) { bool linger = Closes > 0 && DelayRelease-- > 0; return new ComputerProxyCommandResult { Output = Unknown ? "IPH_PROXY_PORT|UNKNOWN\n" : Occupied || Listening || linger ? Listener(Wildcard, Dual) : "IPH_PROXY_PORT|END\n" }; }
                if (Listening) { LiveProbes++; if (FailProbe) throw new IOException("secret https://user:password@private.invalid/?token=secret"); return Reply(0, Http403 ? "403" : "200", "200", "0"); }
                return FalseClosedProbe ? Reply(0, "200", "200", "0") : Reply(7, "000", "000", "0");
            }
            public Task ConnectSecondaryAsync(CancellationToken token) { Connects++; if (FailConnect) throw new IOException("secret"); SecondaryLive = true; return Task.FromResult(0); }
            public Task StartForwardAsync(int port, CancellationToken token) { Forwards++; Listening = true; if (FailBind) throw new IOException("secret"); if (CancelOnBind != null) CancelOnBind.Cancel(); return Task.FromResult(0); }
            public Task CloseSecondaryAsync() { Closes++; SecondaryLive = false; Listening = Residual; if (FailClose) throw new IOException("secret"); return Task.FromResult(0); }
            public void Dispose() { Disposals++; }
        }
        static ComputerProxyTunnelService Controller(Remote session, IProgress<ComputerProxyLog> logs = null, Action<ComputerProxyCloseResult> released = null)
        { return new ComputerProxyTunnelService(session, new ComputerProxyEndpoint { DeviceHost = "fixture.invalid", DevicePort = 22, LocalProxyPort = 7897 }, logs, released); }
        static ComputerProxyCommandResult Reply(int exit, string http, string connect, string tls)
        { return new ComputerProxyCommandResult { ExitCode = exit, Output = "IPH_PROXY_CURL|" + http + "|" + connect + "|" + tls + "\n" }; }
        static ComputerProxyProbe Probe(int exit, string http, string connect, string tls) { return ComputerProxyPolicy.ParseProbe(ComputerProxyCommand.VerifyApi, Reply(exit, http, connect, tls)); }
        static ComputerProxyPortState Port(string output) { return ComputerProxyPortState.Parse(new ComputerProxyCommandResult { Output = output }); }
        static string Listener(bool wildcard, bool dual) { return "IPH_PROXY_PORT|LISTEN|" + (wildcard ? "00000000" : "0100007F") + ":45E2\n" + (dual ? "IPH_PROXY_PORT|LISTEN|00000000000000000000000001000000:45E2\n" : "") + "IPH_PROXY_PORT|END\n"; }
        static TaskCompletionSource<int> Signal() { return new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); }
        static void Set(object instance, string name, object value) { instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value); }
        static object Field(object instance, string name) { return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
        static void Async(Func<Task> action) { var task = Task.Run(action); if (Task.WhenAny(task, Task.Delay(15000)).GetAwaiter().GetResult() != task) throw new TimeoutException("Proxy offline test timed out."); task.GetAwaiter().GetResult(); }
        static async Task ThrowsAsync(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected failure."); }
        static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected failure."); }
        static void True(bool value) { Equal(true, value); }
        static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
    }
}
