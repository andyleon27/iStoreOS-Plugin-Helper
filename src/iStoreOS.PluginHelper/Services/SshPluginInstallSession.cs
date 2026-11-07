using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    internal sealed class SshPluginInstallSession : IPluginInstallSession, IInstallerMonitoringSession, IInstallationUploadSession
    {
        private readonly Func<SshClient> active;
        private readonly Func<SftpClient> createTransfer;
        private readonly Func<ScpClient> createScp;
        private readonly Func<string, string> redact;
        private IDisposable monitor;
        private Func<long, long, InstallerPoll> poll;
        private string monitorDirectory;
        public SshPluginInstallSession(SshClient ssh, Func<SftpClient> createTransfer, Func<string, string> redact)
            : this(ssh, createTransfer, null, redact) { }
        public SshPluginInstallSession(SshClient ssh, Func<SftpClient> createTransfer, Func<ScpClient> createScp, Func<string, string> redact)
            : this(() => ssh, createTransfer, createScp, redact) { }
        public SshPluginInstallSession(Func<SshClient> active, Func<SftpClient> createTransfer, Func<ScpClient> createScp, Func<string, string> redact)
        { this.active = active; this.createTransfer = createTransfer; this.createScp = createScp; this.redact = redact; }
        public string Sanitize(string text) { return redact(text ?? ""); }
        public long TmpAvailable() { return PluginPackageInspection.ParseTmpAvailable(Require(Execute(PluginInstallCommands.TmpSpace, 10)).Output); }
        public void CreateDirectory(RemoteTempSession s) { Require(Execute(s.CreateCommand(), 10)); }
        public void Upload(Stream source, RemoteTempSession s, Action<long> progress, CancellationToken token)
        { Upload(source, s, progress, null, token); }
        public void Upload(Stream source, RemoteTempSession s, Action<long> progress, Action<string> log, CancellationToken token)
        {
            new InstallationPackageUploader(() => new SftpInstallationUploadClient(createTransfer()),
                () => { if (createScp == null) throw new InvalidOperationException("受信任 SCP 工厂不可用。"); return new ScpInstallationUploadClient(createScp()); },
                () => active() != null && active().IsConnected, Sanitize,
                () => Require(Execute(PluginInstallCommands.ScpTargetCheck(s), 10), "SCP 目标独占检查")).Upload(source, s, progress, log, token);
        }
        public long RemoteFileSize(RemoteTempSession s)
        {
            var exists = Execute(PluginInstallCommands.UploadedFileCheck(s), 10);
            if (exists.ExitStatus == 44) throw new FileNotFoundException("远端本轮安装包不存在。", s.RunPath);
            Require(exists, "上传文件存在性 / 路径检查");
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
                return poll(stdoutOffset, stderrOffset);
            }
            using (OpenInstallerMonitor(s)) return poll(stdoutOffset, stderrOffset);
        }
        public IDisposable OpenInstallerMonitor(RemoteTempSession s)
        {
            if (monitor != null) throw new InvalidOperationException("已有安装监测 scope。");
            if (s.SftpUnavailable)
            {
                var reader = new SshInstallerMonitor(s, text => SshInstallerMonitor.Execute(active, text));
                monitor = reader; poll = reader.Poll;
            }
            else
            {
                var reader = new PersistentInstallerMonitor(() => new TrustedFileSession(createTransfer()), s);
                monitor = reader; poll = reader.Poll;
            }
            monitorDirectory = s.Directory;
            return InstallerMonitoring.ReleaseOnDispose(monitor, () => { monitor = null; monitorDirectory = null; poll = null; },
                s.SftpUnavailable ? "安装监测：SSH fallback（设备无 SFTP subsystem）" : "安装监测：SFTP", !s.SftpUnavailable);
        }
        private sealed class TrustedFileSession : IConnectedInstallerFileSession
        {
            private readonly SftpClient client;
            private readonly IDisposable settings;
            public TrustedFileSession(SftpClient client) { this.client = client; settings = client.ConnectionInfo as IDisposable; }
            public void Connect() { client.OperationTimeout = TimeSpan.FromSeconds(15); SshConnectionFactory.Connect(client); }
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
            try {
            using (var command = active().CreateCommand(text))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(seconds);
                string output = command.Execute(); object status = command.ExitStatus;
                return new ProbeResult { ExitStatus = status == null ? -1 : Convert.ToInt32(status), Output = output, Error = command.Error };
            }
            } catch (Exception error) { SshConnectionFactory.Failure(active(), error); throw; }
        }
    }
}
