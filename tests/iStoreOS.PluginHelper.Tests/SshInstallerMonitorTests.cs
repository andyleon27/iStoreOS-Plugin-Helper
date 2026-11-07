using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class SshInstallerMonitorTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<string, Action> test)
        {
            test("SSH monitor requires confirmed subsystem evidence", () => Throws<InvalidOperationException>(() => new SshInstallerMonitor(RemoteTempSession.Create(), text => null)));
            test("SSH monitor negative offsets issue no commands", () => { int calls = 0; using (var m = new SshInstallerMonitor(Remote(), text => { calls++; return null; })) { Throws<ArgumentException>(() => m.Poll(-1, 0)); Throws<ArgumentException>(() => m.Poll(0, -1)); } Equal(0, calls); });
            test("SSH monitor disposed scope cannot read or disconnect home SSH", () => { int calls = 0; var m = new SshInstallerMonitor(Remote(), text => { calls++; return null; }); m.Dispose(); m.Dispose(); Throws<ObjectDisposedException>(() => m.Poll(0, 0)); Equal(0, calls); });
            test("SSH monitor missing files remain running", () => { var r = Remote(); var p = SshInstallerMonitor.Parse(r, 0, 0, Wire(r, null, null, null)); Equal(null, p.ExitCode); Equal(0, p.Stdout.Length + p.Stderr.Length); });
            test("SSH monitor empty logs and delayed exit are distinct", () => { var r = Remote(); Equal(null, SshInstallerMonitor.Parse(r, 0, 0, Wire(r, null, new byte[0], new byte[0])).ExitCode); Equal((int?)0, SshInstallerMonitor.Parse(r, 0, 0, Wire(r, B("0\n"), new byte[0], new byte[0])).ExitCode); });
            test("SSH monitor log keywords and protocol text never imply success", () => { var r = Remote(); var bytes = B("success exit=0\nIPH_INSTALL_POLL_V1|" + r.Id + "|2|0|0|0|0\n0\nIPH_INSTALL_POLL_END|" + r.Id + "\n中文😀\0"); var p = SshInstallerMonitor.Parse(r, 0, 0, Wire(r, null, bytes, bytes)); Equal(null, p.ExitCode); Bytes(bytes, p.Stdout); Bytes(bytes, p.Stderr); });
            foreach (string state in new[] { "", " ", "-1", "256", "99999999999999", "exit=0", "0\n1", "0\0", "正常", "0\nIPH_INSTALL_POLL_END" }) {
                string value = state; test("SSH monitor rejects invalid exit " + value.Replace('\0', '?'), () => { var r = Remote(); Throws<InvalidDataException>(() => SshInstallerMonitor.Parse(r, 0, 0, Wire(r, B(value), B("success"), null))); });
            }
            foreach (string state in new[] { "0", "0\n", " 7\r\n", "255", "000" }) {
                string value = state; test("SSH monitor accepts bounded trusted exit " + value.Trim(), () => { var r = Remote(); Equal((int?)int.Parse(value.Trim()), SshInstallerMonitor.Parse(r, 0, 0, Wire(r, B(value), null, null)).ExitCode); });
            }
            foreach (string fault in new[] { "wrong-id", "prefix", "truncated", "extra", "header-size", "negative-count", "overflow", "inconsistent-count", "trailer", "over-limit" }) {
                string value = fault; test("SSH monitor rejects response " + value, () => {
                    var r = Remote(); byte[] data = Wire(r, B("0"), B("abc"), null);
                    if (value == "wrong-id") data = Wire(Remote(), B("0"), B("abc"), null);
                    if (value == "prefix") data = B("banner\n").Concat(data).ToArray();
                    if (value == "truncated") data = data.Take(data.Length - 2).ToArray();
                    if (value == "extra") data = data.Concat(new byte[] { 0 }).ToArray();
                    if (value == "header-size") data = B(new string('x', 256) + "\n");
                    if (value == "negative-count") data = B("IPH_INSTALL_POLL_V1|" + r.Id + "|-1|0|-1|0|0\n");
                    if (value == "overflow") data = B("IPH_INSTALL_POLL_V1|" + r.Id + "|-1|9223372036854775808|0|0|0\n");
                    if (value == "inconsistent-count") data = B("IPH_INSTALL_POLL_V1|" + r.Id + "|-1|9|0|0|0\n");
                    if (value == "trailer") data[data.Length - 2] ^= 1;
                    if (value == "over-limit") data = new byte[SshInstallerMonitor.MaximumWireBytes + 1];
                    Throws<InvalidDataException>(() => SshInstallerMonitor.Parse(r, 0, 0, data));
                });
            }
            test("SSH monitor independent raw offsets and final 64KiB drain", () => {
                var r = Remote(); byte[] output = Enumerable.Range(0, 131077).Select(i => (byte)(i % 256)).ToArray(), error = B("stderr 中文\0😀");
                var seen = new List<byte>(); long o = 0, e = 0; int polls = 0;
                while (true) { var p = SshInstallerMonitor.Parse(r, o, e, Wire(r, B("0"), output, error, o, e)); seen.AddRange(p.Stdout); o += p.Stdout.Length; if (polls++ == 0) Bytes(error, p.Stderr); else Equal(0, p.Stderr.Length); e += p.Stderr.Length; if (p.Stdout.Length < 65536) break; }
                Bytes(output, seen.ToArray()); Equal(3, polls);
            });
            test("SSH monitor shrink never rewinds offsets or repeats log bytes", () => { var r = Remote(); var p = SshInstallerMonitor.Parse(r, 999, long.MaxValue, Wire(r, null, B("a"), B("b"), 999, long.MaxValue)); Equal(0, p.Stdout.Length + p.Stderr.Length); True(p.Diagnostic.Contains("不回退偏移")); });
            foreach (int? status in new int?[] { null, 1, 70, 74 }) { int? value = status; test("SSH monitor command status " + value + " cannot imply installer success", () => { var r = Remote(); using (var m = new SshInstallerMonitor(r, text => new InstallerCommandResult { ExitStatus = value, Output = Wire(r, B("0"), null, null) })) Throws<IOException>(() => m.Poll(0, 0)); }); }
            test("SSH monitor unchanged SFTP selector never opens SSH exec or fallback", () => {
                int sftp = 0, active = 0; using (var client = new SshClient("unused.invalid", "fixture", "inert")) {
                    var install = new SshPluginInstallSession(() => { active++; return client; }, () => { sftp++; throw new SshAuthenticationException("auth rejected"); }, null, t => t);
                    using (var scope = install.OpenInstallerMonitor(RemoteTempSession.Create())) { Equal("安装监测：SFTP", ((IInstallerMonitorInfo)scope).Description); Throws<InvalidOperationException>(() => install.PollInstaller(RemoteTempSession.Create(), 0, 0)); }
                    // A different session must be rejected before either path is read.
                    Equal(0, active); Equal(0, sftp);
                }
            });
            test("SSH monitor SFTP authentication rejection never switches to SSH", () => {
                int sftp = 0, active = 0; var r = RemoteTempSession.Create();
                var install = new SshPluginInstallSession(() => { active++; return null; }, () => { sftp++; throw new SshAuthenticationException("auth rejected"); }, null, t => t);
                using (var scope = install.OpenInstallerMonitor(r)) Throws<SshAuthenticationException>(() => install.PollInstaller(r, 0, 0)); Equal(1, sftp); Equal(0, active);
            });
            test("SSH monitor fresh adapter keeps operation evidence and isolates another job", () => {
                var r = Remote(); var install = new SshPluginInstallSession((SshClient)null, () => { throw new Exception("SFTP forbidden"); }, null, t => t);
                using (var scope = install.OpenInstallerMonitor(r)) Equal("安装监测：SSH fallback（设备无 SFTP subsystem）", ((IInstallerMonitorInfo)scope).Description);
                using (var scope = new SshPluginInstallSession((SshClient)null, null, null, t => t).OpenInstallerMonitor(RemoteTempSession.Create())) Equal("安装监测：SFTP", ((IInstallerMonitorInfo)scope).Description);
            });
            test("SSH monitor real command retains raw bytes through fragmented SSH.NET events", () => {
                var r = Remote(); byte[] bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
                using (var peer = new SshPeer(text => new InstallerCommandResult { ExitStatus = 0, Output = Wire(r, B("0"), bytes, B("中文😀")) })) {
                    using (var monitor = new SshInstallerMonitor(r, text => SshInstallerMonitor.Execute(() => peer.Client, text))) { var p = monitor.Poll(0, 0); Bytes(bytes, p.Stdout); Bytes(B("中文😀"), p.Stderr); Equal((int?)0, p.ExitCode); }
                    True(peer.Client.IsConnected); Equal(0, peer.SessionDisposals); True(peer.ChannelDisposals > 0);
                }
            });
            test("SSH monitor real command without SSH exit-status stays unknown", () => { var r = Remote(); using (var peer = new SshPeer(text => new InstallerCommandResult { ExitStatus = null, Output = Wire(r, B("0"), null, null) })) using (var m = new SshInstallerMonitor(r, text => SshInstallerMonitor.Execute(() => peer.Client, text))) Throws<IOException>(() => m.Poll(0, 0)); });
            foreach (LocalPackageFormat format in Enum.GetValues(typeof(LocalPackageFormat))) {
                LocalPackageFormat value = format;
                test("SSH monitor unchanged SFTP upload and persistent monitoring workflow " + value, () => InstallationUploadTests.Fixture(value, path => {
                    var pipeline = new InstallationUploadTests.Pipeline(value, "ok", false); pipeline.Fake.UsePersistentPolling = true;
                    var result = Task.Run(() => new InstallationWorkflowService { Engine = new PluginInstallationService { PollInterval = TimeSpan.FromMilliseconds(1) } }.RunAsync(new InstallationUploadTests.Workflow(pipeline), InstallationUploadTests.Device(value), path, null, CancellationToken.None)).GetAwaiter().GetResult();
                    Equal(InstallationState.Success, result.State); Equal(false, pipeline.Remote.SftpUnavailable); Equal(0, pipeline.Transfers.ScpCreates); Equal(1, pipeline.Fake.PersistentFiles.Creates); Equal(1, pipeline.Fake.PersistentFiles.Disposals); Equal(1, pipeline.Fake.Cleanups);
                }));
                foreach (string mode in new[] { "exit0", "exit7", "delayed", "transient", "permanent", "resume", "bad-exit", "no-exit", "cancel-running", "cancel-upload" }) {
                    string fault = mode; test("SSH monitor full SCP workflow " + value + " " + fault, () => FullWorkflow(value, fault));
                }
                test("SSH monitor real shell raw bytes metadata paths and delayed state " + value, () => Shell(value));
            }
        }
        private static void FullWorkflow(LocalPackageFormat format, string fault)
        {
            InstallationUploadTests.Fixture(format, path => {
                using (var cancel = new CancellationTokenSource()) {
                    var pipeline = new InstallationUploadTests.Pipeline(format, "ok"); var logs = new List<InstallationWorkflowProgress>();
                    int polls = 0, connections = 0; bool recoveryAllowed = fault != "resume"; byte[] output = B(new string('a', 65535) + "中文😀\nfinal\n"), error = B("stderr-only\n");
                    var peers = new List<SshPeer>(); SshPeer current = null;
                    Func<SshPeer> connect = () => {
                        connections++;
                        var peer = new SshPeer(text => {
                            polls++; var remote = pipeline.Remote;
                            if (fault == "permanent" || !recoveryAllowed || (fault == "transient" && polls == 2)) { current.Connected = false; pipeline.Fake.Connected = false; throw new SshConnectionException("test connection lost"); }
                            long o = Offset(text, "stdout.log"), e = Offset(text, "stderr.log");
                            byte[] code = fault == "bad-exit" ? B("success") : fault == "no-exit" || ((fault == "delayed") && polls == 1) ? null : B(fault == "exit7" ? "7\n" : "0\n");
                            return new InstallerCommandResult { ExitStatus = 0, Output = Wire(remote, code, output, error, o, e) };
                        }); peers.Add(peer); return peer;
                    };
                    try {
                        current = connect(); var install = new SshPluginInstallSession(() => current.Client, () => { throw new Exception("Confirmed no SFTP: monitor must not create a new SFTP connection."); }, null, t => t);
                        pipeline.MonitorSession = install;
                        pipeline.Fake.Runs.Enqueue(new WorkflowTests.Outcome { Exit = fault == "exit7" ? 7 : 0 });
                        pipeline.Fake.AfterStart = () => { if (fault == "cancel-running") cancel.Cancel(); };
                        if (fault == "cancel-upload") pipeline.Transfers.Scp.UploadHook = cancel.Cancel;
                        Action reconnect = () => { if (fault == "permanent" || !recoveryAllowed) throw new IOException("still offline"); current = connect(); };
                        var session = new RecoveringWorkflow(pipeline, reconnect);
                        var workflow = new InstallationWorkflowService { ReconnectInterval = TimeSpan.FromMilliseconds(1), ReconnectAttempts = 2, Engine = new PluginInstallationService { PollInterval = TimeSpan.FromMilliseconds(1), OverallLimit = fault == "no-exit" ? TimeSpan.FromMilliseconds(150) : TimeSpan.FromSeconds(3), InactiveLimit = TimeSpan.FromSeconds(3) } };
                        var result = Task.Run(() => workflow.RunAsync(session, InstallationUploadTests.Device(format), path, new InlineProgress<InstallationWorkflowProgress>(logs.Add), cancel.Token)).GetAwaiter().GetResult();
                        if (fault == "resume") {
                            True(result.PendingInstaller); Equal(0, pipeline.Fake.Cleanups); Equal(1, pipeline.Fake.Starts); recoveryAllowed = true;
                            pipeline.MonitorSession = new SshPluginInstallSession(() => current.Client, () => { throw new Exception("SFTP forbidden after resume"); }, null, t => t);
                            var resumed = Task.Run(() => workflow.RunAsync(new RecoveringWorkflow(pipeline, reconnect), InstallationUploadTests.Device(format), path, new InlineProgress<InstallationWorkflowProgress>(logs.Add), CancellationToken.None)).GetAwaiter().GetResult();
                            Equal(InstallationState.Success, resumed.State); Equal((int?)0, resumed.Installation.InstallerExitCode); Equal(1, pipeline.Fake.Starts); Equal(1, pipeline.Fake.Cleanups); Equal(1, pipeline.Transfers.ScpCreates); Equal(2, connections); True(pipeline.Remote.SftpUnavailable); True(current.Client.IsConnected); return;
                        }
                        if (fault == "cancel-upload") { Equal(0, pipeline.Fake.Starts); True(result.Installation.Status == InstallStatus.Cancelled); Equal(1, pipeline.Fake.Cleanups); return; }
                        Equal(1, pipeline.Fake.Starts); True(pipeline.Remote.SftpUnavailable); Equal(1, pipeline.Transfers.SftpCreates); Equal(1, pipeline.Transfers.ScpCreates);
                        True(logs.Any(l => l.Log != null && l.Log.Contains("安装监测：SSH fallback（设备无 SFTP subsystem）")));
                        if (fault == "bad-exit" || fault == "no-exit" || fault == "permanent") {
                            True(result.PendingInstaller); Equal(InstallStatus.InstallerStateUnknown, result.Installation.Status); Equal(0, pipeline.Fake.Cleanups); Equal(false, result.Installation.InstallerEnded);
                        }
                        else {
                            Equal(fault == "exit7" ? InstallStatus.FailedInstaller : InstallStatus.SuccessVerified, result.Installation.Status); Equal((int?)(fault == "exit7" ? 7 : 0), result.Installation.InstallerExitCode);
                            Equal(1, pipeline.Fake.Cleanups); True(result.Installation.CleanupSucceeded); True(current.Client.IsConnected); Equal(0, current.SessionDisposals);
                            string log = string.Join("\n", logs.Where(l => l.Transfer != null && l.Transfer.Log != null && (l.Transfer.Log.StartsWith("[stdout]", StringComparison.Ordinal) || l.Transfer.Log.StartsWith("[stderr]", StringComparison.Ordinal))).Select(l => l.Transfer.Log));
                            Equal(1, Occurrences(log, "final")); Equal(1, Occurrences(log, "stderr-only")); True(log.Contains("中文😀")); Equal(false, log.Contains("Installer monitoring SFTP connection count"));
                        }
                        if (fault == "transient") { Equal(2, connections); Equal(1, pipeline.Fake.Reconnects); }
                        else Equal(1, connections);
                    }
                    finally { foreach (var peer in peers) peer.Dispose(); }
                }
            });
        }
        private sealed class RecoveringWorkflow : IInstallationWorkflowSession
        {
            private readonly InstallationUploadTests.Pipeline pipeline; private readonly Action reconnect;
            public RecoveringWorkflow(InstallationUploadTests.Pipeline pipeline, Action reconnect) { this.pipeline = pipeline; this.reconnect = reconnect; }
            public bool IsConnected { get { return pipeline.Fake.IsConnected; } }
            public IPluginInstallSession Installer { get { return pipeline; } }
            public ISoftwareSourceSession Sources { get { return pipeline.Fake; } }
            public RouterEnvironment ConnectAndDetect(RouterEnvironment expected, bool force, Action<string> log, CancellationToken token) { if (force || !IsConnected) reconnect(); return pipeline.Fake.ConnectAndDetect(expected, force, log, token); }
            public RouterReadiness ReadReadiness(IList<RepositoryFeed> feeds) { return pipeline.Fake.ReadReadiness(feeds); }
            public bool PackageManagerBusy(PackageManagerKind manager) { return pipeline.Fake.PackageManagerBusy(manager); }
            public bool WasLaunched(RemoteTempSession remote) { return pipeline.Fake.WasLaunched(remote); }
            public ProbeResult RestoreOriginal(PackageManagerKind manager, string text, string backup) { return pipeline.Fake.RestoreOriginal(manager, text, backup); }
            public bool DeleteBackup(string backup) { return pipeline.Fake.DeleteBackup(backup); }
            public string Sanitize(string text) { return pipeline.Sanitize(text); }
        }
        private static long Offset(string text, string filename)
        { var match = System.Text.RegularExpressions.Regex.Match(text, "if='[^']*/" + System.Text.RegularExpressions.Regex.Escape(filename) + "' bs=1 skip=([0-9]+)"); return long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); }
        private static byte[] Wire(RemoteTempSession r, byte[] exit, byte[] stdout, byte[] stderr, long o = 0, long e = 0)
        {
            byte[] output = stdout == null ? new byte[0] : stdout.Skip((int)Math.Min(o, stdout.Length)).Take(65536).ToArray();
            byte[] error = stderr == null ? new byte[0] : stderr.Skip((int)Math.Min(e, stderr.Length)).Take(65536).ToArray();
            return B("IPH_INSTALL_POLL_V1|" + r.Id + "|" + (exit == null ? -1 : exit.Length) + "|" + (stdout == null ? -1 : stdout.Length) + "|" + output.Length + "|" + (stderr == null ? -1 : stderr.Length) + "|" + error.Length + "\n")
                .Concat(exit ?? new byte[0]).Concat(output).Concat(error).Concat(B("IPH_INSTALL_POLL_END|" + r.Id + "\n")).ToArray();
        }
        private static void Shell(LocalPackageFormat format)
        {
            string folder = Path.Combine(Path.GetTempPath(), "monitor-shell-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try {
                var remote = Remote(format); string root = Path.Combine(folder, "remote"), dir = Path.Combine(root, remote.Id); Directory.CreateDirectory(dir);
                byte[] output = Enumerable.Range(0, 66000).Select(i => (byte)(i % 256)).ToArray(), error = B("stderr $ ` ' \" ;\nexit=0\n中文😀\0");
                Func<long, long, InstallerCommandResult> poll = (o, e) => RunShell(folder, SshInstallerMonitor.Command(remote, o, e).Replace(RemoteTempSession.Root, Unix(root)));
                var absent = poll(0, 0); Equal((int?)0, absent.ExitStatus); Equal(null, SshInstallerMonitor.Parse(remote, 0, 0, absent.Output).ExitCode);
                File.WriteAllBytes(Path.Combine(dir, "stdout.log"), output); File.WriteAllBytes(Path.Combine(dir, "stderr.log"), error);
                var first = SshInstallerMonitor.Parse(remote, 0, 0, poll(0, 0).Output); Equal(null, first.ExitCode); Equal(65536, first.Stdout.Length); Bytes(error, first.Stderr);
                File.WriteAllText(Path.Combine(dir, "exit.code"), "7\n", new UTF8Encoding(false));
                var last = SshInstallerMonitor.Parse(remote, 65536, error.Length, poll(65536, error.Length).Output); Equal((int?)7, last.ExitCode); Bytes(output.Skip(65536).ToArray(), last.Stdout); Equal(0, last.Stderr.Length);
                File.WriteAllText(Path.Combine(dir, "exit.code"), new string('0', 65)); True(poll(0, 0).ExitStatus != 0);
                File.Delete(Path.Combine(dir, "exit.code")); Directory.CreateDirectory(Path.Combine(dir, "exit.code")); True(poll(0, 0).ExitStatus != 0); Directory.Delete(Path.Combine(dir, "exit.code"));
                string target = Path.Combine(dir, "stderr.log"); File.Delete(target);
                var link = RunShell(folder, "ln -s missing '" + Unix(target) + "'"); Equal((int?)0, link.ExitStatus); True(poll(0, 0).ExitStatus != 0);
            }
            finally { Directory.Delete(folder, true); }
        }
        private static InstallerCommandResult RunShell(string folder, string text)
        {
            string path = Path.Combine(folder, "command.sh"); File.WriteAllText(path, text + "\n", new UTF8Encoding(false));
            var start = new ProcessStartInfo { FileName = @"C:\Program Files\Git\bin\bash.exe", Arguments = "--noprofile --norc \"" + path.Replace('\\', '/') + "\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.EnvironmentVariables["MSYS"] = "winsymlinks:nativestrict";
            using (var p = Process.Start(start)) using (var output = new MemoryStream()) {
                var read = Task.Run(() => p.StandardOutput.BaseStream.CopyTo(output));
                if (!p.WaitForExit(5000)) { p.Kill(); throw new TimeoutException("SSH shell fixture timed out."); } read.GetAwaiter().GetResult();
                return new InstallerCommandResult { ExitStatus = p.ExitCode, Output = output.ToArray(), Error = p.StandardError.ReadToEnd() };
            }
        }
        private static string Unix(string path) { return "/" + char.ToLowerInvariant(path[0]) + path.Substring(2).Replace('\\', '/'); }
        private static RemoteTempSession Remote(LocalPackageFormat format = LocalPackageFormat.Run) { var r = RemoteTempSession.Create(format); r.SftpUnavailable = true; return r; }
        private sealed class SshPeer : RealProxy, IDisposable
        {
            public readonly SshClient Client = new SshClient("unused.invalid", "fixture", "inert");
            public bool Connected = true;
            public int SessionDisposals, ChannelDisposals;
            private readonly Func<string, InstallerCommandResult> run;
            public SshPeer(Func<string, InstallerCommandResult> run) : base(typeof(BaseClient).Assembly.GetType("Renci.SshNet.ISession", true)) { this.run = run; typeof(BaseClient).GetField("<Session>k__BackingField", Private).SetValue(Client, GetTransparentProxy()); }
            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message; object result = null;
                if (call.MethodName == "get_IsConnected") result = Connected;
                else if (call.MethodName == "get_ConnectionInfo") result = Client.ConnectionInfo;
                else if (call.MethodName == "get_SessionLoggerFactory") result = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
                else if (call.MethodName == "CreateChannelSession") result = new Channel(((MethodInfo)call.MethodBase).ReturnType, run, () => ChannelDisposals++).GetTransparentProxy();
                else if (call.MethodName == "Dispose" || call.MethodName == "Disconnect") { Connected = false; SessionDisposals++; }
                else if (!call.MethodName.StartsWith("add_", StringComparison.Ordinal) && !call.MethodName.StartsWith("remove_", StringComparison.Ordinal) && !call.MethodName.StartsWith("OnDisconnect", StringComparison.Ordinal)) return new ReturnMessage(new InvalidOperationException("Offline session forbids " + call.MethodName), call);
                return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
            }
            public void Dispose() { Client.Dispose(); }
        }
        private sealed class Channel : RealProxy
        {
            private readonly Func<string, InstallerCommandResult> run; private readonly Action dispose;
            private readonly Dictionary<string, Delegate> events = new Dictionary<string, Delegate>();
            public Channel(Type type, Func<string, InstallerCommandResult> run, Action dispose) : base(type) { this.run = run; this.dispose = dispose; }
            private void Fire(string name, object args) { Delegate handler; if (events.TryGetValue(name, out handler) && handler != null) handler.DynamicInvoke(GetTransparentProxy(), args); }
            private static object Args(string name, params object[] values) { return Activator.CreateInstance(typeof(BaseClient).Assembly.GetType(name, true), BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, values, null); }
            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message; object result = null;
                try {
                    if (call.MethodName.StartsWith("add_", StringComparison.Ordinal) || call.MethodName.StartsWith("remove_", StringComparison.Ordinal)) {
                        string name = call.MethodName.Substring(call.MethodName.IndexOf('_') + 1); Delegate old; events.TryGetValue(name, out old); events[name] = call.MethodName.StartsWith("add_", StringComparison.Ordinal) ? Delegate.Combine(old, (Delegate)call.Args[0]) : Delegate.Remove(old, (Delegate)call.Args[0]);
                    }
                    else if (call.MethodName == "Open") { }
                    else if (call.MethodName == "Dispose") dispose();
                    else if (call.MethodName == "get_IsOpen") result = true;
                    else if (call.MethodName == "SendExecRequest") {
                        var response = run((string)call.Args[0]);
                        for (int offset = 0; offset < response.Output.Length; offset += 113) Fire("DataReceived", Args("Renci.SshNet.Common.ChannelDataEventArgs", (uint)0, new ArraySegment<byte>(response.Output, offset, Math.Min(113, response.Output.Length - offset))));
                        if (response.ExitStatus.HasValue) Fire("RequestReceived", Args("Renci.SshNet.Common.ChannelRequestEventArgs", Args("Renci.SshNet.Messages.Connection.ExitStatusRequestInfo", (uint)response.ExitStatus.Value)));
                        Fire("Closed", Args("Renci.SshNet.Common.ChannelEventArgs", (uint)0)); result = true;
                    }
                    else throw new InvalidOperationException("Offline channel forbids " + call.MethodName);
                    return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
                }
                catch (Exception error) { return new ReturnMessage(error, call); }
            }
        }
        private static byte[] B(string value) { return Encoding.UTF8.GetBytes(value); }
        private static int Occurrences(string text, string value) { return (text.Length - text.Replace(value, "").Length) / value.Length; }
        private static void Bytes(byte[] expected, byte[] actual) { True(expected.SequenceEqual(actual)); }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
        private static T Throws<T>(Action action) where T : Exception { try { action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
    }
}
