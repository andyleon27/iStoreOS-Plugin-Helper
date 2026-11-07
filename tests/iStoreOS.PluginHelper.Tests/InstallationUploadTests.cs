using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class InstallationUploadTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<string, Action> test)
        {
            foreach (string message in new[] { "Channel was closed.", "Subsystem 'sftp' could not be executed." })
            {
                string text = message;
                test("Upload exact subsystem failure allowed: " + text, () => True(InstallationUploadPolicy.IsUnavailable(new SshException(text), true, true, false)));
                test("Upload no authenticated session forbids: " + text, () => Equal(false, InstallationUploadPolicy.IsUnavailable(new SshException(text), false, true, false)));
                test("Upload no trusted host key forbids: " + text, () => Equal(false, InstallationUploadPolicy.IsUnavailable(new SshException(text), true, false, false)));
                test("Upload transport error forbids: " + text, () => Equal(false, InstallationUploadPolicy.IsUnavailable(new SshException(text), true, true, true)));
            }
            var forbidden = new Exception[] {
                new SshAuthenticationException("Channel was closed."), new SshConnectionException("Channel was closed."),
                new SshOperationTimeoutException("Channel was closed."), new TimeoutException("Channel was closed."),
                new SocketException((int)SocketError.ConnectionReset), new IOException("Channel was closed."),
                new EndOfStreamException("Channel was closed."), new OperationCanceledException(),
                new SshException("Key exchange negotiation failed."), new SshException("Host key rejected."),
                new SshException("Connection was closed by the server."), new SftpPermissionDeniedException("Permission denied"),
                new SshException("subsystem unavailable"), new SshException("Channel was closed.", new SocketException()),
                new IOException("wrapped", new SshException("Channel was closed."))
            };
            foreach (var error in forbidden)
            {
                Exception failure = error;
                test("Upload forbids " + failure.GetType().Name + " " + failure.Message, () => {
                    Equal(false, InstallationUploadPolicy.IsUnavailable(failure, true, true, false));
                    var f = new Transfers(); f.Sftp.ConnectError = failure;
                    Throws<Exception>(() => f.Run()); Equal(0, f.ScpCreates); Equal(1, f.Sftp.Disposals);
                });
            }
            test("Upload SFTP success never constructs SCP", () => {
                var f = new Transfers(); f.Run(); Equal(1, f.Sftp.Uploads); Equal(0, f.ScpCreates); Equal(1, f.Sftp.Disposals);
                Equal("payload 中文", Encoding.UTF8.GetString(f.Sftp.Bytes)); True(f.Log.Contains("SFTP 上传完成。"));
            });
            test("Upload subsystem refused switches once after disposing SFTP", () => {
                var f = new Transfers(true); f.Run(); Equal(0, f.Sftp.Uploads); Equal(1, f.Scp.Uploads); Equal(1, f.Scp.Disposals);
                Equal("payload 中文", Encoding.UTF8.GetString(f.Scp.Bytes)); True(f.Log.Any(s => s.Contains("SFTP 不可用，自动切换 SCP 上传"))); True(f.Log.Contains("SCP 上传完成。"));
                Equal("sftp-dispose,target-check,scp-create,scp-connect,scp-upload,scp-dispose", string.Join(",", f.Trace));
            });
            test("Upload SCP failure retains sanitized diagnostics of both protocols", () => {
                var f = new Transfers(true); f.Scp.UploadError = new ScpException("scp: not found test-secret");
                var error = Throws<IOException>(() => f.Run()); Contains(error.Message, "SFTP 初始化：SshException：Channel was closed."); Contains(error.Message, "SCP：ScpException：scp: not found");
                Equal(false, error.ToString().Contains("test-secret")); Equal(1, f.Scp.Disposals);
            });
            test("Upload factory failure retains SFTP evidence", () => {
                var f = new Transfers(true); f.ScpFactoryError = new IOException("factory failed");
                var error = Throws<IOException>(() => f.Run()); Contains(error.Message, "SFTP 初始化"); Contains(error.Message, "factory failed");
            });
            test("Upload mid-transfer closed channel never falls back", () => {
                var f = new Transfers(); f.Sftp.UploadError = new SftpSubsystemUnavailableException(new SshException("Channel was closed."));
                Throws<IOException>(() => f.Run()); Equal(0, f.ScpCreates); Equal(1, f.Sftp.Disposals);
            });
            test("Upload home SSH disconnect forbids fallback", () => {
                var f = new Transfers(true) { HomeConnected = false }; Contains(Throws<IOException>(() => f.Run()).Message, "首页 SSH 已断开"); Equal(0, f.ScpCreates);
            });
            test("Upload SCP refuses existing or unsafe target", () => {
                var f = new Transfers(true) { UnsafeTarget = true }; Contains(Throws<IOException>(() => f.Run()).Message, "目标非空"); Equal(0, f.ScpCreates);
            });
            test("Upload pre-cancellation creates no connection", () => {
                using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); var f = new Transfers(true); Throws<OperationCanceledException>(() => f.Run(cancel.Token)); Equal(0, f.SftpCreates); Equal(0, f.ScpCreates); }
            });
            test("Upload cancel between protocols never creates SCP", () => {
                using (var cancel = new CancellationTokenSource()) { var f = new Transfers(true); f.Sftp.DisposeHook = cancel.Cancel; Throws<OperationCanceledException>(() => f.Run(cancel.Token)); Equal(0, f.ScpCreates); }
            });
            foreach (bool fallback in new[] { false, true })
            {
                bool scp = fallback;
                test("Upload cancel in transfer disposes before returning " + (scp ? "SCP" : "SFTP"), () => {
                    using (var cancel = new CancellationTokenSource()) {
                        var f = new Transfers(scp); (scp ? f.Scp : f.Sftp).UploadHook = cancel.Cancel;
                        Throws<OperationCanceledException>(() => f.Run(cancel.Token)); Equal(1, (scp ? f.Scp : f.Sftp).Disposals);
                    }
                });
                test("Upload real " + (scp ? "SCP" : "SFTP") + " adapter honors pre-cancel without socket", () => {
                    using (var cancel = new CancellationTokenSource()) {
                        cancel.Cancel();
                        IInstallationUploadClient client = scp ? (IInstallationUploadClient)new ScpInstallationUploadClient(new ScpClient(new PasswordConnectionInfo("unused.invalid", "fixture", "inert"), RemotePathTransformation.ShellQuote)) : new SftpInstallationUploadClient(new InstallationSftpClient(new PasswordConnectionInfo("unused.invalid", "fixture", "inert")));
                        using (client) Throws<OperationCanceledException>(() => client.Connect(cancel.Token));
                    }
                });
            }
            foreach (LocalPackageFormat format in Enum.GetValues(typeof(LocalPackageFormat)))
            {
                LocalPackageFormat value = format;
                test("Upload actual shell path existence size SHA and symlink boundaries " + value, () => ShellBoundaries(value));
                test("Upload shared engine SFTP never creates SCP " + value, () => Fixture(value, path => {
                    var f = new Pipeline(value, "ok", false);
                    var result = new PluginInstallationService { PollInterval = TimeSpan.FromMilliseconds(1) }.RunAsync(f, Device(value), path,
                        () => Task.FromResult(new SoftwareEnvironmentResult { Status = SoftwareEnvironmentStatus.Ready }), s => Task.FromResult(true), null, CancellationToken.None).GetAwaiter().GetResult();
                    Equal(InstallStatus.SuccessVerified, result.Status); Equal(0, f.Transfers.ScpCreates); Equal(1, f.Transfers.Sftp.Uploads); Equal(1, f.Fake.Cleanups);
                }));
                test("Upload full workflow forwards fallback logs and cleanup " + value, () => Fixture(value, path => {
                    var pipeline = new Pipeline(value, "ok"); var logs = new List<InstallationWorkflowProgress>();
                    var result = new InstallationWorkflowService { Engine = new PluginInstallationService { PollInterval = TimeSpan.FromMilliseconds(1) } }
                        .RunAsync(new Workflow(pipeline), Device(value), path, new InlineProgress<InstallationWorkflowProgress>(logs.Add), CancellationToken.None).GetAwaiter().GetResult();
                    Equal(InstallationState.Success, result.State); Equal(1, pipeline.Fake.Starts); Equal(1, pipeline.Fake.Cleanups); Equal(1, pipeline.Transfers.ScpCreates);
                    True(logs.Any(l => l.Log != null && l.Log.Contains("SFTP 不可用，自动切换 SCP 上传")));
                    True(logs.Any(l => l.Log != null && l.Log.Contains("SCP 上传完成"))); True(result.Installation.CleanupSucceeded);
                }));
                foreach (string fault in new[] { "ok", "size", "hash", "missing", "nohash", "both", "cancel" })
                {
                    string failure = fault;
                    test("Upload shared engine " + value + " SCP " + failure, () => Fixture(value, path => {
                        var f = new Pipeline(value, failure); var logs = new List<InstallProgress>();
                        using (var cancel = new CancellationTokenSource()) {
                            if (failure == "cancel") f.Transfers.Scp.UploadHook = cancel.Cancel;
                            var r = new PluginInstallationService { PollInterval = TimeSpan.FromMilliseconds(1) }.RunAsync(f, Device(value), path,
                                () => Task.FromResult(new SoftwareEnvironmentResult { Status = SoftwareEnvironmentStatus.Ready }), s => Task.FromResult(true), new Progress(logs), cancel.Token).GetAwaiter().GetResult();
                            Equal(1, f.Transfers.ScpCreates); Equal(1, f.Transfers.Sftp.Disposals); Equal(1, f.Transfers.Scp.Disposals); Equal(1, f.Fake.Cleanups);
                            if (failure == "ok") { Equal(InstallStatus.SuccessVerified, r.Status); Equal(1, f.Fake.Starts); True(logs.Any(l => l.Log != null && l.Log.Contains("SCP 上传完成"))); True(logs.Any(l => l.Log != null && l.Log.Contains("上传文件 SHA256 校验通过"))); }
                            else { Equal(failure == "both" ? InstallStatus.FailedUpload : failure == "cancel" ? InstallStatus.Cancelled : InstallStatus.FailedIntegrity, r.Status); Equal(0, f.Fake.Starts); }
                            if (failure != "both" && failure != "cancel") Equal(remotePath(value, f.Remote), f.Transfers.Scp.Path);
                        }
                    }));
                }
            }
            foreach (bool trusted in new[] { false, true })
            {
                bool key = trusted;
                test("Upload actual SFTP initializer marks subsystem refusal only when trusted=" + key, () => {
                    using (var client = new InstallationSftpClient(new PasswordConnectionInfo("unused.invalid", "fixture", "inert"))) {
                        client.HostKeyAccepted = key; typeof(ConnectionInfo).GetProperty("IsAuthenticated").SetValue(client.ConnectionInfo, true, null);
                        InjectSession(client, "subsystem");
                        var error = Throws<Exception>(() => client.Connect()); Equal(key, error is SftpSubsystemUnavailableException);
                    }
                });
            }
            test("Upload actual SFTP initializer marks immediate channel close", () => {
                using (var client = new InstallationSftpClient(new PasswordConnectionInfo("unused.invalid", "fixture", "inert"))) {
                    client.HostKeyAccepted = true; typeof(ConnectionInfo).GetProperty("IsAuthenticated").SetValue(client.ConnectionInfo, true, null);
                    InjectSession(client, "channel"); Contains(Throws<SftpSubsystemUnavailableException>(() => client.Connect()).Message, "Channel was closed.");
                }
            });
            test("Upload actual SCP adapter streams fixed filename and quoted directory", () => {
                using (var client = new ScpClient(new PasswordConnectionInfo("unused.invalid", "fixture", "inert"), RemotePathTransformation.ShellQuote) { UseDirectoryFlag = true }) {
                    var peer = InjectSession(client, "scp"); var remote = RemoteTempSession.Create(LocalPackageFormat.Ipk); long sent = -1;
                    using (var adapter = new ScpInstallationUploadClient(client))
                    using (var input = new MemoryStream(Encoding.UTF8.GetBytes("payload"))) { adapter.Upload(input, remote.RunPath, count => sent = count, CancellationToken.None); }
                    Contains(peer.Channel.Command, "scp -t -d "); Contains(peer.Channel.Command, "'" + remote.Directory + "'");
                    True(peer.Channel.Writes.Any(b => Encoding.UTF8.GetString(b).Contains("package.ipk"))); Equal(7L, sent);
                }
            });
            test("Upload factories reuse endpoint credentials and reject changed or untrusted keys", TrustFactories);
            test("Upload actual SCP cancellation aborts blocked channel and joins before cleanup", () => {
                using (var cancel = new CancellationTokenSource())
                using (var client = new ScpClient(new PasswordConnectionInfo("unused.invalid", "fixture", "inert"), RemotePathTransformation.ShellQuote)) {
                    var peer = InjectSession(client, "scp-block");
                    using (var adapter = new ScpInstallationUploadClient(client))
                    using (var input = new MemoryStream(new byte[10])) {
                        var task = Task.Run(() => { try { adapter.Upload(input, RemoteTempSession.Create().RunPath, null, cancel.Token); } catch (Exception) { } });
                        True(peer.Entered.Wait(3000)); cancel.Cancel(); True(task.Wait(3000)); Equal(false, peer.Connected);
                    }
                }
            });
        }

        private static string remotePath(LocalPackageFormat format, RemoteTempSession remote)
        { return remote.Directory + (format == LocalPackageFormat.Ipk ? "/package.ipk" : format == LocalPackageFormat.Apk ? "/package.apk" : "/package.run"); }
        internal static RouterEnvironment Device(LocalPackageFormat format)
        { return new RouterEnvironment { IsConnected = true, Architecture = "x86_64", Version = "25.12.5", SdkSeries = "25.12", PackageManager = format == LocalPackageFormat.Ipk ? PackageManagerKind.OPKG : PackageManagerKind.APK, HostKeyFingerprint = "SHA256:fixture" }; }
        internal static void Fixture(LocalPackageFormat format, Action<string> action)
        {
            if (format != LocalPackageFormat.Run) { NativePackageTests.Fixture(format == LocalPackageFormat.Ipk ? "ipk" : "deflate", action); return; }
            string folder = Path.Combine(Path.GetTempPath(), "upload-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "PassWall_26.9.16_x86_64_all_sdk_25.12.run");
            try { File.WriteAllText(path, "inert fixture", new UTF8Encoding(false)); action(path); } finally { File.Delete(path); Directory.Delete(folder); }
        }
        private sealed class Progress : IProgress<InstallProgress>
        { private readonly List<InstallProgress> values; public Progress(List<InstallProgress> values) { this.values = values; } public void Report(InstallProgress value) { values.Add(value); } }
        internal sealed class Workflow : IInstallationWorkflowSession
        {
            private readonly Pipeline pipeline; private WorkflowTests.Fake Fake { get { return pipeline.Fake; } }
            public Workflow(Pipeline pipeline) { this.pipeline = pipeline; }
            public bool IsConnected { get { return Fake.IsConnected; } }
            public IPluginInstallSession Installer { get { return pipeline; } }
            public ISoftwareSourceSession Sources { get { return Fake; } }
            public RouterEnvironment ConnectAndDetect(RouterEnvironment expected, bool force, Action<string> log, CancellationToken token) { return Fake.ConnectAndDetect(expected, force, log, token); }
            public RouterReadiness ReadReadiness(IList<RepositoryFeed> feeds) { return Fake.ReadReadiness(feeds); }
            public bool PackageManagerBusy(PackageManagerKind manager) { return Fake.PackageManagerBusy(manager); }
            public bool WasLaunched(RemoteTempSession remote) { return Fake.WasLaunched(remote); }
            public ProbeResult RestoreOriginal(PackageManagerKind manager, string text, string backup) { return Fake.RestoreOriginal(manager, text, backup); }
            public bool DeleteBackup(string backup) { return Fake.DeleteBackup(backup); }
            public string Sanitize(string text) { return pipeline.Sanitize(text); }
        }
        internal sealed class Pipeline : IPluginInstallSession, IInstallationUploadSession, IInstallerMonitoringSession
        {
            public readonly WorkflowTests.Fake Fake = new WorkflowTests.Fake();
            public readonly Transfers Transfers;
            public RemoteTempSession Remote;
            public IPluginInstallSession MonitorSession;
            private readonly string fault;
            public Pipeline(LocalPackageFormat format, string fault, bool fallback = true) { Transfers = new Transfers(fallback); this.fault = fault; if (format == LocalPackageFormat.Ipk) Fake.MakeOpkg(); if (fault == "both") Transfers.Scp.UploadError = new ScpException("missing scp"); }
            public string Sanitize(string text) { return Transfers.Sanitize(text); }
            public long TmpAvailable() { return Fake.TmpAvailable(); }
            public void CreateDirectory(RemoteTempSession s) { Remote = s; Fake.CreateDirectory(s); }
            public void Upload(Stream source, RemoteTempSession s, Action<long> progress, CancellationToken token) { Upload(source, s, progress, null, token); }
            public void Upload(Stream source, RemoteTempSession s, Action<long> progress, Action<string> log, CancellationToken token) { Transfers.Uploader().Upload(source, s, progress, log, token); }
            private Client Active { get { return Transfers.ScpCreates > 0 ? Transfers.Scp : Transfers.Sftp; } }
            public long RemoteFileSize(RemoteTempSession s) { if (fault == "missing") throw new FileNotFoundException("missing remote file"); return Active.Bytes.Length + (fault == "size" ? 1 : 0); }
            public ProbeResult RemoteHash(RemoteTempSession s) { using (var data = new MemoryStream(Active.Bytes)) return new ProbeResult { ExitStatus = fault == "nohash" ? 127 : 0, Output = fault == "hash" ? new string('0', 64) : PluginPackageInspection.ComputeSha256(data) }; }
            public ProbeResult Inspect(RemoteTempSession s, bool check) { return Fake.Inspect(s, check); }
            public PackageSnapshot Snapshot(PackageManagerKind manager) { return Fake.Snapshot(manager); }
            public bool PackageExists(PackageManagerKind manager, string name) { return Fake.PackageExists(manager, name); }
            public void CheckRunner() { Fake.CheckRunner(); }
            public void StartInstaller(RemoteTempSession s) { Fake.StartInstaller(s); }
            public IDisposable OpenInstallerMonitor(RemoteTempSession s) { return MonitorSession == null ? Fake.OpenInstallerMonitor(s) : InstallerMonitoring.Open(MonitorSession, s); }
            public InstallerPoll PollInstaller(RemoteTempSession s, long stdout, long stderr) { var result = Fake.PollInstaller(s, stdout, stderr); return MonitorSession == null ? result : MonitorSession.PollInstaller(s, stdout, stderr); }
            public bool Cleanup(RemoteTempSession s) { Equal(1, Active.Disposals); return Fake.Cleanup(s); }
        }
        internal sealed class Transfers
        {
            public readonly Client Sftp = new Client(), Scp = new Client();
            public readonly List<string> Trace = new List<string>(), Log = new List<string>();
            public int SftpCreates, ScpCreates;
            public bool HomeConnected = true, UnsafeTarget;
            public Exception ScpFactoryError;
            public Transfers(bool fallback = false)
            {
                if (fallback) Sftp.ConnectError = new SftpSubsystemUnavailableException(new SshException("Channel was closed."));
                Sftp.DisposeHook = () => Trace.Add("sftp-dispose"); Scp.Trace = Trace;
            }
            public string Sanitize(string text) { return (text ?? "").Replace("test-secret", "[已隐藏]"); }
            public InstallationPackageUploader Uploader()
            { return new InstallationPackageUploader(() => { SftpCreates++; return Sftp; }, () => { ScpCreates++; Trace.Add("scp-create"); if (ScpFactoryError != null) throw ScpFactoryError; return Scp; }, () => HomeConnected, Sanitize, () => { Trace.Add("target-check"); if (UnsafeTarget) throw new IOException("目标非空"); }); }
            public void Run(CancellationToken token = default(CancellationToken))
            { using (var input = new MemoryStream(Encoding.UTF8.GetBytes("payload 中文"))) { input.Position = 3; Uploader().Upload(input, RemoteTempSession.Create(), bytes => { }, Log.Add, token); } }
        }
        internal sealed class Client : IInstallationUploadClient
        {
            public Exception ConnectError, UploadError;
            public Action UploadHook, DisposeHook;
            public List<string> Trace;
            public int Uploads, Disposals;
            public byte[] Bytes;
            public string Path;
            public void Connect(CancellationToken token) { token.ThrowIfCancellationRequested(); if (Trace != null) Trace.Add("scp-connect"); if (ConnectError != null) throw ConnectError; }
            public void Upload(Stream source, string path, Action<long> progress, CancellationToken token)
            {
                Uploads++; Path = path; if (Trace != null) Trace.Add("scp-upload"); if (UploadError != null) throw UploadError;
                using (var output = new MemoryStream()) { source.CopyTo(output); Bytes = output.ToArray(); }
                if (progress != null) progress(Bytes.Length); if (UploadHook != null) UploadHook(); token.ThrowIfCancellationRequested();
            }
            public void Dispose() { Disposals++; if (Trace != null) Trace.Add("scp-dispose"); if (DisposeHook != null) DisposeHook(); }
        }

        private static void TrustFactories()
        {
            byte[] bytes = { 1, 2, 3 }; string fingerprint;
            using (var sha = SHA256.Create()) fingerprint = "SHA256:" + Convert.ToBase64String(sha.ComputeHash(bytes)).TrimEnd('=');
            using (var service = new SshService()) {
                typeof(SshService).GetField("connectionFactory", Private).SetValue(service, new Func<PasswordConnectionInfo>(() => new PasswordConnectionInfo("router.fixture", 2222, "user-fixture", "test-secret")));
                bool first; var trust = (HostKeyTrustStore)typeof(SshService).GetField("trustStore", Private).GetValue(service); trust.TryTrust("router.fixture", 2222, fingerprint, out first);
                foreach (string method in new[] { "CreateTrustedInstallationTransfer", "CreateTrustedScp" }) {
                    using (var client = (BaseClient)typeof(SshService).GetMethod(method, Private).Invoke(service, new object[] { fingerprint }))
                    using (client.ConnectionInfo as IDisposable) {
                        Equal("router.fixture", client.ConnectionInfo.Host); Equal(2222, client.ConnectionInfo.Port); Equal("user-fixture", client.ConnectionInfo.Username); Equal(1, client.ConnectionInfo.AuthenticationMethods.Count);
                        True(client.ConnectionInfo.AuthenticationMethods[0] is PasswordAuthenticationMethod);
                        var handlers = (EventHandler<HostKeyEventArgs>)typeof(BaseClient).GetField("HostKeyReceived", Private).GetValue(client);
                        var same = Key(bytes); handlers(client, same); True(same.CanTrust);
                        var changed = Key(new byte[] { 9 }); handlers(client, changed); Equal(false, changed.CanTrust);
                        trust = new HostKeyTrustStore(); typeof(SshService).GetField("trustStore", Private).SetValue(service, trust);
                        var unknown = Key(bytes); handlers(client, unknown); Equal(false, unknown.CanTrust);
                        trust.TryTrust("router.fixture", 2222, fingerprint, out first);
                        var scp = client as ScpClient; if (scp != null) { True(scp.UseDirectoryFlag); Equal(RemotePathTransformation.ShellQuote, scp.RemotePathTransformation); }
                    }
                }
            }
        }
        private static HostKeyEventArgs Key(byte[] bytes)
        { var args = (HostKeyEventArgs)FormatterServices.GetUninitializedObject(typeof(HostKeyEventArgs)); typeof(HostKeyEventArgs).GetField("<HostKey>k__BackingField", Private).SetValue(args, bytes); return args; }

        private static void ShellBoundaries(LocalPackageFormat format)
        {
            string folder = Path.Combine(Path.GetTempPath(), "upload-shell-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try {
                string unix = "/" + char.ToLowerInvariant(folder[0]) + folder.Substring(2).Replace('\\', '/');
                var remote = RemoteTempSession.Create(format); string root = unix + "/remote", dir = root + "/" + remote.Id, path = dir + "/" + remote.PackageFileName;
                string target = PluginInstallCommands.ScpTargetCheck(remote).Replace(RemoteTempSession.Root, root);
                string exists = PluginInstallCommands.UploadedFileCheck(remote).Replace(RemoteTempSession.Root, root);
                string size = PluginInstallCommands.FileSize(remote).Replace(RemoteTempSession.Root, root), hash = PluginInstallCommands.Hash(remote).Replace(RemoteTempSession.Root, root);
                var script = new StringBuilder("set -eu\n"); script.AppendLine("mkdir -p " + ShellArgument.Quote(dir));
                script.Append(Expect(target, 0)); script.Append(Expect(exists, 44));
                script.AppendLine("printf abc > " + ShellArgument.Quote(path)); script.Append(Expect(target, 1)); script.Append(Expect(exists, 0));
                script.AppendLine("[ \"$(" + size + ")\" = 3 ]");
                using (var bytes = new MemoryStream(Encoding.ASCII.GetBytes("abc"))) script.AppendLine("[ \"$(" + hash + " | cut -d ' ' -f 1)\" = " + ShellArgument.Quote(PluginPackageInspection.ComputeSha256(bytes)) + " ]");
                script.AppendLine("rm -- " + ShellArgument.Quote(path)); script.AppendLine("ln -s absent " + ShellArgument.Quote(path));
                script.Append(Expect(target, 1)); script.Append(Expect(exists, 2)); script.AppendLine("rm -- " + ShellArgument.Quote(path));
                script.AppendLine("mkdir -- " + ShellArgument.Quote(path)); script.Append(Expect(target, 1)); script.Append(Expect(exists, 2)); script.AppendLine("rmdir -- " + ShellArgument.Quote(path));
                script.AppendLine("rmdir -- " + ShellArgument.Quote(dir)); script.AppendLine("ln -s " + ShellArgument.Quote(unix) + " " + ShellArgument.Quote(dir));
                script.Append(Expect(target, 1)); script.Append(Expect(exists, 2)); script.AppendLine("rm -- " + ShellArgument.Quote(dir));
                script.AppendLine("rmdir -- " + ShellArgument.Quote(root)); script.AppendLine("ln -s " + ShellArgument.Quote(unix) + " " + ShellArgument.Quote(root));
                script.Append(Expect(target, 1)); script.Append(Expect(exists, 2)); script.AppendLine("rm -- " + ShellArgument.Quote(root));
                string file = Path.Combine(folder, "test.sh"); File.WriteAllText(file, script.ToString().Replace("\r\n", "\n"), new UTF8Encoding(false));
                var start = new ProcessStartInfo { FileName = @"C:\Program Files\Git\bin\bash.exe", Arguments = "--noprofile --norc \"" + file.Replace('\\', '/') + "\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.EnvironmentVariables["MSYS"] = "winsymlinks:nativestrict";
                using (var process = Process.Start(start)) { if (!process.WaitForExit(5000)) { process.Kill(); throw new Exception("Upload shell test timed out."); } string error = process.StandardError.ReadToEnd(); if (process.ExitCode != 0) throw new Exception("Shell exit " + process.ExitCode + ": " + error); }
            }
            finally { Directory.Delete(folder, true); }
        }
        private static string Expect(string command, int exit)
        { return "set +e\n( " + command + " )\nrc=$?\nset -e\n[ \"$rc\" -eq " + exit + " ] || { echo 'Expected exit " + exit + " got' \"$rc\" >&2; exit 91; }\n"; }

        // Real SSH.NET clients/protocol code, with in-memory session/channel peers; no sockets or device writes.
        private static SessionPeer InjectSession(BaseClient client, string mode)
        {
            var field = typeof(BaseClient).GetField("<Session>k__BackingField", Private); var peer = new SessionPeer(field.FieldType, mode, client.ConnectionInfo); field.SetValue(client, peer.GetTransparentProxy()); return peer;
        }
        private sealed class SessionPeer : RealProxy
        {
            public ChannelPeer Channel;
            private readonly string mode; private readonly ConnectionInfo info;
            public bool Connected = true;
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim();
            public SessionPeer(Type type, string mode, ConnectionInfo info) : base(type) { this.mode = mode; this.info = info; }
            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message; object result = null;
                if (call.MethodName == "get_IsConnected") result = Connected;
                else if (call.MethodName == "get_ConnectionInfo") result = info;
                else if (call.MethodName == "get_SessionLoggerFactory") result = Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance;
                else if (call.MethodName == "CreateChannelSession") { Channel = new ChannelPeer(((MethodInfo)call.MethodBase).ReturnType, mode, Entered); result = Channel.GetTransparentProxy(); }
                else if (call.MethodName == "Dispose" || call.MethodName == "Disconnect") { Connected = false; if (Channel != null) Channel.Close(); }
                else if (!call.MethodName.StartsWith("add_", StringComparison.Ordinal) && !call.MethodName.StartsWith("remove_", StringComparison.Ordinal) && !call.MethodName.StartsWith("OnDisconnect", StringComparison.Ordinal))
                    return new ReturnMessage(new InvalidOperationException("Offline session forbids " + call.MethodName), call);
                return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
            }
        }
        private sealed class ChannelPeer : RealProxy
        {
            public string Command;
            public readonly List<byte[]> Writes = new List<byte[]>();
            private readonly string mode;
            private readonly ManualResetEventSlim entered;
            private readonly Dictionary<string, Delegate> events = new Dictionary<string, Delegate>();
            public ChannelPeer(Type type, string mode, ManualResetEventSlim entered) : base(type) { this.mode = mode; this.entered = entered; }
            public void Close() { Fire("Closed", Args("ChannelEventArgs", (uint)0)); }
            private void Fire(string name, object args) { Delegate handler; if (events.TryGetValue(name, out handler) && handler != null) handler.DynamicInvoke(GetTransparentProxy(), args); }
            private void Ack() { Fire("DataReceived", Args("ChannelDataEventArgs", (uint)0, new ArraySegment<byte>(new byte[] { 0 }))); }
            private static object Args(string name, params object[] values)
            { return Activator.CreateInstance(typeof(BaseClient).Assembly.GetType("Renci.SshNet.Common." + name, true), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, values, null); }
            public override IMessage Invoke(IMessage message)
            {
                var call = (IMethodCallMessage)message; object result = null;
                if (call.MethodName.StartsWith("add_", StringComparison.Ordinal) || call.MethodName.StartsWith("remove_", StringComparison.Ordinal)) {
                    string name = call.MethodName.Substring(call.MethodName.IndexOf('_') + 1); Delegate old; events.TryGetValue(name, out old);
                    events[name] = call.MethodName.StartsWith("add_", StringComparison.Ordinal) ? Delegate.Combine(old, (Delegate)call.Args[0]) : Delegate.Remove(old, (Delegate)call.Args[0]);
                }
                else if (call.MethodName == "get_IsOpen") result = true;
                else if (call.MethodName == "Open") { }
                else if (call.MethodName == "Dispose") Close();
                else if (call.MethodName == "SendSubsystemRequest") result = mode == "channel";
                else if (call.MethodName == "SendExecRequest") { Command = (string)call.Args[0]; result = true; entered.Set(); if (mode != "scp-block") Ack(); }
                else if (call.MethodName == "SendData" && mode == "channel") Close();
                else if (call.MethodName == "SendData" && mode == "scp") {
                    var bytes = (byte[])call.Args[0]; int offset = call.Args.Length > 1 ? (int)call.Args[1] : 0; int count = call.Args.Length > 2 ? (int)call.Args[2] : bytes.Length;
                    Writes.Add(bytes.Skip(offset).Take(count).ToArray()); Ack();
                }
                else return new ReturnMessage(new InvalidOperationException("Offline channel forbids " + call.MethodName), call);
                return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
            }
        }
        private static T Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T error) { return error; } throw new Exception("Expected " + typeof(T).Name); }
        private static void Contains(string text, string fragment) { if (!text.Contains(fragment)) throw new Exception("Missing " + fragment + " in " + text); }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    }
}
