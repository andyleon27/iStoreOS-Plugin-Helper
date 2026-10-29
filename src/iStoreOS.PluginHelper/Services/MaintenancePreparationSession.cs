using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // Preparation remains independently testable without execution capability.
    public interface IMaintenancePreparationSession
    {
        bool IsConnected { get; }
        string Sanitize(string text);
        Task CreateWorkspaceAsync(RemoteMaintenanceWorkspace workspace, CancellationToken token);
        Task UploadAsync(string localPath, RemoteMaintenanceWorkspace workspace, string fileName, CancellationToken token);
        Task<string> ReadHashAsync(RemoteMaintenanceWorkspace workspace, string fileName, CancellationToken token);
        Task<bool> CleanupAsync(RemoteMaintenanceWorkspace workspace, CancellationToken token);
    }
    internal sealed partial class SshMaintenancePreparationSession : IMaintenancePreparationSession
    {
        private readonly SshClient ssh;
        private readonly Func<SftpClient> transfer;
        private readonly Func<string, string> sanitize;
        public SshMaintenancePreparationSession(SshClient ssh, Func<SftpClient> transfer, Func<string, string> sanitize)
        { this.ssh = ssh; this.transfer = transfer; this.sanitize = sanitize; }
        public bool IsConnected { get { return ssh != null && ssh.IsConnected; } }
        public string Sanitize(string text) { return sanitize(text ?? ""); }
        public async Task CreateWorkspaceAsync(RemoteMaintenanceWorkspace workspace, CancellationToken token)
        { Require(await CommandAsync(workspace.CreateCommand(), token).ConfigureAwait(false)); }
        public async Task UploadAsync(string localPath, RemoteMaintenanceWorkspace workspace, string fileName, CancellationToken token)
        {
            string path = workspace.FilePath(fileName);
            token.ThrowIfCancellationRequested();
            var client = transfer();
            using (var settings = client.ConnectionInfo as IDisposable)
            using (client)
            {
                client.OperationTimeout = TimeSpan.FromSeconds(20);
                await client.ConnectAsync(token).ConfigureAwait(false);
                using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    await client.UploadFileAsync(input, path, false, null, token).ConfigureAwait(false);
            }
        }
        public async Task<string> ReadHashAsync(RemoteMaintenanceWorkspace workspace, string fileName, CancellationToken token)
        { return Require(await CommandAsync(workspace.HashCommand(fileName), token).ConfigureAwait(false)); }
        public async Task<bool> CleanupAsync(RemoteMaintenanceWorkspace workspace, CancellationToken token)
        { return (await CommandAsync(workspace.CleanupCommand(), token).ConfigureAwait(false)).ExitStatus == 0; }
        private async Task<ProbeResult> CommandAsync(string text, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!IsConnected) throw new IOException("SSH 已断开，无法完成脚本准备或清理。");
            using (var command = ssh.CreateCommand(text))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(15);
                await command.ExecuteAsync(token).ConfigureAwait(false);
                return new ProbeResult { ExitStatus = command.ExitStatus ?? -1, Output = command.Result, Error = Sanitize(command.Error) };
            }
        }
        private static string Require(ProbeResult result)
        { if (result.ExitStatus != 0) throw new IOException("维护准备远端命令失败，exit " + result.ExitStatus + ": " + result.Error); return result.Output; }
    }
}
