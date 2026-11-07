using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace iStoreOS.PluginHelper.Services
{
    internal sealed partial class SshMaintenancePreparationSession : IMaintenanceMenuSession
    {
        public Task<IShellTransport> OpenTerminalAsync(uint columns, uint rows, CancellationToken token)
        {
            MaintenanceMenuTerminal.ValidateSize(columns, rows);
            return Task.Run<IShellTransport>(() => {
                token.ThrowIfCancellationRequested();
                if (!IsConnected) throw new IOException("SSH 已断开，无法创建菜单 PTY。");
                Renci.SshNet.ShellStream shell;
                try { shell = ssh.CreateShellStream("xterm-256color", columns, rows, 0, 0, 8192); }
                catch (Exception error) { SshConnectionFactory.Failure(ssh, error); throw; }
                try
                {
                    var transport = new SshShellTransport(shell, (width, height) => shell.ChangeWindowSize(width, height, 0, 0), () => IsConnected);
                    shell.ErrorOccurred += (sender, args) => { SshConnectionFactory.Failure(ssh, args.Exception); transport.ReportFailure(args.Exception); };
                    token.ThrowIfCancellationRequested();
                    return transport;
                }
                catch { shell.Dispose(); throw; }
            });
        }
        public Task<ProbeResult> ExecuteMenuCommandAsync(string command, CancellationToken token)
        { return CommandAsync(command, token); }
    }
}
