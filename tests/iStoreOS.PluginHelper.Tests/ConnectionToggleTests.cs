using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Renci.SshNet;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class ConnectionToggleTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void Run(Action<string, Action> test)
        {
            var errors = new List<Exception>();
            ThreadExceptionEventHandler handler = (sender, args) => errors.Add(args.Exception);
            bool check = System.Windows.Forms.Control.CheckForIllegalCrossThreadCalls;
            System.Windows.Forms.Control.CheckForIllegalCrossThreadCalls = true;
            Application.ThreadException += handler;
            try { RunCases((name, action) => test(name, () => {
                errors.Clear(); action();
                if (errors.Count != 0) throw new Exception("Unexpected UI exception: " + errors[0].GetType().Name);
            })); }
            finally { Application.ThreadException -= handler; System.Windows.Forms.Control.CheckForIllegalCrossThreadCalls = check; }
        }
        private static void RunCases(Action<string, Action> test)
        {
            test("Connection toggle starts as connect without a live session", () => {
                using (var form = new MainForm()) {
                    form.Show(); Pump(20); Equal("连接", Control(form, "connectButton").Text); True(Control(form, "connectButton").Enabled);
                    Equal(false, Control(form, "pluginMaintenanceLink").Enabled); form.Close();
                }
            });
            test("Connection toggle successful connection displays disconnect", () => {
                using (var f = new Fixture()) {
                    Invoke(f.Main, "DisplayConnectionOutcome", new ConnectionResult { Success = true, Environment = f.Device });
                    Equal("断开", f.Button.Text); True(f.Button.Enabled); True(f.Service.IsConnected); True(Control(f.Main, "pluginMaintenanceLink").Enabled);
                }
            });
            test("Connection toggle click disconnects before address username or password validation", () => {
                using (var f = new Fixture()) {
                    Control(f.Main, "hostBox").Text = "invalid/host"; Control(f.Main, "usernameBox").Text = ""; Control(f.Main, "passwordBox").Text = "";
                    f.Button.PerformClick(); f.Disconnected(); Equal(1, f.Proxy.Disposals); Equal("连接", f.Button.Text);
                    True(Control(f.Main, "connectionLabel").Text.Contains("已断开")); Equal(0, f.Proxy.Commands);
                }
            });
            test("Connection toggle clears device compatibility software state and maintenance authorization", () => {
                using (var f = new Fixture()) {
                    var plugin = new PluginPackageAnalyzer().AnalyzeFileName("OpenClash_0.47.156+aarch64_core_sdk_24.10.run");
                    Set(f.Main, "plugin", plugin); Invoke(f.Main, "RefreshCompatibility"); Equal(CompatibilityStatus.Compatible, plugin.CompatibilityStatus);
                    Control(f.Main, "fileNameBox").Text = plugin.FileName;
                    Control(f.Main, "currentSourceLabel").Text = "old source"; Control(f.Main, "packageDiffBox").Text = "old device packages";
                    Control(f.Main, "passwordBox").Text = "disconnect-secret"; Invoke(f.Main, "SetPasswordVisibility", true);
                    Invoke(f.Main, "AppendLog", "history survives manual disconnect");
                    MaintenanceLaunchTests.SeedDirect(f.Service, f.Client);
                    f.Button.PerformClick(); f.Disconnected();
                    Equal<object>(null, Field(f.Main, "router")); Equal(false, f.Device.IsConnected);
                    Equal("—", Control(f.Main, "versionLabel").Text); Equal("—", Control(f.Main, "sdkLabel").Text);
                    Equal("—", Control(f.Main, "architectureLabel").Text); Equal("—", Control(f.Main, "managerLabel").Text);
                    Equal("QuickStart：—", Control(f.Main, "quickStartLabel").Text); Equal("—", Control(f.Main, "currentSourceLabel").Text);
                    Equal(CompatibilityStatus.Unknown, plugin.CompatibilityStatus); True(ReferenceEquals(plugin, Field(f.Main, "plugin")));
                    Equal(plugin.FileName, Control(f.Main, "fileNameBox").Text); Equal(false, Control(f.Main, "installButton").Enabled);
                    Equal(false, Control(f.Main, "softwareButton").Enabled); Equal(false, Control(f.Main, "pluginMaintenanceLink").Enabled);
                    Equal("", Control(f.Main, "passwordBox").Text); Equal('●', ((TextBox)Control(f.Main, "passwordBox")).PasswordChar);
                    True(Control(f.Main, "logBox").Text.Contains("history survives manual disconnect"));
                    Equal(MaintenanceNetworkState.NotChecked, f.Service.GetMaintenanceNetworkSnapshot().State); Equal(false, f.Service.CanReconnect);
                    Equal(MaintenanceMenuAvailabilityStatus.NotConnected, f.Service.GetMaintenanceMenuAvailability(f.Device).Status);
                    Invoke(f.Main, "ConnectionTimer_Tick", null, EventArgs.Empty); Equal("连接", f.Button.Text);
                }
            });
            foreach (string flag in new[] { "isInstallBusy", "isSoftwareBusy", "maintenanceWindowOpen", "isConnecting", "isDisconnecting", "installerStateUnresolved" }) {
                string state = flag;
                test("Connection toggle UI rejects stale clicks while " + state, () => {
                    using (var f = new Fixture()) {
                        Set(f.Main, state, true); Invoke(f.Main, "RefreshInstallButton"); Equal(false, f.Button.Enabled);
                        Invoke(f.Main, "ConnectButton_Click", null, EventArgs.Empty); Pump(20);
                        Equal(0, f.Proxy.Disposals); True(f.Service.IsConnected); True(ReferenceEquals(f.Device, Field(f.Main, "router")));
                        Set(f.Main, state, false); Invoke(f.Main, "RefreshInstallButton"); Equal("断开", f.Button.Text); True(f.Button.Enabled);
                    }
                });
            }
            foreach (string gate in new[] { "connecting", "softwareBusy", "menuLifetime", "pending-installation" }) {
                string state = gate;
                test("Connection toggle service atomically refuses disconnect and reconnect while " + state, () => {
                    using (var f = new Fixture()) {
                        f.ServiceGate(state); Equal(false, f.Service.CanChangeConnection);
                        Equal(false, f.Service.DisconnectAsync().GetAwaiter().GetResult());
                        Throws<InvalidOperationException>(() => f.Service.ConnectAndDetectAsync("fixture.invalid", "fixture", "new", null, CancellationToken.None));
                        Equal(0, f.Proxy.Disposals); True(f.Service.IsConnected); True(f.Service.CanReconnect);
                        True(ReferenceEquals(f.Client, Field(f.Service, "client"))); Equal(0, f.Proxy.Commands);
                        Invoke(f.Main, "ConnectionTimer_Tick", null, EventArgs.Empty); Equal("断开", f.Button.Text); Equal(false, f.Button.Enabled);
                        Invoke(f.Main, "ConnectButton_Click", null, EventArgs.Empty); Equal(0, f.Proxy.Disposals);
                    }
                });
            }
            test("Connection toggle disconnect joins disposal off the UI thread and blocks competing operations", () => {
                using (var f = new Fixture()) using (var release = new ManualResetEventSlim()) {
                    f.Proxy.Release = release;
                    try {
                        f.Button.PerformClick(); Until(() => f.Proxy.Entered.IsSet);
                        Equal("断开中…", f.Button.Text); Equal(false, f.Button.Enabled); Equal(false, f.Service.CanChangeConnection);
                        Equal(false, f.Service.IsConnected); Equal(false, f.Service.CanReconnect);
                        Equal(false, f.Service.DisconnectAsync().GetAwaiter().GetResult());
                        Throws<InvalidOperationException>(() => f.Service.InstallOneClickAsync(f.Device, "not-read.run", null, CancellationToken.None));
                        Throws<InvalidOperationException>(() => f.Service.RunMaintenanceMenuAsync(f.Device, t => { }, null, CancellationToken.None));
                        Throws<InvalidOperationException>(() => f.Service.ConnectAndDetectAsync("fixture.invalid", "fixture", "new", null, CancellationToken.None));
                        int ticks = 0; using (var timer = new System.Windows.Forms.Timer { Interval = 10 }) { timer.Tick += (s, e) => ticks++; timer.Start(); Pump(80); timer.Stop(); }
                        True(ticks > 0); Invoke(f.Main, "ConnectButton_Click", null, EventArgs.Empty); Equal(1, f.Proxy.Disposals);
                    } finally { release.Set(); }
                    f.Disconnected(); True(f.Button.Enabled); True(f.Service.CanChangeConnection); Equal(0, f.Proxy.Commands);
                }
            });
            test("Connection toggle rejects disconnect while the real menu preparation lease is active", () => {
                using (var f = new Fixture()) using (var cancel = new CancellationTokenSource()) {
                    var http = new MaintenanceMenuSshTests.Http(); var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                    http.Hook = async token => { entered.TrySetResult(0); await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false); };
                    Set(f.Service, "maintenancePreparation", MaintenanceMenuSshTests.Service(http));
                    var run = f.Service.RunMaintenanceMenuAsync(f.Device, value => { throw new Exception("Cancelled preparation must not open terminal."); }, null, cancel.Token);
                    try {
                        Until(() => entered.Task.IsCompleted); True(f.Service.HasActiveMaintenanceMenu);
                        Equal(false, f.Service.DisconnectAsync().GetAwaiter().GetResult()); Equal(0, f.Proxy.Disposals); True(f.Service.CanReconnect);
                        Invoke(f.Main, "ConnectionTimer_Tick", null, EventArgs.Empty); Equal("断开", f.Button.Text); Equal(false, f.Button.Enabled);
                        Invoke(f.Main, "ConnectButton_Click", null, EventArgs.Empty); Equal(0, f.Proxy.Disposals);
                    } finally { cancel.Cancel(); }
                    Equal(MaintenancePreparationStatus.Cancelled, run.GetAwaiter().GetResult().Preparation.Status);
                    True(f.Service.IsConnected); True(f.Service.CanChangeConnection); Equal(0, f.Proxy.Commands);
                    Invoke(f.Main, "ConnectionTimer_Tick", null, EventArgs.Empty); True(f.Button.Enabled);
                }
            });
            test("Connection toggle service remains reusable after manual disconnect", () => {
                using (var f = new Fixture()) {
                    True(f.Service.DisconnectAsync().GetAwaiter().GetResult()); True(f.Service.CanChangeConnection); Equal(false, (bool)Field(f.Service, "disposed"));
                    Equal<object>(null, Field(f.Service, "client")); Equal<object>(null, Field(f.Service, "connectionFactory"));
                    Equal<object>(null, ((Func<string, string>)Field(f.Service, "sanitizeLog")).Target);
                    using (var cancel = new CancellationTokenSource()) {
                        cancel.Cancel(); var result = f.Service.ConnectAndDetectAsync("fixture.invalid", "fixture", "new", null, cancel.Token).GetAwaiter().GetResult();
                        Equal(ConnectionError.Cancelled, result.Error); Equal(false, f.Service.IsConnected); True(f.Service.CanChangeConnection);
                    }
                    True(f.Service.DisconnectAsync().GetAwaiter().GetResult()); Equal(1, f.Proxy.Disposals);
                }
            });
            test("Connection toggle idle disconnected service can be disconnected idempotently", () => {
                using (var service = new SshService()) {
                    True(service.DisconnectAsync().GetAwaiter().GetResult()); True(service.DisconnectAsync().GetAwaiter().GetResult());
                    True(service.CanChangeConnection); Equal(false, service.IsConnected); Equal(false, service.CanReconnect);
                    service.Dispose(); Equal(false, service.CanChangeConnection); Throws<ObjectDisposedException>(() => service.DisconnectAsync());
                }
            });
            test("Connection toggle disposal error clears active state and is shown without exception secrets", () => {
                using (var f = new Fixture()) {
                    f.Proxy.FailDispose = true; f.Button.PerformClick(); Until(() => !(bool)Field(f.Main, "isDisconnecting"));
                    Equal("连接", f.Button.Text); Equal(false, f.Service.IsConnected); Equal(false, f.Service.CanReconnect); Equal<object>(null, Field(f.Main, "router"));
                    True(Control(f.Main, "connectionLabel").Text.Contains("未能确认")); Equal(false, Control(f.Main, "logBox").Text.Contains("dispose-secret"));
                }
            });
            test("Connection toggle unexpected network loss keeps existing installation reconnect credentials", () => {
                using (var f = new Fixture()) {
                    f.Proxy.Connected = false; Invoke(f.Main, "ConnectionTimer_Tick", null, EventArgs.Empty);
                    Equal("连接", f.Button.Text); True(f.Service.CanReconnect); True(f.Device.IsConnected);
                    Equal(0, f.Proxy.Disposals); True(Control(f.Main, "connectionLabel").Text.Contains("自动重连"));
                    f.Proxy.Connected = true; Invoke(f.Main, "ConnectionTimer_Tick", null, EventArgs.Empty); Equal("断开", f.Button.Text);
                }
            });
            test("Connection toggle stale disconnect intent clears a session lost just before click", () => {
                using (var f = new Fixture()) {
                    Equal("断开", f.Button.Text); f.Proxy.Connected = false; f.Button.PerformClick(); f.Disconnected();
                    Equal(1, f.Proxy.Disposals); Equal(false, f.Service.CanReconnect); Equal<object>(null, Field(f.Main, "router"));
                }
            });
            test("Connection toggle disconnected click uses the original local validation", () => {
                using (var f = new Fixture()) {
                    f.Button.PerformClick(); f.Disconnected(); Control(f.Main, "passwordBox").Text = ""; f.Button.PerformClick();
                    True(Control(f.Main, "connectionLabel").Text.Contains("请输入密码")); Equal("连接", f.Button.Text); Equal(1, f.Proxy.Disposals);
                }
            });
        }
        private sealed class Fixture : IDisposable
        {
            public readonly MainForm Main = new MainForm(); public readonly SshService Service;
            public readonly SshClient Client = new SshClient("fixture.invalid", "fixture", "inert");
            public readonly SessionProxy Proxy; public readonly RouterEnvironment Device; private CancellationTokenSource menu;
            public Button Button { get { return (Button)Control(Main, "connectButton"); } }
            public Fixture()
            {
                Service = (SshService)Field(Main, "ssh"); var field = typeof(BaseClient).GetField("<Session>k__BackingField", Private);
                Proxy = new SessionProxy(field.FieldType); field.SetValue(Client, Proxy.GetTransparentProxy());
                Set(Service, "client", Client); Set(Service, "connectionFactory", new Func<PasswordConnectionInfo>(() => new PasswordConnectionInfo("fixture.invalid", 22, "fixture", "disconnect-secret")));
                Set(Service, "sanitizeLog", new Func<string, string>(s => s.Replace("disconnect-secret", "[hidden]")));
                string fingerprint; using (var hash = SHA256.Create()) fingerprint = "SHA256:" + Convert.ToBase64String(hash.ComputeHash(new byte[] { 1, 2, 3 })).TrimEnd('=');
                bool first; ((HostKeyTrustStore)Field(Service, "trustStore")).TryTrust("fixture.invalid", 22, fingerprint, out first);
                Device = new RouterEnvironment { IsConnected = true, Version = "24.10.8", SdkSeries = "24.10", Architecture = "aarch64_cortex-a53", PackageManager = PackageManagerKind.OPKG, QuickStartAvailable = true, HostKeyFingerprint = fingerprint };
                Set(Main, "router", Device); Control(Main, "hostBox").Text = "fixture.invalid"; Control(Main, "usernameBox").Text = "fixture";
                MaintenanceLaunchTests.SeedDirect(Service, Client);
                Main.Show();
                ((System.Windows.Forms.Timer)Field(Main, "connectionTimer")).Stop(); Invoke(Main, "DisplayEnvironment"); Invoke(Main, "RefreshInstallButton"); Pump(10);
                // Form.Show + DoEvents does not always install the context supplied
                // by the real Application.Run loop. Async UI events must use it.
                if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            }
            public void ServiceGate(string state)
            {
                if (state == "menuLifetime") { menu = new CancellationTokenSource(); Set(Service, state, menu); }
                else if (state == "pending-installation") { object workflow = Field(Service, "installWorkflow"); var unfinished = workflow.GetType().GetField("unfinished", Private); unfinished.SetValue(workflow, FormatterServices.GetUninitializedObject(unfinished.FieldType)); }
                else Set(Service, state, true);
            }
            public void Disconnected() { Until(() => !(bool)Field(Main, "isDisconnecting") && (Field(Main, "router") == null)); Equal("连接", Button.Text); }
            public void Dispose()
            {
                if (Proxy.Release != null) Proxy.Release.Set();
                Set(Service, "connecting", false); Set(Service, "softwareBusy", false); Set(Service, "menuLifetime", null);
                object workflow = Field(Service, "installWorkflow"); workflow.GetType().GetField("unfinished", Private).SetValue(workflow, null);
                foreach (string flag in new[] { "isConnecting", "isDisconnecting", "isInstallBusy", "isSoftwareBusy", "maintenanceWindowOpen", "installerStateUnresolved" }) Set(Main, flag, false);
                Main.Close(); Main.Dispose(); Proxy.FailDispose = false; Client.Dispose();
                if (menu != null) menu.Dispose(); Proxy.Entered.Dispose();
            }
        }
        private sealed class SessionProxy : RealProxy
        {
            public bool Connected = true, FailDispose; public int Disposals, Commands; public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(); public ManualResetEventSlim Release;
            public SessionProxy(Type type) : base(type) { }
            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message;
                if (call.MethodName == "get_IsConnected") return new ReturnMessage(Connected, null, 0, call.LogicalCallContext, call);
                if (call.MethodName == "Dispose" || call.MethodName == "Disconnect") {
                    Interlocked.Increment(ref Disposals); Connected = false; Entered.Set();
                    if (Release != null && !Release.Wait(5000)) return new ReturnMessage(new TimeoutException("fixture disposal wait"), call);
                    if (FailDispose) return new ReturnMessage(new InvalidOperationException("dispose-secret"), call);
                }
                else if (!call.MethodName.StartsWith("OnDisconnect", StringComparison.Ordinal) && !call.MethodName.StartsWith("add_", StringComparison.Ordinal) && !call.MethodName.StartsWith("remove_", StringComparison.Ordinal)) {
                    Commands++; return new ReturnMessage(new InvalidOperationException("No network permitted: " + call.MethodName), call);
                }
                return new ReturnMessage(null, null, 0, call.LogicalCallContext, call);
            }
        }
        private static Control Control(MainForm form, string name)
        { var field = form.GetType().GetField(name, Private); if (field != null) return (Control)field.GetValue(form); return form.Controls.Find(name, true)[0]; }
        private static object Field(object owner, string name) { return owner.GetType().GetField(name, Private).GetValue(owner); }
        private static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Private).SetValue(owner, value); }
        private static object Invoke(object owner, string method, params object[] args) { return owner.GetType().GetMethod(method, Private).Invoke(owner, args); }
        private static void Pump(int milliseconds) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(1); } }
        private static void Until(Func<bool> condition) { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.ElapsedMilliseconds > 8000) throw new TimeoutException("Connection toggle timed out."); Application.DoEvents(); Thread.Sleep(1); } Application.DoEvents(); }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
        private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    }
}
