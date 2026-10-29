using System;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public interface IMaintenanceMenuSession : IMaintenancePreparationSession
    {
        Task<IShellTransport> OpenTerminalAsync(uint columns, uint rows, CancellationToken token);
        Task<ProbeResult> ExecuteMenuCommandAsync(string command, CancellationToken token);
    }

    public sealed partial class PluginMaintenanceExecutionService
    {
        // Unlike PrepareMenuAsync, this keeps the same verified workspace alive through execution.
        public async Task<MaintenanceMenuRunResult> RunMenuAsync(IMaintenanceMenuSession session,
            Action<MaintenanceMenuTerminal> terminalReady, IProgress<MaintenanceOperationLog> progress,
            CancellationToken token, string commit = null, uint columns = 100, uint rows = 30, Func<Tuple<uint, uint>> terminalSize = null, MaintenanceNetworkPath? networkPath = null, int? localProxyPort = null)
        {
            if (terminalReady == null) throw new ArgumentNullException("terminalReady");
            if (commit != null) ThirdPartyScriptCatalog.ValidateCommit(commit);
            MaintenanceMenuTerminal.ValidateSize(columns, rows);
            var run = new MenuRunContext(terminalReady, columns, rows, terminalSize, networkPath);
            IThirdPartyScriptSource scoped = source;
            if (localProxyPort.HasValue) {
                ComputerProxyPolicy.ValidatePort(localProxyPort.Value);
                if (networkPath != MaintenanceNetworkPath.ComputerProxy) throw new ArgumentException("Windows proxy requires the computer proxy route.");
                var windows = source as ThirdPartyScriptSource; if (windows != null) scoped = windows.WithLocalHttpProxy(localProxyPort.Value);
            }
            var preparation = await ProcessMenuAsync(session, progress, token, commit, run, scoped).ConfigureAwait(false);
            return new MaintenanceMenuRunResult { Preparation = preparation, Runtime = run.Runtime };
        }

        private sealed class MenuRunContext
        {
            public readonly Guid RunId = Guid.NewGuid();
            public readonly MaintenanceNetworkPath? NetworkPath;
            private readonly Action<MaintenanceMenuTerminal> ready;
            private readonly uint columns, rows;
            private readonly Func<Tuple<uint, uint>> terminalSize;
            private MaintenanceMenuTerminal terminal;
            public bool DispatchAttempted { get { return terminal != null && terminal.DispatchAttempted; } }
            public MaintenanceMenuRuntimeInfo Runtime { get { return terminal == null ? null : terminal.Snapshot; } }
            public MenuRunContext(Action<MaintenanceMenuTerminal> ready, uint columns, uint rows, Func<Tuple<uint, uint>> terminalSize, MaintenanceNetworkPath? networkPath)
            { this.ready = ready; this.columns = columns; this.rows = rows; this.terminalSize = terminalSize; NetworkPath = networkPath; }

            public async Task ExecuteAsync(IMaintenanceMenuSession session, RemoteMaintenanceWorkspace workspace, CancellationToken token)
            {
                // Preparation may take seconds while the window is resized. Read only
                // the latest display geometry here, immediately before PTY creation.
                var size = terminalSize == null ? Tuple.Create(columns, rows) : terminalSize();
                if (size == null) throw new InvalidOperationException("无法读取终端尺寸。");
                MaintenanceMenuTerminal.ValidateSize(size.Item1, size.Item2);
                IShellTransport transport = await session.OpenTerminalAsync(size.Item1, size.Item2, token).ConfigureAwait(false);
                if (transport == null) throw new InvalidOperationException("PTY 通道创建失败。");
                terminal = new MaintenanceMenuTerminal(workspace, RunId, transport);
                Exception failure = null;
                using (token.Register(terminal.RequestClose))
                {
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        ready(terminal); // Bind output before the reader and the short command start.
                        token.ThrowIfCancellationRequested();
                        terminal.Start();
                        await terminal.Completion.ConfigureAwait(false);
                    }
                    catch (Exception ex) { failure = ex; }
                    if (failure != null)
                    {
                        try { await terminal.CloseAsync().ConfigureAwait(false); } catch (Exception) { }
                        terminal.CompleteFailure(failure);
                        throw failure;
                    }
                }
            }

            public async Task<bool> CleanupAsync(IMaintenanceMenuSession session, RemoteMaintenanceWorkspace workspace, CancellationToken token)
            {
                // ExecuteAsync has joined the reader and writer before this can run.
                if (!Runtime.ChannelClosed) return false;
                ProbeResult state = null;
                Exception failure = null;
                try { state = await session.ExecuteMenuCommandAsync(MaintenanceMenuCommands.ReadStateCommand(workspace, RunId), token).ConfigureAwait(false); }
                catch (Exception ex) { failure = ex; }
                terminal.Protocol.ApplyRemoteState(state);
                if (failure != null) throw failure;
                if (!Runtime.CanCleanup) return false;
                var cleaned = await session.ExecuteMenuCommandAsync(MaintenanceMenuCommands.CleanupFinishedCommand(workspace, RunId), token).ConfigureAwait(false);
                return terminal.Protocol.ConfirmCleanup(cleaned);
            }

            public void ApplyResult(MaintenancePreparationResult result)
            {
                if (!DispatchAttempted) return;
                var runtime = Runtime;
                result.ExitCode = runtime.ExitCode;
                switch (runtime.Status)
                {
                    case MaintenanceMenuRunStatus.ExitedNormally:
                        result.Status = MaintenancePreparationStatus.Completed; result.Error = null; break;
                    case MaintenanceMenuRunStatus.ExitedWithError:
                        result.Status = MaintenancePreparationStatus.ScriptFailed;
                        result.Error = "菜单进程退出码：" + runtime.ExitCode + "。"; break;
                    default:
                        result.Status = MaintenancePreparationStatus.FinalStateUnknown;
                        result.Error = "菜单最终状态未知；保留远端任务目录：" + result.RemoteDirectory; break;
                }
            }
        }
    }
}
