using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Controls;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class MaintenanceMenuSshTests
    {
        private const string Commit = "0123456789abcdef0123456789abcdef01234567";
        private const string Identity = "123|456|1793000000";
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void Run(Action<string, Action> test)
        {
            test("Menu SSH verified live workspace covers four original payload hashes before PTY", () => Async(async () => {
                var http = new Http(); var remote = new Remote(); var service = Service(http); string output = "";
                var result = await service.RunMenuAsync(remote, t => { Equal(false, t.Send("too early\n")); t.Output += text => output += text; }, null, CancellationToken.None);
                Finished(result, remote, 0); Equal(2, http.Urls.Count); Equal(0, http.Urls.Count(u => u == ComputerProxyPolicy.ApiUrl));
                Equal(ThirdPartyScriptCatalog.MainMenuUrl, http.Urls[0]); Equal(ThirdPartyScriptCatalog.MainMenuLicenseUrl, http.Urls[1]);
                Equal(4, remote.Uploads.Count); Equal(4, remote.Hashes.Count); Equal(0, remote.PreviewCleanups); Equal(1, remote.Opens);
                True(remote.Uploads["menu.sh"].SequenceEqual(http.Menu)); True(remote.Uploads["LICENSE"].SequenceEqual(http.License));
                string notice = Encoding.UTF8.GetString(remote.Uploads["NOTICE.txt"]);
                True(notice.Contains("Ref: main")); True(notice.Contains("Commit: not resolved (direct branch download)"));
                Equal(Hash(http.Menu), result.Preparation.ScriptSha256); Equal(Hash(http.License), result.Preparation.LicenseSha256);
                True(output.Contains("中文😀 prompt> ")); Equal(1, remote.Peer.Writes); Equal(1, remote.Peer.Disposals);
                Equal(false, Directory.Exists(result.Preparation.LocalDirectory)); True(remote.HomeConnected);
                Equal("create,upload:menu.sh,upload:LICENSE,upload:NOTICE.txt,upload:helper-launcher.sh,hash:menu.sh,hash:LICENSE,hash:NOTICE.txt,hash:helper-launcher.sh,open,state,clean", string.Join(",", remote.Trace));
            }));
            test("Menu SSH explicit SHA and PTY size stay fixed throughout run", () => Async(async () => {
                var http = new Http(); var remote = new Remote();
                var result = await Service(http).RunMenuAsync(remote, t => { }, null, CancellationToken.None, Commit.ToUpperInvariant(), 132, 44);
                Finished(result, remote, 0); Equal(2, http.Urls.Count); True(http.Urls.All(u => u.Contains("/" + Commit + "/")));
                Equal((uint)132, remote.Columns); Equal((uint)44, remote.Rows);
            }));
            foreach (string file in new[] { "menu.sh", "LICENSE", "NOTICE.txt", "helper-launcher.sh" })
            {
                string item = file;
                test("Menu SSH mismatched hash blocks all launch activity " + item, () => Async(async () => {
                    var remote = new Remote { BadHash = item }; var result = await Service(new Http()).RunMenuAsync(remote, t => { throw new Exception("must not bind"); }, null, CancellationToken.None);
                    Equal(MaintenancePreparationStatus.Failed, result.Preparation.Status); Equal(false, result.Preparation.ExecutionAttempted);
                    Equal(0, remote.Opens); Equal(1, remote.PreviewCleanups); True(result.Preparation.LocalCleanupSucceeded); True(remote.HomeConnected);
                }));
            }
            test("Menu SSH invalid script never creates a remote directory", () => Async(async () => {
                var http = new Http { Menu = Encoding.UTF8.GetBytes("<html>failure</html>") }; var remote = new Remote();
                var result = await Service(http).RunMenuAsync(remote, t => { }, null, CancellationToken.None);
                Equal(MaintenancePreparationStatus.Failed, result.Preparation.Status); Equal(2, http.Urls.Count); Equal(0, remote.Creates); Equal(0, remote.Opens);
            }));
            test("Menu SSH invalid license never creates a remote directory", () => Async(async () => {
                var http = new Http { License = Encoding.UTF8.GetBytes("invalid") }; var remote = new Remote();
                var result = await Service(http).RunMenuAsync(remote, t => { }, null, CancellationToken.None);
                Equal(MaintenancePreparationStatus.Failed, result.Preparation.Status); Equal(2, http.Urls.Count); Equal(0, remote.Creates);
            }));
            test("Menu SSH disconnected and pre-cancelled preparation have no side effects", () => Async(async () => {
                var http = new Http(); var remote = new Remote { HomeConnected = false }; var service = Service(http);
                Equal(MaintenancePreparationStatus.Rejected, (await service.RunMenuAsync(remote, t => { }, null, CancellationToken.None)).Preparation.Status);
                remote.HomeConnected = true;
                using (var cancel = new CancellationTokenSource()) {
                    cancel.Cancel(); Equal(MaintenancePreparationStatus.Cancelled, (await service.RunMenuAsync(remote, t => { }, null, cancel.Token)).Preparation.Status);
                }
                Equal(0, http.Urls.Count); Equal(0, remote.Creates); Equal(0, remote.Opens);
            }));
            test("Menu SSH invalid SHA size and missing consumer fail before downloads", () => Async(async () => {
                var http = new Http(); var service = Service(http); var remote = new Remote();
                await ThrowsAsync<InvalidDataException>(() => service.RunMenuAsync(remote, t => { }, null, CancellationToken.None, "main"));
                await ThrowsAsync<ArgumentOutOfRangeException>(() => service.RunMenuAsync(remote, t => { }, null, CancellationToken.None, null, 0, 30));
                await ThrowsAsync<ArgumentOutOfRangeException>(() => service.RunMenuAsync(remote, t => { }, null, CancellationToken.None, null, 100, 1001));
                await ThrowsAsync<ArgumentNullException>(() => service.RunMenuAsync(remote, null, null, CancellationToken.None)); Equal(0, http.Urls.Count);
            }));
            test("Menu SSH cancellation during upload uses a fresh cleanup token", () => Async(async () => {
                using (var cancel = new CancellationTokenSource()) {
                    var remote = new Remote { UploadHook = () => cancel.Cancel() };
                    var result = await Service(new Http()).RunMenuAsync(remote, t => { }, null, cancel.Token);
                    Equal(MaintenancePreparationStatus.Cancelled, result.Preparation.Status); Equal(0, remote.Opens);
                    Equal(1, remote.PreviewCleanups); True(result.Preparation.RemoteCleanupSucceeded && result.Preparation.LocalCleanupSucceeded);
                }
            }));
            foreach (string fault in new[] { "throw", "null", "disconnect" })
            {
                string value = fault;
                test("Menu SSH PTY open failure never dispatches and releases operation gate " + value, () => Async(async () => {
                    var remote = new Remote { OpenFault = value }; var service = Service(new Http());
                    var first = await service.RunMenuAsync(remote, t => { throw new Exception("must not bind"); }, null, CancellationToken.None);
                    Equal(MaintenancePreparationStatus.Failed, first.Preparation.Status); Equal(false, first.Preparation.ExecutionAttempted);
                    Equal(1, remote.PreviewCleanups); True(first.Preparation.LocalCleanupSucceeded);
                    remote = new Remote(); Finished(await service.RunMenuAsync(remote, t => { }, null, CancellationToken.None), remote, 0);
                }));
            }
            test("Menu SSH throwing binding callback closes only the opened channel", () => Async(async () => {
                var remote = new Remote(); var result = await Service(new Http()).RunMenuAsync(remote, t => { throw new IOException("view closed"); }, null, CancellationToken.None);
                Equal(MaintenancePreparationStatus.Failed, result.Preparation.Status); Equal(false, result.Preparation.ExecutionAttempted);
                Equal(0, remote.Peer.Writes); Equal(1, remote.Peer.Disposals); Equal(1, remote.PreviewCleanups); True(remote.HomeConnected);
            }));
            test("Menu SSH close before dispatch cannot send the menu start command", () => Async(async () => {
                var remote = new Remote(); Task closing = null;
                var result = await Service(new Http()).RunMenuAsync(remote, t => closing = t.CloseAsync(), null, CancellationToken.None);
                await closing; Equal(MaintenancePreparationStatus.Cancelled, result.Preparation.Status);
                Equal(false, result.Preparation.ExecutionAttempted); Equal(0, remote.Peer.Writes); Equal(1, remote.Peer.Disposals); True(remote.HomeConnected);
            }));
            test("Menu SSH input resize and control C use only the menu channel", () => Async(async () => {
                var remote = new Remote { AutoFinish = false, FinishOnInput = true }; MaintenanceMenuTerminal terminal = null; string output = "";
                var task = Service(new Http()).RunMenuAsync(remote, t => { terminal = t; t.Output += value => output += value; }, null, CancellationToken.None);
                await Until(() => terminal != null && terminal.IsInputReady && output.Contains("prompt> "));
                True(terminal.Resize(140, 48)); True(terminal.Send("选项\n\u0003")); var result = await task;
                Finished(result, remote, 0); Equal("选项\n\u0003", remote.Input); Equal((uint)140, remote.ResizedColumns); Equal((uint)48, remote.ResizedRows);
                Equal(false, terminal.Send("after close")); Equal(false, terminal.Resize(80, 24));
                Throws<ArgumentOutOfRangeException>(() => terminal.Resize(0, 24)); True(remote.HomeConnected);
            }));
            test("Menu SSH view output exceptions cannot interrupt protocol or final drain", () => Async(async () => {
                var remote = new Remote(); string received = "";
                var result = await Service(new Http()).RunMenuAsync(remote, t => { t.Output += value => { throw new IOException("disposed view"); }; t.Output += value => received += value; }, null, CancellationToken.None);
                Finished(result, remote, 0); True(received.Contains("中文😀 prompt> "));
            }));
            foreach (int code in new[] { 7, 130, 143, 255 })
            {
                int value = code;
                test("Menu SSH nonzero foreground result has known exit and safe cleanup " + code, () => Async(async () => {
                    var remote = new Remote { ExitCode = value }; Finished(await Service(new Http()).RunMenuAsync(remote, t => { }, null, CancellationToken.None), remote, value);
                }));
            }
            test("Menu SSH EOF alone is unknown and never uses preview cleanup", () => Async(async () => {
                var remote = new Remote { DropExit = true, StateOverride = "UNKNOWN" };
                Unknown(await Service(new Http()).RunMenuAsync(remote, t => { }, null, CancellationToken.None), remote);
            }));
            test("Menu SSH lost stream markers recover only from owned durable exit", () => Async(async () => {
                var remote = new Remote { DropStart = true, DropExit = true };
                Finished(await Service(new Http()).RunMenuAsync(remote, t => { }, null, CancellationToken.None), remote, 0);
            }));
            foreach (string fault in new[] { "wrong-task", "wrong-run", "wrong-pid", "wrong-code", "malformed", "throw", "RUNNING", "UNKNOWN" })
            {
                string value = fault;
                test("Menu SSH remote state cannot authorize cleanup " + fault, () => Async(async () => {
                    var remote = new Remote { StateOverride = value };
                    Unknown(await Service(new Http()).RunMenuAsync(remote, t => { }, null, CancellationToken.None), remote);
                }));
            }
            test("Menu SSH transport disconnect keeps final status unknown and retains evidence", () => Async(async () => {
                var remote = new Remote { DisconnectOnWrite = true };
                Unknown(await Service(new Http()).RunMenuAsync(remote, t => { }, null, CancellationToken.None), remote); Equal(false, remote.HomeConnected);
            }));
            test("Menu SSH write failure is attempted unknown and cannot enter preparation cleanup", () => Async(async () => {
                var remote = new Remote { WriteFailure = true, StateOverride = "UNKNOWN" };
                Unknown(await Service(new Http()).RunMenuAsync(remote, t => { }, null, CancellationToken.None), remote); True(remote.HomeConnected);
            }));
            test("Menu SSH terminal close is idempotent and preserves home SSH", () => Async(async () => {
                var remote = new Remote { AutoFinish = false }; MaintenanceMenuTerminal terminal = null;
                var task = Service(new Http()).RunMenuAsync(remote, t => terminal = t, null, CancellationToken.None);
                await Until(() => terminal != null && terminal.IsInputReady && terminal.Snapshot.ProcessId.HasValue);
                await Task.WhenAll(terminal.CloseAsync(), terminal.CloseAsync()); Unknown(await task, remote);
                Equal(1, remote.Peer.Disposals); True(remote.HomeConnected);
            }));
            test("Menu SSH cancellation after dispatch closes channel and preserves home SSH", () => Async(async () => {
                using (var cancel = new CancellationTokenSource()) {
                    var remote = new Remote { AutoFinish = false }; MaintenanceMenuTerminal terminal = null;
                    var task = Service(new Http()).RunMenuAsync(remote, t => terminal = t, null, cancel.Token);
                    await Until(() => terminal != null && terminal.IsInputReady && terminal.Snapshot.ProcessId.HasValue);
                    cancel.Cancel(); Unknown(await task, remote); Equal(1, remote.Peer.Disposals); True(remote.HomeConnected);
                }
            }));
            test("Menu SSH exit marker cannot release gate or clean before final output EOF", () => Async(async () => {
                var http = new Http(); var service = Service(http); var remote = new Remote { HoldEof = true }; MaintenanceMenuTerminal terminal = null; string output = "";
                var task = service.RunMenuAsync(remote, t => { terminal = t; t.Output += value => output += value; }, null, CancellationToken.None);
                await Until(() => terminal != null && terminal.Snapshot.Status == MaintenanceMenuRunStatus.AwaitingClose);
                Equal(0, remote.StateProbes); Equal(0, remote.FinishedCleanups); Equal(false, task.IsCompleted);
                Equal(MaintenancePreparationStatus.Busy, (await service.PrepareMenuAsync(remote, null, CancellationToken.None)).Status);
                Equal(MaintenancePreparationStatus.Busy, (await service.RunMenuAsync(new Remote(), t => { }, null, CancellationToken.None)).Preparation.Status);
                remote.Peer.Emit("\n尾部😀" + new string('x', 20000)); remote.Peer.End();
                Finished(await task, remote, 0); True(output.EndsWith(new string('x', 20000), StringComparison.Ordinal)); True(output.Contains("尾部😀"));
            }));
            test("Menu SSH mutex remains held during remote final cleanup", () => Async(async () => {
                var service = Service(new Http()); var remote = new Remote(); var entered = Signal(); var release = Signal();
                remote.CleanHook = async token => { entered.SetResult(0); await release.Task; token.ThrowIfCancellationRequested(); };
                var task = service.RunMenuAsync(remote, t => { }, null, CancellationToken.None); await entered.Task;
                Equal(MaintenancePreparationStatus.Busy, (await service.RunMenuAsync(new Remote(), t => { }, null, CancellationToken.None)).Preparation.Status);
                release.SetResult(0); Finished(await task, remote, 0);
                var next = new Remote(); Finished(await service.RunMenuAsync(next, t => { }, null, CancellationToken.None), next, 0);
            }));
            foreach (string fault in new[] { "failed", "wrong-ack", "throw" })
            {
                string value = fault;
                test("Menu SSH cleanup failure preserves known exit plus warning " + fault, () => Async(async () => {
                    var remote = new Remote { CleanFault = value }; var result = await Service(new Http()).RunMenuAsync(remote, t => { }, null, CancellationToken.None);
                    Equal(MaintenancePreparationStatus.Completed, result.Preparation.Status); Equal(MaintenanceMenuRunStatus.ExitedNormally, result.Runtime.Status);
                    Equal(false, result.Preparation.RemoteCleanupSucceeded); Equal(false, result.Runtime.CleanupCompleted); True(!string.IsNullOrEmpty(result.Preparation.CleanupWarning));
                    Equal(0, remote.PreviewCleanups); True(result.Preparation.LocalCleanupSucceeded); True(remote.HomeConnected);
                }));
            }
            RunTransport(test);
            RunSshGate(test);
            RunTerminalView(test);
        }

        private static void RunTerminalView(Action<string, Action> test)
        {
            test("Menu SSH TerminalView binds controller for real key input output and final render", () => {
                using (var window = new Form()) using (var view = new TerminalView(new Clipboard())) {
                    window.Controls.Add(view); window.Show();
                    var remote = new Remote { AutoFinish = false }; MaintenanceMenuTerminal terminal = null;
                    var task = Service(new Http()).RunMenuAsync(remote, value => {
                        window.Invoke(new Action(() => { view.Bind(value); terminal = value; }));
                    }, null, CancellationToken.None);
                    try {
                        PumpUntil(() => terminal != null && terminal.IsInputReady && view.Text.Contains("prompt> "), "controller bind and prompt");
                        foreach (char character in "abc") typeof(TerminalView).GetMethod("OnKeyPress", Private).Invoke(view, new object[] { new KeyPressEventArgs(character) });
                        foreach (Keys key in new[] { Keys.Enter, Keys.Control | Keys.C })
                            typeof(TerminalView).GetMethod("ProcessCmdKey", Private).Invoke(view, new object[] { new Message(), key });
                        PumpUntil(() => remote.Input == "abc\n\u0003", "keyboard relay; received=" + remote.Input);
                        remote.Peer.Emit("\r\n最后一行😀"); remote.Finish(); PumpUntil(() => task.IsCompleted && view.PendingChunkCount == 0, "final drain");
                        Finished(task.GetAwaiter().GetResult(), remote, 0); True(view.Text.Contains("最后一行😀")); Equal(false, view.Text.Contains("ISTORE_MENU_V1"));
                        True(remote.HomeConnected);
                    } finally { if (terminal != null) { var closing = terminal.CloseAsync(); PumpUntil(() => closing.IsCompleted); } window.Close(); }
                }
            });
            test("Menu SSH terminal view close detaches output and closes only menu controller", () => {
                using (var window = new Form()) {
                    var view = new TerminalView(new Clipboard()); window.Controls.Add(view); window.Show();
                    var remote = new Remote { AutoFinish = false }; MaintenanceMenuTerminal terminal = null;
                    var task = Service(new Http()).RunMenuAsync(remote, value => window.Invoke(new Action(() => { view.Bind(value); terminal = value; })), null, CancellationToken.None);
                    PumpUntil(() => terminal != null && terminal.IsInputReady && view.Text.Contains("prompt> "));
                    view.Dispose(); var close = terminal.CloseAsync(); PumpUntil(() => close.IsCompleted && task.IsCompleted);
                    Unknown(task.GetAwaiter().GetResult(), remote); True(remote.HomeConnected); window.Close();
                }
            });
        }
        private sealed class Clipboard : ITerminalClipboard { public string ReadText() { return null; } public void WriteText(string text) { } }
        private static void PumpUntil(Func<bool> condition, string checkpoint = "unspecified")
        {
            var watch = Stopwatch.StartNew();
            while (!condition()) { if (watch.ElapsedMilliseconds > 10000) throw new TimeoutException("Menu terminal UI checkpoint timed out: " + checkpoint); Application.DoEvents(); Thread.Sleep(5); }
            Application.DoEvents();
        }

        private static void RunTransport(Action<string, Action> test)
        {
            test("Menu SSH borrowed stream drains buffers then reports channel error", () => {
                var stream = new Peer(); bool connected = true; var transport = new SshShellTransport(stream, (c, r) => { }, () => connected);
                stream.Emit("中文"); stream.End(); transport.ReportFailure(new IOException("lost channel")); var bytes = new byte[20];
                Equal(Encoding.UTF8.GetByteCount("中文"), transport.Read(bytes)); Throws<IOException>(() => transport.Read(bytes));
                transport.Dispose(); transport.Dispose(); Equal(1, stream.Disposals); True(connected);
            });
            test("Menu SSH parent disconnect converts bare EOF into transport failure", () => {
                var stream = new Peer(); stream.End(); using (var transport = new SshShellTransport(stream, (c, r) => { }, () => false))
                    Throws<IOException>(() => transport.Read(new byte[1]));
            });
            test("Menu SSH adapter refuses PTY creation on disconnected actual SSH client", () => Async(async () => {
                using (var ssh = new SshClient("fixture.invalid", "fixture", "inert")) {
                    var type = typeof(SshService).Assembly.GetType("iStoreOS.PluginHelper.Services.SshMaintenancePreparationSession");
                    var session = (IMaintenanceMenuSession)Activator.CreateInstance(type, ssh, new Func<SftpClient>(() => { throw new Exception("no SFTP allowed"); }), new Func<string, string>(value => value));
                    await ThrowsAsync<IOException>(() => session.OpenTerminalAsync(100, 30, CancellationToken.None)); Equal(false, ssh.IsConnected);
                }
            }));
            test("Menu SSH actual SSH.NET ShellStream owns one PTY channel and never its parent client", () => Async(async () => {
                using (var fixture = new SshFixture()) {
                    fixture.Proxy.AllowChannel = true;
                    var type = typeof(SshService).Assembly.GetType("iStoreOS.PluginHelper.Services.SshMaintenancePreparationSession");
                    var session = (IMaintenanceMenuSession)Activator.CreateInstance(type, fixture.Client,
                        new Func<SftpClient>(() => { throw new Exception("PTY must reuse SSH authentication"); }), new Func<string, string>(text => text));
                    var transport = await session.OpenTerminalAsync(132, 44, CancellationToken.None);
                    var channel = fixture.Proxy.Channel; Equal(1, channel.Opens);
                    var request = channel.Requests["SendPseudoTerminalRequest"];
                    Equal("xterm-256color", request[0]); Equal((uint)132, request[1]); Equal((uint)44, request[2]);
                    True(channel.Requests.ContainsKey("SendShellRequest"));
                    transport.Resize(150, 50); Equal((uint)150, channel.Requests["SendWindowChangeRequest"][0]);
                    transport.Dispose(); transport.Dispose(); Equal(1, channel.Disposals); Equal(0, fixture.Proxy.Disposals);
                    True(fixture.Client.IsConnected); True(fixture.Service.IsConnected);
                }
            }));
        }

        private static void RunSshGate(Action<string, Action> test)
        {
            test("Menu SSH service refuses disconnected disposed and untrusted identity without effects", () => Async(async () => {
                using (var ssh = new SshService()) {
                    Equal(MaintenancePreparationStatus.Rejected, (await ssh.RunMaintenanceMenuAsync(Device(), t => { }, null, CancellationToken.None)).Preparation.Status);
                    Equal(false, ssh.HasActiveMaintenanceMenu); Equal(false, ssh.CanReconnect);
                    ssh.Dispose(); Throws<ObjectDisposedException>(() => ssh.RunMaintenanceMenuAsync(Device(), t => { }, null, CancellationToken.None));
                }
                using (var fixture = new SshFixture()) {
                    foreach (var device in new[] { null, new RouterEnvironment { IsConnected = false, HostKeyFingerprint = fixture.Fingerprint }, new RouterEnvironment { IsConnected = true, HostKeyFingerprint = "SHA256:wrong" } })
                        Equal(MaintenancePreparationStatus.Rejected, (await fixture.Service.RunMaintenanceMenuAsync(device, t => { }, null, CancellationToken.None)).Preparation.Status);
                    Equal(0, fixture.Http.Urls.Count); Equal(0, fixture.Proxy.Disposals); True(fixture.Client.IsConnected);
                }
            }));
            foreach (string busy in new[] { "connecting", "softwareBusy", "pending-installation" })
            {
                string value = busy;
                test("Menu SSH service respects existing exclusive operation " + busy, () => {
                    using (var fixture = new SshFixture()) {
                        if (value == "pending-installation") {
                            var workflow = Field(fixture.Service, "installWorkflow"); var unfinished = workflow.GetType().GetField("unfinished", Private);
                            unfinished.SetValue(workflow, FormatterServices.GetUninitializedObject(unfinished.FieldType));
                        } else Set(fixture.Service, value, true);
                        Throws<InvalidOperationException>(() => fixture.Service.RunMaintenanceMenuAsync(fixture.Device, t => { }, null, CancellationToken.None));
                        Equal(0, fixture.Http.Urls.Count); Equal(false, fixture.Service.HasActiveMaintenanceMenu);
                    }
                });
            }
            test("Menu SSH service holds shared busy lease until cancelled download has unwound", () => Async(async () => {
                using (var fixture = new SshFixture()) using (var cancel = new CancellationTokenSource()) {
                    var entered = Signal(); fixture.Http.Hook = token => Hold(token, entered);
                    var task = fixture.Service.RunMaintenanceMenuAsync(fixture.Device, t => { throw new Exception("must not bind"); }, null, cancel.Token);
                    await entered.Task; True(fixture.Service.HasActiveMaintenanceMenu); True((bool)Field(fixture.Service, "softwareBusy"));
                    AssertOtherOperationsBusy(fixture.Service, fixture.Device);
                    True(ReferenceEquals(fixture.Client, Field(fixture.Service, "client"))); Equal(0, fixture.Proxy.Disposals);
                    cancel.Cancel(); Equal(MaintenancePreparationStatus.Cancelled, (await task).Preparation.Status);
                    Equal(false, fixture.Service.HasActiveMaintenanceMenu); Equal(false, (bool)Field(fixture.Service, "softwareBusy")); True(fixture.Client.IsConnected); Equal(0, fixture.Proxy.Disposals);
                }
            }));
            test("Menu SSH service disposal cancels in-flight preparation and releases lease", () => Async(async () => {
                using (var fixture = new SshFixture()) {
                    var entered = Signal(); fixture.Http.Hook = token => Hold(token, entered);
                    var task = fixture.Service.RunMaintenanceMenuAsync(fixture.Device, t => { }, null, CancellationToken.None); await entered.Task;
                    fixture.Service.Dispose(); Equal(MaintenancePreparationStatus.Cancelled, (await task).Preparation.Status);
                    Equal(false, fixture.Service.HasActiveMaintenanceMenu); True(fixture.Proxy.Disposals > 0); Equal(false, fixture.Service.IsConnected);
                }
            }));
            test("Menu SSH SFTP factory reuses credentials and enforces matching trusted Host Key", () => {
                using (var fixture = new SshFixture()) {
                    var method = typeof(SshService).GetMethod("CreateTrustedTransfer", Private);
                    using (var transfer = (SftpClient)method.Invoke(fixture.Service, new object[] { fixture.Fingerprint })) {
                        Equal(fixture.Client.ConnectionInfo.Host, transfer.ConnectionInfo.Host); Equal(fixture.Client.ConnectionInfo.Username, transfer.ConnectionInfo.Username);
                        Equal("PasswordAuthenticationMethod", transfer.ConnectionInfo.AuthenticationMethods.Single().GetType().Name);
                        var handlers = (EventHandler<HostKeyEventArgs>)typeof(BaseClient).GetField("HostKeyReceived", Private).GetValue(transfer);
                        var match = KeyArgs(SshFixture.Key); handlers(transfer, match); True(match.CanTrust);
                        var changed = KeyArgs(new byte[] { 9, 8, 7 }); handlers(transfer, changed); Equal(false, changed.CanTrust);
                        True(fixture.Client.IsConnected); Equal(0, fixture.Proxy.Disposals);
                    }
                }
            });
        }
        private static void AssertOtherOperationsBusy(SshService ssh, RouterEnvironment device)
        {
            Throws<InvalidOperationException>(() => ssh.RunMaintenanceMenuAsync(device, t => { }, null, CancellationToken.None));
            Throws<InvalidOperationException>(() => ssh.ConnectAndDetectAsync("unused.invalid", "x", "x", null, CancellationToken.None));
            Throws<InvalidOperationException>(() => ssh.CheckSoftwareEnvironmentAsync(device, null, null, CancellationToken.None));
            Throws<InvalidOperationException>(() => ssh.InstallOneClickAsync(device, "unused", null, CancellationToken.None));
            Throws<InvalidOperationException>(() => ssh.InstallPluginAsync(device, "unused", null, null, null, null, CancellationToken.None));
        }

        internal sealed class Http : IThirdPartyHttpClient
        {
            public byte[] Menu = Encoding.UTF8.GetBytes("#!/bin/sh\r\n# inert original 中文😀\r\nexit 0\r\n");
            public byte[] License = Encoding.UTF8.GetBytes(ThirdPartyScriptCatalog.BundledLicense());
            public readonly List<string> Urls = new List<string>();
            public Func<CancellationToken, Task> Hook;
            public async Task<ThirdPartyHttpContent> GetAsync(string url, int maxBytes, CancellationToken token)
            {
                Urls.Add(url); if (Hook != null) await Hook(token); token.ThrowIfCancellationRequested();
                if (url == ComputerProxyPolicy.ApiUrl) throw new IOException("Menu must not request the commit API.");
                return new ThirdPartyHttpContent { StatusCode = 200, Bytes = url.EndsWith("/LICENSE", StringComparison.Ordinal) ? License : Menu };
            }
        }
        internal sealed class Remote : IMaintenanceMenuSession
        {
            public volatile bool HomeConnected = true;
            public bool IsConnected { get { return HomeConnected; } }
            public string BadHash, OpenFault, StateOverride, CleanFault, Input;
            public bool AutoFinish = true, FinishOnInput, HoldEof, DropStart, DropExit, DisconnectOnWrite, WriteFailure;
            public int ExitCode, Creates, Opens, PreviewCleanups, StateProbes, FinishedCleanups;
            public uint Columns, Rows, ResizedColumns, ResizedRows;
            public Action UploadHook;
            public Func<CancellationToken, Task> CleanHook;
            public readonly Dictionary<string, byte[]> Uploads = new Dictionary<string, byte[]>();
            public readonly HashSet<string> Hashes = new HashSet<string>();
            public readonly ConcurrentQueue<string> Trace = new ConcurrentQueue<string>();
            public Peer Peer; private SshShellTransport transport; private RemoteMaintenanceWorkspace workspace;
            private string wire; private bool exited;
            public string Sanitize(string text) { return text ?? ""; }
            public Task CreateWorkspaceAsync(RemoteMaintenanceWorkspace value, CancellationToken token)
            { token.ThrowIfCancellationRequested(); workspace = value; Creates++; Trace.Enqueue("create"); return Task.FromResult(0); }
            public Task UploadAsync(string local, RemoteMaintenanceWorkspace space, string name, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Trace.Enqueue("upload:" + name); Uploads[name] = File.ReadAllBytes(local); if (UploadHook != null) UploadHook(); return Task.FromResult(0); }
            public Task<string> ReadHashAsync(RemoteMaintenanceWorkspace space, string name, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Trace.Enqueue("hash:" + name); Hashes.Add(name); return Task.FromResult((name == BadHash ? new string('f', 64) : Hash(Uploads[name])) + "  " + space.FilePath(name) + "\n"); }
            public Task<bool> CleanupAsync(RemoteMaintenanceWorkspace space, CancellationToken token)
            { token.ThrowIfCancellationRequested(); PreviewCleanups++; Trace.Enqueue("preview-clean"); return Task.FromResult(HomeConnected); }
            public Task<IShellTransport> OpenTerminalAsync(uint columns, uint rows, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Equal(4, Uploads.Count); Equal(4, Hashes.Count); Equal(0, PreviewCleanups);
                Opens++; Trace.Enqueue("open"); Columns = columns; Rows = rows;
                if (OpenFault == "disconnect") { HomeConnected = false; throw new IOException("connection lost"); }
                if (OpenFault == "throw") throw new IOException("PTY refused");
                if (OpenFault == "null") return Task.FromResult<IShellTransport>(null);
                string run = Regex.Match(Encoding.UTF8.GetString(Uploads["helper-launcher.sh"]), "\nrun='([a-f0-9]{32})'\n").Groups[1].Value;
                Equal(32, run.Length); wire = "ISTORE_MENU_V1|" + workspace.OperationId.ToString("N") + "|" + run;
                Peer = new Peer(); transport = new SshShellTransport(Peer, (c, r) => { ResizedColumns = c; ResizedRows = r; }, () => HomeConnected);
                Peer.OnWrite = value => {
                    if (Peer.Writes == 1) {
                        Equal(MaintenanceMenuCommands.StartPayload(workspace), value);
                        if (WriteFailure) throw new IOException("write failed");
                        if (!DropStart) Peer.Emit(Marker("START", Identity));
                        Peer.Emit("中文😀 prompt> ");
                        if (DisconnectOnWrite) { HomeConnected = false; transport.ReportFailure(new IOException("network lost")); Peer.End(); }
                        else if (AutoFinish) Finish();
                    } else { Input += value; if (FinishOnInput) Finish(); }
                };
                return Task.FromResult<IShellTransport>(transport);
            }
            private string Marker(string kind, string value) { return "\u001b]777;" + wire + "|" + kind + "|" + value + "\a"; }
            public void Finish() { exited = true; if (!DropExit) Peer.Emit(Marker("EXIT", Identity + "|" + ExitCode)); if (!HoldEof) Peer.End(); }
            public async Task<ProbeResult> ExecuteMenuCommandAsync(string command, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); if (!HomeConnected) throw new IOException("SSH disconnected");
                True(command.Contains("wire='" + wire + "'")); Equal(1, Peer.Disposals); Equal(false, Peer.Reading);
                if (command.Contains("printf '%s|CLEANED")) {
                    FinishedCleanups++; Trace.Enqueue("clean"); if (CleanHook != null) await CleanHook(token);
                    if (CleanFault == "throw") throw new IOException("cleanup disconnected");
                    return new ProbeResult { ExitStatus = CleanFault == "failed" ? 79 : 0, Output = (CleanFault == "wrong-ack" ? wire.Replace(workspace.OperationId.ToString("N"), new string('a', 32)) : wire) + "|CLEANED\n" };
                }
                StateProbes++; Trace.Enqueue("state"); if (StateOverride == "throw") throw new IOException("probe disconnected");
                string state = StateOverride == "UNKNOWN" || StateOverride == "RUNNING" ? StateOverride : exited ? "EXITED" : "RUNNING";
                string value = wire + "|STATE|" + state + (state == "EXITED" ? "|" + Identity + "|" + ExitCode : state == "RUNNING" ? "|" + Identity : "") + "\n";
                if (StateOverride == "wrong-task") value = value.Replace(workspace.OperationId.ToString("N"), new string('a', 32));
                if (StateOverride == "wrong-run") value = value.Replace(wire.Split('|')[2], new string('b', 32));
                if (StateOverride == "wrong-pid") value = value.Replace("|123|", "|124|");
                if (StateOverride == "wrong-code") value = value.Replace("|" + ExitCode + "\n", "|19\n");
                if (StateOverride == "malformed") value = "malformed";
                return new ProbeResult { ExitStatus = 0, Output = value };
            }
        }
        internal sealed class Peer : Stream
        {
            private readonly BlockingCollection<byte[]> output = new BlockingCollection<byte[]>();
            private byte[] current; private int offset;
            public int Writes, Disposals; public volatile bool Reading; public Action<string> OnWrite;
            public void Emit(string text) { output.Add(Encoding.UTF8.GetBytes(text)); }
            public void End() { if (!output.IsAddingCompleted) output.CompleteAdding(); }
            public override int Read(byte[] buffer, int start, int length)
            {
                Reading = true;
                try {
                    if (current == null || offset == current.Length) {
                        try { current = output.Take(); offset = 0; } catch (InvalidOperationException) { return 0; }
                    }
                    int size = Math.Min(Math.Min(length, 17), current.Length - offset);
                    Buffer.BlockCopy(current, offset, buffer, start, size); offset += size; return size;
                } finally { Reading = false; }
            }
            public override void Write(byte[] bytes, int start, int length) { Interlocked.Increment(ref Writes); if (OnWrite != null) OnWrite(Encoding.UTF8.GetString(bytes, start, length)); }
            protected override void Dispose(bool disposing) { if (Interlocked.Increment(ref Disposals) == 1) End(); }
            public override void Flush() { }
            public override bool CanRead { get { return true; } } public override bool CanWrite { get { return true; } } public override bool CanSeek { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override long Seek(long value, SeekOrigin origin) { throw new NotSupportedException(); } public override void SetLength(long value) { throw new NotSupportedException(); }
        }
        // A remoting proxy supplies only the connection flag to SSH.NET. It cannot open any channel or socket.
        private sealed class SessionProxy : RealProxy
        {
            public int Disposals; private bool connected = true;
            public bool AllowChannel;
            public ChannelProxy Channel;
            public ConnectionInfo Info;
            public SessionProxy(Type type) : base(type) { }
            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message;
                if (call.MethodName == "get_IsConnected") return new ReturnMessage(connected, null, 0, call.LogicalCallContext, call);
                if (call.MethodName == "get_ConnectionInfo") return new ReturnMessage(Info, null, 0, call.LogicalCallContext, call);
                if (call.MethodName == "get_SessionLoggerFactory") return new ReturnMessage(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, null, 0, call.LogicalCallContext, call);
                if (call.MethodName == "CreateChannelSession" && AllowChannel) {
                    Channel = new ChannelProxy(((MethodInfo)call.MethodBase).ReturnType);
                    return new ReturnMessage(Channel.GetTransparentProxy(), null, 0, call.LogicalCallContext, call);
                }
                if (call.MethodName == "Dispose" || call.MethodName == "Disconnect") { Disposals++; connected = false; }
                else if (!call.MethodName.StartsWith("OnDisconnect", StringComparison.Ordinal) && !call.MethodName.StartsWith("remove_", StringComparison.Ordinal) && !call.MethodName.StartsWith("add_", StringComparison.Ordinal))
                    return new ReturnMessage(new InvalidOperationException("Offline proxy forbids " + call.MethodName), call);
                return new ReturnMessage(null, null, 0, call.LogicalCallContext, call);
            }
        }
        private sealed class ChannelProxy : RealProxy
        {
            public int Opens, Disposals;
            public readonly Dictionary<string, object[]> Requests = new Dictionary<string, object[]>();
            public ChannelProxy(Type type) : base(type) { }
            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message; object result = null;
                if (call.MethodName == "Open") Opens++;
                else if (call.MethodName == "Dispose") Disposals++;
                else if (call.MethodName.StartsWith("Send", StringComparison.Ordinal)) {
                    Requests[call.MethodName] = call.Args;
                    if (((MethodInfo)call.MethodBase).ReturnType == typeof(bool)) result = true;
                } else if (call.MethodName == "get_IsOpen") result = true;
                else if (!call.MethodName.StartsWith("add_", StringComparison.Ordinal) && !call.MethodName.StartsWith("remove_", StringComparison.Ordinal))
                    return new ReturnMessage(new InvalidOperationException("Offline channel forbids " + call.MethodName), call);
                return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
            }
        }
        private sealed class SshFixture : IDisposable
        {
            public static readonly byte[] Key = { 1, 2, 3, 4 };
            public readonly SshService Service = new SshService();
            public readonly SshClient Client = new SshClient("fixture.invalid", "fixture", "inert");
            public readonly Http Http = new Http(); public readonly SessionProxy Proxy;
            public readonly string Fingerprint = FingerprintOf(Key);
            public RouterEnvironment Device { get { return new RouterEnvironment { IsConnected = true, HostKeyFingerprint = Fingerprint }; } }
            public SshFixture()
            {
                var session = typeof(BaseClient).GetField("<Session>k__BackingField", Private); Proxy = new SessionProxy(session.FieldType) { Info = Client.ConnectionInfo }; session.SetValue(Client, Proxy.GetTransparentProxy());
                True(Client.IsConnected); Set(Service, "client", Client); Set(Service, "connectionFactory", new Func<PasswordConnectionInfo>(() => new PasswordConnectionInfo("fixture.invalid", 22, "fixture", "inert")));
                bool first; ((HostKeyTrustStore)Field(Service, "trustStore")).TryTrust("fixture.invalid", 22, Fingerprint, out first);
                Set(Service, "maintenancePreparation", MaintenanceMenuSshTests.Service(Http));
            }
            public void Dispose() { Service.Dispose(); }
        }
        private static HostKeyEventArgs KeyArgs(byte[] bytes)
        {
            var value = (HostKeyEventArgs)FormatterServices.GetUninitializedObject(typeof(HostKeyEventArgs));
            typeof(HostKeyEventArgs).GetField("<HostKey>k__BackingField", Private).SetValue(value, bytes); return value;
        }
        private static string FingerprintOf(byte[] bytes) { using (var hash = SHA256.Create()) return "SHA256:" + Convert.ToBase64String(hash.ComputeHash(bytes)).TrimEnd('='); }
        private static object Field(object instance, string name) { return instance.GetType().GetField(name, Private).GetValue(instance); }
        private static void Set(object instance, string name, object value) { instance.GetType().GetField(name, Private).SetValue(instance, value); }
        private static async Task Hold(CancellationToken token, TaskCompletionSource<int> entered)
        { var release = Signal(); using (token.Register(() => release.TrySetCanceled())) { entered.TrySetResult(0); await release.Task.ConfigureAwait(false); } }
        private static TaskCompletionSource<int> Signal() { return new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); }
        internal static PluginMaintenanceExecutionService Service(Http http) { return new PluginMaintenanceExecutionService(new ThirdPartyScriptSource(http)); }
        private static RouterEnvironment Device() { return new RouterEnvironment { IsConnected = true, HostKeyFingerprint = "SHA256:fixture" }; }
        private static void Finished(MaintenanceMenuRunResult result, Remote remote, int code)
        {
            if (result.Preparation.Status == MaintenancePreparationStatus.Failed)
                throw new Exception("Run failed at " + result.Preparation.Stage + ": " + result.Preparation.Error);
            Equal(code == 0 ? MaintenancePreparationStatus.Completed : MaintenancePreparationStatus.ScriptFailed, result.Preparation.Status);
            Equal(code == 0 ? MaintenanceMenuRunStatus.ExitedNormally : MaintenanceMenuRunStatus.ExitedWithError, result.Runtime.Status);
            Equal<int?>(code, result.Runtime.ExitCode); True(result.Preparation.ExecutionAttempted && result.Preparation.IntegrityVerified);
            True(result.Preparation.RemoteCleanupSucceeded && result.Preparation.LocalCleanupSucceeded); True(result.Runtime.CleanupCompleted);
            Equal(1, remote.FinishedCleanups); Equal(0, remote.PreviewCleanups); Equal(1, remote.Peer.Disposals);
        }
        private static void Unknown(MaintenanceMenuRunResult result, Remote remote)
        {
            Equal(MaintenancePreparationStatus.FinalStateUnknown, result.Preparation.Status); Equal(MaintenanceMenuRunStatus.Unknown, result.Runtime.Status);
            Equal<int?>(null, result.Runtime.ExitCode); Equal(false, result.Runtime.CanCleanup || result.Runtime.CleanupCompleted);
            Equal(0, remote.FinishedCleanups); Equal(0, remote.PreviewCleanups); Equal(false, result.Preparation.RemoteCleanupSucceeded);
            True(result.Preparation.LocalCleanupSucceeded); True(!string.IsNullOrEmpty(result.Preparation.CleanupWarning)); Equal(1, remote.Peer.Disposals);
        }
        private static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static void Async(Func<Task> action)
        {
            var task = Task.Run(action);
            if (Task.WhenAny(task, Task.Delay(15000)).GetAwaiter().GetResult() != task) throw new TimeoutException("Offline menu SSH test timed out.");
            try { task.GetAwaiter().GetResult(); } catch (Exception ex) { throw new Exception(ex.ToString()); }
        }
        private static async Task Until(Func<bool> condition) { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.ElapsedMilliseconds > 8000) throw new TimeoutException(); await Task.Delay(5).ConfigureAwait(false); } }
        private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
        private static void True(bool value) { Equal(true, value); }
        private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action().ConfigureAwait(false); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    }
}
