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
                var shell = ssh.CreateShellStream("xterm-256color", columns, rows, 0, 0, 8192);
                try
                {
                    var transport = new SshShellTransport(shell, (width, height) => shell.ChangeWindowSize(width, height, 0, 0), () => IsConnected);
                    shell.ErrorOccurred += (sender, args) => transport.ReportFailure(args.Exception);
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
