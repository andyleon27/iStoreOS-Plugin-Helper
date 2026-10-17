using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    internal sealed class SshPluginInstallSession : IPluginInstallSession, IInstallerMonitoringSession
    {
        private readonly SshClient ssh;
        private readonly Func<SftpClient> createTransfer;
        private readonly Func<string, string> redact;
        private PersistentInstallerMonitor monitor;
        private string monitorDirectory;
        public SshPluginInstallSession(SshClient ssh, Func<SftpClient> createTransfer, Func<string, string> redact)
        { this.ssh = ssh; this.createTransfer = createTransfer; this.redact = redact; }
        public string Sanitize(string text) { return redact(text ?? ""); }
        public long TmpAvailable() { return PluginPackageInspection.ParseTmpAvailable(Require(Execute(PluginInstallCommands.TmpSpace, 10)).Output); }
        public void CreateDirectory(RemoteTempSession s) { Require(Execute(s.CreateCommand(), 10)); }
        public void Upload(Stream source, RemoteTempSession s, Action<long> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var transfer = createTransfer();
            using (var settings = transfer.ConnectionInfo as IDisposable)
            using (transfer)
            {
                transfer.OperationTimeout = TimeSpan.FromSeconds(60);
                // Only SFTP in V0.3. Authentication/host-key/network failures never trigger a weaker fallback.
                try { transfer.Connect(); }
                catch (SshException ex)
                { throw new IOException("SFTP 连接 / subsystem 不可用；未启用 SCP fallback。" + Sanitize(ex.Message), ex); }
                token.ThrowIfCancellationRequested();
                source.Position = 0;
                // UploadFile's legacy callback runs on the ThreadPool. Use the cancellable
                // task API and an inline IProgress instead of throwing from that callback.
                transfer.UploadFileAsync(source, s.RunPath, false, new InlineUploadProgress(progress), token).GetAwaiter().GetResult();
                token.ThrowIfCancellationRequested();
            }
        }
        private sealed class InlineUploadProgress : IProgress<UploadFileProgressReport>
        {
            private readonly Action<long> report;
            public InlineUploadProgress(Action<long> report) { this.report = report; }
            public void Report(UploadFileProgressReport value) { if (report != null) report(checked((long)value.TotalBytesUploaded)); }
        }
        public long RemoteFileSize(RemoteTempSession s)
        {
            long size;
            if (!long.TryParse(Require(Execute(PluginInstallCommands.FileSize(s), 10)).Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out size))
                throw new InvalidDataException("远程文件大小不可读。");
            return size;
        }
        public ProbeResult RemoteHash(RemoteTempSession s) { return Execute(PluginInstallCommands.Hash(s), 120); }
        public ProbeResult Inspect(RemoteTempSession s, bool check) { return Execute(PluginInstallCommands.Inspect(s, check), check ? 180 : 30); }
        public PackageSnapshot Snapshot(PackageManagerKind manager)
        { return PackageSnapshotService.Parse(Require(Execute(PluginInstallCommands.Snapshot(manager), 30), "读取 " + manager + " 软件包快照").Output, manager); }
        public bool PackageExists(PackageManagerKind manager, string name)
        {
            ProbeResult result = Execute(PluginInstallCommands.Query(manager, name), 15);
            return PackageSnapshotService.QueryReportsInstalled(result, manager, name);
        }
        public void CheckRunner() { Require(Execute(PluginInstallCommands.RunnerCheck, 10), "后台安装 shell HUP 检查"); }
        public void StartInstaller(RemoteTempSession s) { Require(Execute(PluginInstallCommands.Launch(s), 15)); }
        public InstallerPoll PollInstaller(RemoteTempSession s, long stdoutOffset, long stderrOffset)
        {
            if (monitor != null)
            {
                if (s.Directory != monitorDirectory) throw new InvalidOperationException("监测 session 不匹配。");
                return monitor.Poll(stdoutOffset, stderrOffset);
            }
            using (OpenInstallerMonitor(s)) return monitor.Poll(stdoutOffset, stderrOffset);
        }
        public IDisposable OpenInstallerMonitor(RemoteTempSession s)
        {
            if (monitor != null) throw new InvalidOperationException("已有安装监测 scope。");
            monitor = new PersistentInstallerMonitor(() => new TrustedFileSession(createTransfer()), s); monitorDirectory = s.Directory;
            return InstallerMonitoring.ReleaseOnDispose(monitor, () => { monitor = null; monitorDirectory = null; });
        }
        private sealed class TrustedFileSession : IConnectedInstallerFileSession
        {
            private readonly SftpClient client;
            private readonly IDisposable settings;
            public TrustedFileSession(SftpClient client) { this.client = client; settings = client.ConnectionInfo as IDisposable; }
            public void Connect() { client.OperationTimeout = TimeSpan.FromSeconds(15); client.Connect(); }
            public bool IsConnected { get { return client.IsConnected; } }
            public bool Exists(string path) { return client.Exists(path); }
            public Stream OpenRead(string path)
            { try { return client.OpenRead(path); } catch (SftpPathNotFoundException ex) { throw new FileNotFoundException("SFTP 文件不存在。", path, ex); } }
            public void Dispose() { try { client.Dispose(); } finally { if (settings != null) settings.Dispose(); } }
        }
        public bool Cleanup(RemoteTempSession s)
        { return Execute(s.CleanupCommand(), 20).ExitStatus == 0 && Execute(PluginInstallCommands.Exists(s), 10).ExitStatus == 0; }
        private static ProbeResult Require(ProbeResult result, string operation = "远程操作")
        {
            if (result.ExitStatus != 0) throw new IOException(operation + "失败（exit " + result.ExitStatus + "）：" + result.Error);
            return result;
        }
        private ProbeResult Execute(string text, int seconds)
        {
            using (var command = ssh.CreateCommand(text))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(seconds);
                string output = command.Execute(); object status = command.ExitStatus;
                return new ProbeResult { ExitStatus = status == null ? -1 : Convert.ToInt32(status), Output = output, Error = command.Error };
            }
        }
    }
}
