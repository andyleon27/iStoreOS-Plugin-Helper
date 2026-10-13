using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    internal sealed class SshPluginInstallSession : IPluginInstallSession
    {
        private readonly SshClient ssh;
        private readonly Func<SftpClient> createTransfer;
        private readonly Func<string, string> redact;
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
        { return PackageSnapshotService.Parse(Require(Execute(PluginInstallCommands.Snapshot(manager), 30)).Output, manager); }
        public bool PackageExists(PackageManagerKind manager, string name)
        {
            ProbeResult result = Execute(PluginInstallCommands.Query(manager, name), 15);
            return PackageSnapshotService.QueryReportsInstalled(result, manager, name);
        }
        public void CheckRunner() { Require(Execute("command -v nohup >/dev/null 2>&1 && command -v base64 >/dev/null 2>&1 && command -v dd >/dev/null 2>&1", 10)); }
        public void StartInstaller(RemoteTempSession s) { Require(Execute(PluginInstallCommands.Launch(s), 15)); }
        public InstallerPoll PollInstaller(RemoteTempSession s, long stdoutOffset, long stderrOffset)
        {
            string state = Require(Execute(PluginInstallCommands.ReadExit(s), 10)).Output.Trim();
            int exit;
            if (state != "RUNNING" && (!int.TryParse(state, NumberStyles.None, CultureInfo.InvariantCulture, out exit) || exit > 255))
                throw new InvalidDataException("安装器结束状态不可读。");
            return new InstallerPoll
            {
                ExitCode = state == "RUNNING" ? (int?)null : int.Parse(state, CultureInfo.InvariantCulture),
                Stdout = Convert.FromBase64String(Require(Execute(PluginInstallCommands.ReadLog(s, false, stdoutOffset), 15)).Output.Trim()),
                Stderr = Convert.FromBase64String(Require(Execute(PluginInstallCommands.ReadLog(s, true, stderrOffset), 15)).Output.Trim())
            };
        }
        public bool Cleanup(RemoteTempSession s)
        { return Execute(s.CleanupCommand(), 20).ExitStatus == 0 && Execute(PluginInstallCommands.Exists(s), 10).ExitStatus == 0; }
        private static ProbeResult Require(ProbeResult result)
        {
            if (result.ExitStatus != 0) throw new IOException("远程操作失败（exit " + result.ExitStatus + "）：" + result.Error);
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
