using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Renci.SshNet;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Services
{
    internal sealed class SshInstallationWorkflowSession : IInstallationWorkflowSession
    {
        private readonly Func<SshClient> active;
        private readonly Func<RouterEnvironment, bool, Action<string>, CancellationToken, RouterEnvironment> connect;
        private readonly Func<SftpClient> transfer;
        private readonly Func<string, string> redact;
        public SshInstallationWorkflowSession(Func<SshClient> active,
            Func<RouterEnvironment, bool, Action<string>, CancellationToken, RouterEnvironment> connect,
            Func<SftpClient> transfer, Func<string, string> redact)
        { this.active = active; this.connect = connect; this.transfer = transfer; this.redact = redact; }
        public bool IsConnected { get { var client = active(); return client != null && client.IsConnected; } }
        public string Sanitize(string text) { return redact(text ?? ""); }
        public RouterEnvironment ConnectAndDetect(RouterEnvironment expected, bool force, Action<string> log, CancellationToken token)
        { return connect(expected, force, log, token); }
        public IPluginInstallSession Installer { get { return new SshPluginInstallSession(active(), transfer, redact); } }
        public ISoftwareSourceSession Sources { get { return new SshSoftwareSourceSession(active(), redact); } }
        private ProbeResult Execute(string text, int seconds)
        {
            using (var command = active().CreateCommand(text))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(seconds); string output = command.Execute(); object status = command.ExitStatus;
                return new ProbeResult { ExitStatus = status == null ? -1 : Convert.ToInt32(status), Output = output, Error = command.Error };
            }
        }
        private string Require(string text, int seconds)
        {
            var result = Execute(text, seconds); if (result.ExitStatus != 0) throw new IOException("只读检查失败：" + Sanitize(result.Error)); return result.Output;
        }
        public RouterReadiness ReadReadiness(IList<RepositoryFeed> feeds)
        {
            long epoch; if (!long.TryParse(Require(InstallationReadiness.Time, 8).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out epoch)) throw new IOException("路由器时间不可读。");
            string disks = Require(InstallationReadiness.PersistentSpace, 8);
            var root = new List<string>(); var overlay = new List<string>();
            foreach (string line in disks.Split('\n')) { if (line.TrimEnd().EndsWith(" /overlay", StringComparison.Ordinal)) overlay.Add(line); else if (line.TrimEnd().EndsWith(" /", StringComparison.Ordinal)) root.Add(line); }
            string space = "Filesystem 1024-blocks Used Available Capacity Mounted on\n" + (overlay.Count > 0 ? overlay.Last() : root.Last());
            var value = new RouterReadiness { DefaultRoute = InstallationReadiness.HasRoute(Require(InstallationReadiness.Routes, 8)), UnixTime = epoch,
                TmpBytes = Installer.TmpAvailable(), PersistentBytes = PluginPackageInspection.ParseTmpAvailable(space), PersistentReadOnly = InstallationReadiness.IsReadOnly(Require(InstallationReadiness.PersistentMounts, 8)) };
            foreach (string host in feeds.Where(f => f.IndexUrl != null).Select(f => new Uri(f.IndexUrl).Host).Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
            {
                string dns = Require(InstallationReadiness.DnsCommand(host), 8).Trim(); if (dns == "UNKNOWN") continue;
                value.DnsChecked++; if (dns == "0") value.DnsResolved++;
            }
            return value;
        }
        public bool PackageManagerBusy(PackageManagerKind manager)
        {
            string result = Require(InstallationReadiness.Busy, 8).Trim();
            if (result.StartsWith("BUSY_INSTALLER:", StringComparison.Ordinal))
                throw new InstallationPolicyException(InstallationFailureKind.PackageManagerBusy, "发现尚无 exit.code 的旧安装 session，未启动第二个安装器：" + result.Substring(15));
            if (result != "BUSY" && result != "IDLE") throw new IOException("包管理器活动状态不可读。"); return result == "BUSY";
        }
        public bool WasLaunched(RemoteTempSession remote)
        {
            RemoteTempSession.ValidateDirectory(remote.Directory);
            var result = Execute("test -d " + ShellArgument.Quote(remote.Directory + "/launch.once"), 8);
            if (result.ExitStatus != 0 && result.ExitStatus != 1) throw new IOException("启动标记不可读。"); return result.ExitStatus == 0;
        }
        public ProbeResult RestoreOriginal(PackageManagerKind manager, string text, string backupPath)
        {
            if (!string.IsNullOrEmpty(backupPath))
            {
                var backup = Sources.ReadBackup(backupPath);
                if (backup.ExitStatus == 0 && backup.Output == text) return Sources.RestoreBackup(manager, backupPath);
            }
            return Execute(InstallationReadiness.RestoreCommand(manager, text), 10);
        }
        public bool DeleteBackup(string backupPath)
        {
            SoftwareSourceCommands.ValidateBackupPath(backupPath);
            string folder = backupPath.Substring(0, backupPath.LastIndexOf('/'));
            // No recursive delete: only our validated backup and its now-empty directory.
            return Execute("test ! -L " + ShellArgument.Quote(folder) + " && rm -f -- " + ShellArgument.Quote(backupPath) + " && rmdir -- " + ShellArgument.Quote(folder), 10).ExitStatus == 0;
        }
    }
}
