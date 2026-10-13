using System;
using System.Collections.Generic;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    internal sealed class SshSoftwareSourceSession : ISoftwareSourceSession
    {
        private readonly SshClient ssh;
        private readonly Func<string, string> sanitize;
        public SshSoftwareSourceSession(SshClient ssh, Func<string, string> sanitize) { this.ssh = ssh; this.sanitize = sanitize; }
        public string Sanitize(string text) { return sanitize(text ?? ""); }
        public ProbeResult ReadOfficial(PackageManagerKind manager, bool rom) { return Execute(SoftwareSourceCommands.ReadOfficial(manager, rom), 8); }
        public IDictionary<string, string> ReadAdditional(PackageManagerKind manager)
        {
            ProbeResult paths = Execute(SoftwareSourceCommands.ListAdditional(manager), 8);
            if (paths.ExitStatus != 0) throw new InvalidOperationException("无法枚举辅助 / 自定义源配置。");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in (paths.Output ?? "").Split('\n'))
            {
                string path = raw.Trim();
                if (!SoftwareSourceCommands.IsAdditionalPath(manager, path)) continue;
                ProbeResult content = Execute(SoftwareSourceCommands.ReadAdditional(manager, path), 8);
                if (content.ExitStatus != 0) throw new InvalidOperationException("无法读取 " + path);
                result[path] = content.Output;
            }
            return result;
        }
        public ProbeResult QuickStartGet(bool list)
        { return Execute(ReadOnlyRouterCommands.Get(list ? RouterProbe.QuickStartSources : RouterProbe.QuickStartCurrentSource), 8); }
        public ProbeResult QuickStartSwitch(string identity) { return Execute(SoftwareSourceCommands.QuickStartSwitch(identity), 25); }
        public ProbeResult ProbeIndex(string url) { return Execute(SoftwareSourceCommands.ProbeIndex(url), 20); }
        public PackageUpdateResult Update(PackageManagerKind manager)
        {
            using (SshCommand command = ssh.CreateCommand(SoftwareSourceCommands.Update(manager)))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(125);
                try
                {
                    string output = command.Execute(); object status = command.ExitStatus;
                    return new PackageUpdateResult { ExitCode = status == null ? -1 : Convert.ToInt32(status), Stdout = output, Stderr = command.Error };
                }
                catch (SshOperationTimeoutException)
                {
                    return new PackageUpdateResult { ExitCode = -1, TimedOut = true, Stdout = command.Result,
                        Stderr = (command.Error ?? "") + "\nPackage update timeout（SSH 命令上限 125 秒）。" };
                }
            }
        }
        public string BackupOfficial(PackageManagerKind manager)
        {
            ProbeResult result = Execute(SoftwareSourceCommands.BackupOfficial(manager), 8);
            if (result.ExitStatus != 0) throw new InvalidOperationException("原始官方源备份失败，禁止切换。");
            string path = (result.Output ?? "").Trim(); SoftwareSourceCommands.ValidateBackupPath(path); return path;
        }
        public ProbeResult ReadBackup(string backupPath) { return Execute(SoftwareSourceCommands.ReadBackup(backupPath), 8); }
        public ProbeResult RestoreBackup(PackageManagerKind manager, string backupPath) { return Execute(SoftwareSourceCommands.RestoreBackup(manager, backupPath), 8); }
        private ProbeResult Execute(string commandText, int seconds)
        {
            using (SshCommand command = ssh.CreateCommand(commandText))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(seconds);
                string output = command.Execute(); object status = command.ExitStatus;
                return new ProbeResult { ExitStatus = status == null ? -1 : Convert.ToInt32(status), Output = output, Error = command.Error };
            }
        }
    }
}
