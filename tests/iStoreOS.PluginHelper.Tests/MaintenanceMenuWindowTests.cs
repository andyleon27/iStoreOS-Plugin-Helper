using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Renci.SshNet;
using iStoreOS.PluginHelper.Controls;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class MaintenanceMenuWindowTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static MaintenanceMenuAvailability Ready() { return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.Ready, "ready"); }
        private static MaintenanceMenuAvailability Offline() { return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.NotConnected, "请先在首页连接路由器。"); }
        public static void Run(Action<string, Action> test)
        {
            test("Menu window first PTY uses the completed visible terminal layout", () => {
                using (var f = new UiFixture(Running())) {
                    f.UseWorker = true; f.ShowRunning();
                    Equal(f.View.Columns, f.Remote.Columns); Equal(f.View.Rows, f.Remote.Rows);
                    var size = f.Form.GetTerminalSize(); Equal(f.View.Columns, size.Item1); Equal(f.View.Rows, size.Item2);
                    Equal(1, f.Remote.Opens); f.Remote.Finish(); f.Done();
                }
            });
            test("Menu window resize during download supplies the latest initial PTY size", () => {
                using (var f = new UiFixture(Running())) {
                    bool waiting = false; var release = Signal(); int downloads = 0;
                    f.Http.Hook = async token => {
                        if (Interlocked.Increment(ref downloads) == 1) { waiting = true; await release.Task.ConfigureAwait(false); }
                    };
                    f.UseWorker = true; f.Show(); PumpUntil(() => waiting); Equal(0, f.Remote.Opens);
                    f.Form.ClientSize = new Size(1120, 780); Pump(30); uint columns = f.View.Columns, rows = f.View.Rows;
                    release.TrySetResult(0); PumpUntil(() => f.Remote.Opens == 1 && f.Terminal != null && f.Terminal.IsInputReady);
                    Equal(columns, f.Remote.Columns); Equal(rows, f.Remote.Rows); Equal(1, f.Calls);
                    f.Remote.Finish(); f.Done();
                }
            });
            test("Menu window resize between PTY open and bind is retried after controller start", () => {
                using (var f = new UiFixture(Running())) {
                    f.UseWorker = true;
                    f.BeforeBind = () => f.Form.Invoke(new Action(() => f.Form.ClientSize = new Size(1120, 780)));
                    f.ShowRunning(); PumpUntil(() => f.Remote.ResizedColumns == f.View.Columns && f.Remote.ResizedRows == f.View.Rows);
                    True(f.Remote.Columns != f.Remote.ResizedColumns); Equal(1, f.Remote.Opens); Equal(1, f.Remote.Peer.Writes);
                    True(f.Remote.HomeConnected); f.Remote.Finish(); f.Done();
                }
            });
            test("Menu window constructor is inert and shown starts exactly one menu", () => {
                using (var f = new UiFixture()) {
                    Equal(0, f.Calls); Equal(0, f.Http.Urls.Count); f.Show(); f.Done(); Equal(1, f.Calls); Equal(2, f.Http.Urls.Count);
                    True(f.Form.Visible); f.Form.Hide(); f.Form.Show(); Pump(30); Equal(1, f.Calls); Equal(1, f.Remote.Opens);
                }
            });
            test("Menu window has terminal source connection status and return control", () => {
                using (var f = new UiFixture()) {
                    f.Show(); f.Done(); True(f.View.ReadOnly); Equal(false, f.View.WordWrap); True(f.View.Visible);
                    Equal("SSH 已连接", f.Find("maintenanceTerminalConnection").Text); Equal("返回首页", f.Return.Text);
                    string text = string.Join("\n", Descendants(f.Form).OfType<Label>().Select(c => c.Text));
                    True(text.Contains("slobys/openclash-auto-installer")); True(text.Contains("MIT License")); True(text.Contains(ThirdPartyScriptCatalog.Copyright));
                    Equal(0, Descendants(f.Form).Count(c => c.Name.StartsWith("maintenanceAction", StringComparison.Ordinal)));
                }
            });
            test("Menu window live output renders UTF8 and hides protocol before displaying normal exit", () => {
                using (var f = new UiFixture()) {
                    f.Show(); f.Done(); Equal("菜单已正常退出", f.State.Text); True(f.View.Text.Contains("中文😀 prompt> "));
                    Equal(false, f.View.Text.Contains("ISTORE_MENU_V1")); Equal(0, f.View.PendingChunkCount);
                    Equal(f.View.ReceivedCharacterCount, f.View.DisplayedCharacterCount); True(f.Remote.HomeConnected);
                }
            });
            foreach (int code in new[] { 7, 130 })
            {
                int value = code;
                test("Menu window nonzero menu result is visible " + code, () => {
                    using (var f = new UiFixture(new MaintenanceMenuSshTests.Remote { ExitCode = value })) {
                        f.Show(); f.Done(); True(f.State.Text.Contains("异常退出")); True(f.State.Text.Contains(value.ToString()));
                        Equal(MaintenancePreparationStatus.ScriptFailed, f.Form.Result.Preparation.Status);
                    }
                });
            }
            test("Menu window unknown result displays retained remote path", () => {
                using (var f = new UiFixture(new MaintenanceMenuSshTests.Remote { DropExit = true, StateOverride = "UNKNOWN" })) {
                    f.Show(); f.Done(); Equal("菜单最终状态未知", f.State.Text);
                    True(f.Detail.Text.Contains(f.Form.Result.Preparation.RemoteDirectory)); Equal(0, f.Remote.FinishedCleanups);
                }
            });
            test("Menu window cleanup warning does not become a false script error", () => {
                using (var f = new UiFixture(new MaintenanceMenuSshTests.Remote { CleanFault = "failed" })) {
                    f.Show(); f.Done(); Equal("菜单已正常退出", f.State.Text); True(f.Detail.Text.Contains("清理"));
                    Equal(false, f.Form.Result.Preparation.RemoteCleanupSucceeded);
                }
            });
            test("Menu window unavailable at shown never invokes menu runner", () => {
                using (var f = new UiFixture(null, () => Offline())) {
                    f.Show(); f.Done(); Equal(0, f.Calls); Equal(0, f.Http.Urls.Count); Equal("菜单暂不可用", f.State.Text);
                    True(f.Detail.Text.Contains("请先")); f.Return.PerformClick(); PumpUntil(() => !f.Form.Visible);
                }
            });
            test("Menu window reads latest availability at shown rather than construction", () => {
                bool allowed = true;
                using (var f = new UiFixture(null, () => allowed ? Ready() : Offline())) {
                    allowed = false; f.Show(); f.Done(); Equal(0, f.Calls); Equal(0, f.Http.Urls.Count);
                }
            });
            test("Menu window unavailable identity shows reason with disconnected status", () => {
                using (var f = new UiFixture(new MaintenanceMenuSshTests.Remote { HomeConnected = false }, () => Offline())) {
                    f.Show(); f.Done(); Equal("SSH 已断开", f.Find("maintenanceTerminalConnection").Text); Equal(0, f.Calls);
                }
            });
            test("Menu window startup exception remains reviewable without retry", () => {
                int calls = 0;
                using (var form = new PluginMaintenanceTerminalForm((r, p, t, c, h) => { calls++; throw new IOException("fixture error"); }, Ready, () => true, null)) {
                    Show(form); PumpUntil(() => form.Completion.IsCompleted); Equal(1, calls);
                    Equal("菜单会话未能完成", Find(form, "maintenanceTerminalState").Text); True(form.Visible); True(Find(form, "maintenanceTerminalReturn").Enabled);
                    form.Close();
                }
            });
            test("Menu window null result is unknown without false success", () => {
                using (var form = new PluginMaintenanceTerminalForm((r, p, t, c, h) => Task.FromResult<MaintenanceMenuRunResult>(null), Ready, () => true, null)) {
                    Show(form); PumpUntil(() => form.Completion.IsCompleted); Equal("菜单状态无法确认", Find(form, "maintenanceTerminalState").Text); form.Close();
                }
            });
            test("Menu window preparation failure shows error and does not open PTY", () => {
                using (var f = new UiFixture()) {
                    f.Http.Menu = new byte[] { 1, 2, 3 }; f.Show(); f.Done(); Equal("菜单未启动", f.State.Text);
                    True(!string.IsNullOrEmpty(f.Detail.Text)); Equal(0, f.Remote.Opens); True(f.Remote.HomeConnected);
                }
            });
            test("Menu window return during download cancels and waits for preparation unwind", () => {
                using (var f = new UiFixture()) {
                    bool entered = false; f.Http.Hook = token => Hold(token, () => entered = true);
                    f.Show(); PumpUntil(() => entered); True(f.State.Text.Contains("正在")); f.Return.PerformClick();
                    Equal(false, f.Return.Enabled); PumpUntil(() => !f.Form.Visible); True(f.Form.Completion.IsCompleted);
                    Equal(MaintenancePreparationStatus.Cancelled, f.Form.Result.Preparation.Status); Equal(0, f.Remote.Opens); True(f.Remote.HomeConnected);
                }
            });
            test("Menu window return while running closes only terminal and awaits final status", () => {
                using (var f = new UiFixture(Running())) {
                    f.ShowRunning(); f.Return.PerformClick(); PumpUntil(() => !f.Form.Visible);
                    Equal(MaintenancePreparationStatus.FinalStateUnknown, f.Form.Result.Preparation.Status);
                    Equal(1, f.Remote.Peer.Disposals); True(f.Remote.HomeConnected); True(f.Form.Completion.IsCompleted);
                }
            });
            test("Menu window title bar close uses the same safe close path", () => {
                using (var f = new UiFixture(Running())) {
                    f.ShowRunning(); f.Form.Close(); PumpUntil(() => !f.Form.Visible);
                    Equal(1, f.Remote.Peer.Disposals); True(f.Form.Completion.IsCompleted); True(f.Remote.HomeConnected);
                }
            });
            test("Menu window repeated close cannot dispatch another menu or dispose channel twice", () => {
                using (var f = new UiFixture(Running())) {
                    f.ShowRunning(); f.Return.PerformClick(); f.Form.Close(); f.Form.Close(); PumpUntil(() => !f.Form.Visible);
                    Equal(1, f.Calls); Equal(1, f.Remote.Opens); Equal(1, f.Remote.Peer.Disposals); True(f.Remote.HomeConnected);
                }
            });
            test("Menu window closing waits for cleanup before actually returning", () => {
                using (var f = new UiFixture()) {
                    var entered = Signal(); var release = Signal(); f.Remote.CleanHook = async token => { entered.SetResult(0); await release.Task; };
                    f.Show(); PumpUntil(() => entered.Task.IsCompleted); f.Return.PerformClick(); Pump(40); True(f.Form.Visible);
                    release.SetResult(0); PumpUntil(() => !f.Form.Visible); True(f.Form.Completion.IsCompleted); True(f.Remote.HomeConnected);
                }
            });
            test("Menu window tail output is drawn before return closes window", () => {
                using (var f = new UiFixture(new MaintenanceMenuSshTests.Remote { HoldEof = true })) {
                    f.Show(); PumpUntil(() => f.Terminal != null && f.Terminal.Snapshot.Status == MaintenanceMenuRunStatus.AwaitingClose);
                    string tail = "\r\n最终输出😀" + new string('x', 70000); f.Remote.Peer.Emit(tail); f.Remote.Peer.End(); f.Done();
                    True(f.View.Text.Contains("最终输出😀")); Equal(f.View.ReceivedCharacterCount, f.View.DisplayedCharacterCount); Equal(0, f.View.PendingChunkCount);
                    f.Return.PerformClick(); PumpUntil(() => !f.Form.Visible); True(f.Remote.HomeConnected);
                }
            });
            test("Menu window real terminal keyboard reaches menu controller", () => {
                using (var f = new UiFixture(Running())) {
                    f.ShowRunning();
                    typeof(TerminalView).GetMethod("OnKeyPress", Private).Invoke(f.View, new object[] { new KeyPressEventArgs('1') });
                    foreach (Keys key in new[] { Keys.Enter, Keys.Control | Keys.C })
                        typeof(TerminalView).GetMethod("ProcessCmdKey", Private).Invoke(f.View, new object[] { new Message(), key });
                    PumpUntil(() => f.Remote.Input == "1\n\u0003"); f.Remote.Finish(); f.Done(); Equal("菜单已正常退出", f.State.Text);
                }
            });
            test("Menu window resize changes remote PTY without restarting menu", () => {
                using (var f = new UiFixture(Running())) {
                    f.ShowRunning(); f.Form.ClientSize = new Size(1040, 720); PumpUntil(() => f.Remote.ResizedColumns > 0);
                    Equal(f.View.Columns, f.Remote.ResizedColumns); Equal(f.View.Rows, f.Remote.ResizedRows); Equal(1, f.Calls);
                    f.Remote.Finish(); f.Done();
                }
            });
            test("Menu window connection loss updates connection and unknown result", () => {
                using (var f = new UiFixture(Running())) {
                    f.ShowRunning(); f.Remote.HomeConnected = false; f.Remote.Peer.End(); f.Done(); Pump(280);
                    Equal("SSH 已断开", f.Find("maintenanceTerminalConnection").Text); Equal("菜单最终状态未知", f.State.Text);
                    Equal(0, f.Remote.FinishedCleanups); Equal(1, f.Calls);
                }
            });
            test("Menu window owner cancellation closes its child menu and not home SSH", () => {
                using (var owner = new CancellationTokenSource()) using (var f = new UiFixture(Running(), null, owner.Token)) {
                    f.ShowRunning(); owner.Cancel(); PumpUntil(() => !f.Form.Visible); Equal(1, f.Remote.Peer.Disposals); True(f.Remote.HomeConnected);
                }
            });
            test("Menu window pre-cancelled owner cannot start menu", () => {
                using (var owner = new CancellationTokenSource()) {
                    owner.Cancel(); using (var f = new UiFixture(null, null, owner.Token)) {
                        f.Form.Show(); PumpUntil(() => !f.Form.Visible); Equal(0, f.Calls); Equal(0, f.Http.Urls.Count);
                    }
                }
            });
            test("Menu window direct disposal releases blocked reader and output subscriptions", () => {
                using (var f = new UiFixture(Running())) {
                    f.ShowRunning(); f.Form.Dispose(); PumpUntil(() => f.Form.Completion.IsCompleted);
                    Equal(1, f.Remote.Peer.Disposals); True(f.Remote.HomeConnected);
                }
            });
            test("Menu window close from shown prevents any startup", () => {
                using (var f = new UiFixture()) {
                    f.Form.Shown += (s, e) => f.Form.Close(); f.Form.Show(); PumpUntil(() => !f.Form.Visible);
                    Equal(0, f.Calls); Equal(0, f.Http.Urls.Count);
                }
            });
            test("Menu window queued worker binding after close is rejected before command dispatch", () => {
                using (var f = new UiFixture(Running())) using (var release = new ManualResetEventSlim()) {
                    bool waiting = false; f.BeforeBind = () => { waiting = true; release.Wait(8000); };
                    f.UseWorker = true; f.Show(); PumpUntil(() => waiting); f.Return.PerformClick(); release.Set();
                    PumpUntil(() => !f.Form.Visible); Equal(false, f.Form.Result.Preparation.ExecutionAttempted);
                    Equal(0, f.Remote.Peer.Writes); Equal(1, f.Remote.Peer.Disposals); True(f.Remote.HomeConnected);
                }
            });
            test("Menu window default and minimum layouts keep terminal and return visible", () => {
                using (var f = new UiFixture(Running())) {
                    f.ShowRunning(); CheckLayout(f); Capture(f.Form, "menu-window-running.png");
                    f.Form.Size = f.Form.MinimumSize; Pump(80); CheckLayout(f); Capture(f.Form, "menu-window-minimum.png");
                    f.Remote.Finish(); f.Done();
                }
            });
            RunAvailability(test);
            RunHomeEntry(test);
        }

        private static void RunAvailability(Action<string, Action> test)
        {
            test("Menu availability without connection and after disposal is local and safe", () => {
                using (var ssh = new SshService()) {
                    Equal(MaintenanceMenuAvailabilityStatus.NotConnected, ssh.GetMaintenanceMenuAvailability(null).Status);
                    ssh.Dispose(); Equal(MaintenanceMenuAvailabilityStatus.Closed, ssh.GetMaintenanceMenuAvailability(null).Status);
                }
            });
            test("Menu availability ready snapshots do not contact source or SSH", () => {
                using (var f = new HomeFixture()) {
                    for (int i = 0; i < 100; i++) True(f.Service.GetMaintenanceMenuAvailability(f.Device).CanOpen);
                    Equal(0, f.Http.Urls.Count); Equal(0, f.Proxy.OtherCalls); Equal(0, f.Proxy.Disposals);
                }
            });
            foreach (string state in new[] { "connecting", "softwareBusy", "pending-installation", "missing-auth", "identity", "device-offline", "ssh-offline" })
            {
                string item = state;
                test("Menu availability reflects current gate " + state, () => {
                    using (var f = new HomeFixture()) {
                        f.Change(item); Equal(false, f.Service.GetMaintenanceMenuAvailability(f.Device).CanOpen); Equal(0, f.Http.Urls.Count);
                    }
                });
            }
            test("Menu availability snapshot cannot bypass atomic service start gate", () => {
                using (var f = new HomeFixture()) {
                    True(f.Service.GetMaintenanceMenuAvailability(f.Device).CanOpen); Set(f.Service, "softwareBusy", true);
                    Throws<InvalidOperationException>(() => f.Service.RunMaintenanceMenuAsync(f.Device, t => { }, null, CancellationToken.None)); Equal(0, f.Http.Urls.Count);
                }
            });
        }
        private static void RunHomeEntry(Action<string, Action> test)
        {
            test("Menu HTTP 403 diagnostic survives terminal return into saved homepage log", () => {
                using (var f = new HomeFixture()) {
                    f.Http.Hook = token => { throw MaintenanceHttpDiagnosticsTests.Forbidden(); }; f.Show();
                    Exception failure = null;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 20 }) {
                        var watch = Stopwatch.StartNew();
                        timer.Tick += (s, e) => {
                            try {
                                var child = Application.OpenForms.OfType<PluginMaintenanceTerminalForm>().FirstOrDefault(w => w.Owner == f.Main);
                                if (watch.ElapsedMilliseconds > 8000) throw new TimeoutException("403 diagnostic window did not finish.");
                                if (child == null || !child.Completion.IsCompleted) return;
                                True(Find(child, "maintenanceTerminalDetail").Text.Contains("HTTP=403"));
                                True(Find(child, "maintenanceTerminalDetail").Text.Contains("阶段=Download/DownloadMenu"));
                                ((Button)Find(child, "maintenanceTerminalReturn")).PerformClick();
                            } catch (Exception ex) {
                                failure = ex; foreach (var child in Application.OpenForms.Cast<Form>().Where(w => w.Owner == f.Main).ToArray()) child.Dispose();
                            }
                        };
                        timer.Start(); Click(f.Main); timer.Stop();
                    }
                    if (failure != null) throw failure;
                    string log = ((RichTextBox)Field(f.Main, "logBox")).Text;
                    True(log.Contains("插件维护：Failed")); True(log.Contains("阶段=Download/DownloadMenu"));
                    True(log.Contains("域名=raw.githubusercontent.com")); True(log.Contains("URL=" + ThirdPartyScriptCatalog.MainMenuUrl)); True(log.Contains("HTTP=403"));
                    Equal(false, log.Contains("diagnostic-secret")); Equal(false, log.Contains("Bearer"));
                    Equal(1, f.Http.Urls.Count); True(f.Client.IsConnected); Equal(0, f.Proxy.Disposals);
                }
            });
            test("Menu homepage entry starts disabled and retains original geometry", () => {
                using (var main = new MainForm()) {
                    main.Show(); Application.DoEvents(); var entry = Find(main, "pluginMaintenanceLink");
                    Equal(false, entry.Enabled); True(entry.Visible); Equal(new Size(760, 500), main.ClientSize);
                    Equal("插件维护 ›", entry.Text); main.Close();
                }
            });
            test("Menu homepage entry enables after trusted connection and disables on loss", () => {
                using (var f = new HomeFixture()) {
                    f.Show(); True(f.Entry.Enabled); f.Proxy.Connected = false; Tick(f.Main); Equal(false, f.Entry.Enabled);
                    True(f.Device.IsConnected); True(f.Service.CanReconnect); Equal(0, f.Http.Urls.Count);
                    f.Proxy.Connected = true; Tick(f.Main); True(f.Entry.Enabled);
                }
            });
            test("Menu homepage timer refreshes service busy state without network activity", () => {
                using (var f = new HomeFixture()) {
                    f.Show(); True(f.Entry.Enabled); Set(f.Service, "softwareBusy", true); PumpUntil(() => !f.Entry.Enabled);
                    Set(f.Service, "softwareBusy", false); PumpUntil(() => f.Entry.Enabled); Equal(0, f.Http.Urls.Count); Equal(0, f.Proxy.OtherCalls);
                }
            });
            foreach (string flag in new[] { "isConnecting", "isSoftwareBusy", "isInstallBusy" })
            {
                string name = flag;
                test("Menu homepage entry immediately follows UI busy state " + flag, () => {
                    using (var f = new HomeFixture()) {
                        f.Show(); True(f.Entry.Enabled); Set(f.Main, name, true); Invoke(f.Main, "RefreshInstallButton"); Equal(false, f.Entry.Enabled);
                        Set(f.Main, name, false); Invoke(f.Main, "RefreshInstallButton"); True(f.Entry.Enabled);
                    }
                });
            }
            foreach (string gate in new[] { "softwareBusy", "pending-installation", "identity", "missing-auth", "ssh-offline" })
            {
                string item = gate;
                test("Menu homepage stale enabled click is rejected before opening " + gate, () => {
                    using (var f = new HomeFixture()) {
                        f.Show(); True(f.Entry.Enabled); f.Change(item); Click(f.Main);
                        Equal(false, f.Entry.Enabled); Equal(0, OwnedMenus(f.Main)); Equal(0, f.Http.Urls.Count);
                    }
                });
            }
            foreach (bool titleClose in new[] { false, true }) {
                bool closeByTitle = titleClose;
                test("Menu homepage opens one modal terminal and preserves SSH on " + (titleClose ? "title close" : "return"), () => {
                using (var f = new HomeFixture()) {
                    bool download = false; f.Http.Hook = token => Hold(token, () => download = true); f.Show();
                    int shown = 0; Exception failure = null; bool returning = false;
                    using (var timer = new System.Windows.Forms.Timer { Interval = 20 }) {
                        var watch = Stopwatch.StartNew();
                        timer.Tick += (s, e) => {
                            try {
                                var child = Application.OpenForms.OfType<PluginMaintenanceTerminalForm>().FirstOrDefault(w => w.Owner == f.Main);
                                if (child == null) { if (watch.ElapsedMilliseconds > 8000) throw new TimeoutException("New menu window did not open."); return; }
                                if (returning || !download) return;
                                shown++; True(child.Controls.Find("maintenanceTerminalView", true).Single() is TerminalView);
                                Equal(false, IsWindowEnabled(f.Main.Handle)); Click(f.Main); Equal(1, OwnedMenus(f.Main)); Equal(1, f.Http.Urls.Count);
                                returning = true;
                                if (closeByTitle) child.Close(); else ((Button)Find(child, "maintenanceTerminalReturn")).PerformClick();
                            } catch (Exception ex) {
                                failure = ex; foreach (var window in Application.OpenForms.Cast<Form>().Where(w => w.Owner == f.Main).ToArray()) window.Dispose();
                            }
                        };
                        timer.Start(); Click(f.Main); timer.Stop();
                    }
                    if (failure != null) throw failure;
                    Equal(1, shown); Equal(true, f.Main.Enabled); True(f.Entry.Enabled); True(f.Client.IsConnected); Equal(false, f.Service.HasActiveMaintenanceMenu);
                    Equal(0, f.Proxy.Disposals); True(((RichTextBox)Field(f.Main, "logBox")).Text.Contains("插件维护：Cancelled"));
                }
            });
            }
        }

        private sealed class UiFixture : IDisposable
        {
            public readonly MaintenanceMenuSshTests.Http Http = new MaintenanceMenuSshTests.Http();
            public readonly MaintenanceMenuSshTests.Remote Remote;
            public readonly PluginMaintenanceTerminalForm Form;
            public MaintenanceMenuTerminal Terminal; public int Calls; public bool UseWorker; public Action BeforeBind;
            public UiFixture(MaintenanceMenuSshTests.Remote remote = null, Func<MaintenanceMenuAvailability> check = null, CancellationToken owner = default(CancellationToken))
            {
                Remote = remote ?? new MaintenanceMenuSshTests.Remote(); var service = MaintenanceMenuSshTests.Service(Http);
                MaintenanceMenuRunner runner = (ready, progress, token, columns, rows) => {
                    Calls++;
                    Func<Task<MaintenanceMenuRunResult>> action = () => service.RunMenuAsync(Remote, value => {
                        Terminal = value; if (BeforeBind != null) BeforeBind(); ready(value);
                    }, progress, token, null, columns, rows, () => Form.GetTerminalSize());
                    return UseWorker ? Task.Run(action) : action();
                };
                Form = new PluginMaintenanceTerminalForm(runner, check ?? Ready, () => Remote.HomeConnected, null, owner);
            }
            public TerminalView View { get { return (TerminalView)Find("maintenanceTerminalView"); } }
            public Button Return { get { return (Button)Find("maintenanceTerminalReturn"); } }
            public Control State { get { return Find("maintenanceTerminalState"); } }
            public Control Detail { get { return Find("maintenanceTerminalDetail"); } }
            public Control Find(string name) { return MaintenanceMenuWindowTests.Find(Form, name); }
            public void Show() { MaintenanceMenuWindowTests.Show(Form); }
            public void Done() { PumpUntil(() => Form.Completion.IsCompleted); Form.Completion.GetAwaiter().GetResult(); }
            public void ShowRunning() { Show(); PumpUntil(() => Terminal != null && Terminal.IsInputReady && View.Text.Contains("prompt> ")); Pump(280); True(State.Text.Contains("运行中")); }
            public void Dispose() { Form.Dispose(); PumpUntil(() => Form.Completion.IsCompleted); }
        }
        private sealed class HomeFixture : IDisposable
        {
            public readonly MainForm Main = new MainForm();
            public readonly SshService Service;
            public readonly SshClient Client = new SshClient("fixture.invalid", "fixture", "inert");
            public readonly SessionProxy Proxy;
            public readonly MaintenanceMenuSshTests.Http Http = new MaintenanceMenuSshTests.Http();
            public readonly RouterEnvironment Device;
            public LinkLabel Entry { get { return (LinkLabel)Find(Main, "pluginMaintenanceLink"); } }
            public HomeFixture()
            {
                Service = (SshService)Field(Main, "ssh");
                var session = typeof(BaseClient).GetField("<Session>k__BackingField", Private); Proxy = new SessionProxy(session.FieldType); session.SetValue(Client, Proxy.GetTransparentProxy());
                Set(Service, "client", Client); Set(Service, "connectionFactory", new Func<PasswordConnectionInfo>(() => new PasswordConnectionInfo("fixture.invalid", 22, "fixture", "inert")));
                string fingerprint; using (var hash = SHA256.Create()) fingerprint = "SHA256:" + Convert.ToBase64String(hash.ComputeHash(new byte[] { 1, 2, 3 })).TrimEnd('=');
                bool first; ((HostKeyTrustStore)Field(Service, "trustStore")).TryTrust("fixture.invalid", 22, fingerprint, out first);
                Device = new RouterEnvironment { IsConnected = true, HostKeyFingerprint = fingerprint, PackageManager = PackageManagerKind.APK };
                Set(Main, "router", Device); Set(Service, "maintenancePreparation", MaintenanceMenuSshTests.Service(Http));
                MaintenanceLaunchTests.SeedDirect(Service, Client);
            }
            public void Change(string state)
            {
                switch (state) {
                    case "pending-installation": var workflow = Field(Service, "installWorkflow"); var pending = workflow.GetType().GetField("unfinished", Private); pending.SetValue(workflow, FormatterServices.GetUninitializedObject(pending.FieldType)); break;
                    case "missing-auth": Set(Service, "connectionFactory", null); break;
                    case "identity": Device.HostKeyFingerprint = "SHA256:wrong"; break;
                    case "device-offline": Device.IsConnected = false; break;
                    case "ssh-offline": Proxy.Connected = false; break;
                    default: Set(Service, state, true); break;
                }
            }
            public void Show() { Main.Show(); Application.DoEvents(); Invoke(Main, "RefreshMaintenanceEntry"); }
            public void Dispose() { Set(Main, "isConnecting", false); Set(Main, "isSoftwareBusy", false); Set(Main, "isInstallBusy", false); Main.Close(); Main.Dispose(); }
        }
        private sealed class SessionProxy : RealProxy
        {
            public bool Connected = true; public int Disposals, OtherCalls;
            public SessionProxy(Type type) : base(type) { }
            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message;
                if (call.MethodName == "get_IsConnected") return new ReturnMessage(Connected, null, 0, call.LogicalCallContext, call);
                if (call.MethodName == "Dispose" || call.MethodName == "Disconnect") { Disposals++; Connected = false; }
                else if (!call.MethodName.StartsWith("OnDisconnect", StringComparison.Ordinal) && !call.MethodName.StartsWith("add_", StringComparison.Ordinal) && !call.MethodName.StartsWith("remove_", StringComparison.Ordinal)) {
                    OtherCalls++; return new ReturnMessage(new InvalidOperationException("No network permitted in UI fixture: " + call.MethodName), call);
                }
                return new ReturnMessage(null, null, 0, call.LogicalCallContext, call);
            }
        }
        private static void CheckLayout(UiFixture f)
        {
            var view = f.View.RectangleToScreen(f.View.ClientRectangle); var footer = f.Return.RectangleToScreen(f.Return.ClientRectangle);
            var area = f.Form.RectangleToScreen(f.Form.ClientRectangle);
            True(view.Width > 650 && view.Height > 180); True(area.Contains(view)); True(area.Contains(footer)); True(view.Bottom <= footer.Top);
        }
        private static void Capture(Form form, string file)
        {
            string root = Repository(); string directory = Path.Combine(root, "artifacts", "menu-window-phase5"); Directory.CreateDirectory(directory);
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(directory, file)); }
        }
        private static string Repository() { for (var root = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); root != null; root = root.Parent) if (File.Exists(Path.Combine(root.FullName, "iStoreOS.PluginHelper.sln"))) return root.FullName; throw new DirectoryNotFoundException(); }
        private static MaintenanceMenuSshTests.Remote Running() { return new MaintenanceMenuSshTests.Remote { AutoFinish = false }; }
        private static void Show(PluginMaintenanceTerminalForm form) { form.Show(); PumpUntil(() => (bool)Field(form, "started")); }
        private static void Tick(MainForm main) { Invoke(main, "RefreshMaintenanceEntry"); }
        private static int OwnedMenus(MainForm main) { return Application.OpenForms.OfType<PluginMaintenanceTerminalForm>().Count(f => f.Owner == main); }
        private static void Click(MainForm main) { var link = (LinkLabel)Find(main, "pluginMaintenanceLink"); typeof(LinkLabel).GetMethod("OnLinkClicked", Private).Invoke(link, new object[] { new LinkLabelLinkClickedEventArgs(link.Links[0]) }); }
        private static Control Find(Control root, string name) { return root.Controls.Find(name, true).Single(); }
        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root) { foreach (Control control in root.Controls) { yield return control; foreach (Control child in Descendants(control)) yield return child; } }
        private static object Field(object value, string name) { return value.GetType().GetField(name, Private).GetValue(value); }
        private static void Set(object value, string name, object data) { value.GetType().GetField(name, Private).SetValue(value, data); }
        private static object Invoke(object value, string name) { return value.GetType().GetMethod(name, Private).Invoke(value, null); }
        private static TaskCompletionSource<int> Signal() { return new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); }
        private static async Task Hold(CancellationToken token, Action entered) { var release = Signal(); using (token.Register(() => release.TrySetCanceled())) { entered(); await release.Task.ConfigureAwait(false); } }
        private static void Pump(int ms) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); } }
        private static void PumpUntil(Func<bool> condition) { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.ElapsedMilliseconds > 12000) throw new TimeoutException("Menu window checkpoint timed out."); Application.DoEvents(); Thread.Sleep(5); } Application.DoEvents(); }
        private static void True(bool value) { Equal(true, value); }
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr handle);
        private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
        private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    }
}
