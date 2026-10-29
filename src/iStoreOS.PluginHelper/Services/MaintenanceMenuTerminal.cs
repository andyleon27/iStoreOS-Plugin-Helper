using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // Owns the shell channel only. Authentication and the parent SSH connection are borrowed.
    public sealed class MaintenanceMenuTerminal : IInteractiveTerminalSession
    {
        private readonly InteractiveTerminalSession session;
        private readonly MaintenanceMenuProtocol protocol;
        private readonly RemoteMaintenanceWorkspace workspace;
        private int closeRequested, inputReady, dispatchAttempted;
        public event Action<string> Output;
        public MaintenanceMenuRuntimeInfo Snapshot { get { return protocol.Snapshot; } }
        public bool IsInputReady { get { return Volatile.Read(ref inputReady) == 1 && !Snapshot.ChannelClosed && Volatile.Read(ref closeRequested) == 0; } }
        internal bool DispatchAttempted { get { return Volatile.Read(ref dispatchAttempted) != 0; } }
        internal Task Completion { get { return session.Completion; } }
        internal MaintenanceMenuProtocol Protocol { get { return protocol; } }

        internal MaintenanceMenuTerminal(RemoteMaintenanceWorkspace workspace, Guid runId, IShellTransport transport)
        {
            this.workspace = workspace;
            protocol = new MaintenanceMenuProtocol(workspace, runId);
            session = new InteractiveTerminalSession(transport);
            session.Output += text => {
                protocol.Feed(text);
                var handlers = Output;
                if (handlers != null) foreach (Action<string> handler in handlers.GetInvocationList())
                    try { handler(text); } catch (Exception) { } // A closing view must not interrupt the reader.
            };
            session.Closed += error => protocol.Complete(Volatile.Read(ref closeRequested) != 0 ?
                new OperationCanceledException("菜单终端已关闭；最终状态需远端复核。") : error);
        }
        internal void Start()
        {
            if (Volatile.Read(ref closeRequested) != 0) throw new OperationCanceledException();
            protocol.Begin();
            session.Start();
            // Once queued, dispatch may have happened even if the network fails before START arrives.
            Interlocked.Exchange(ref dispatchAttempted, 1);
            if (!session.Send(MaintenanceMenuCommands.StartPayload(workspace))) throw new IOException("无法发送菜单启动命令。");
            Volatile.Write(ref inputReady, 1);
        }
        public bool Send(string text) { return IsInputReady && session.Send(text); }
        public bool Resize(uint columns, uint rows)
        {
            ValidateSize(columns, rows);
            return IsInputReady && session.Resize(columns, rows);
        }
        public Task CloseAsync()
        {
            Interlocked.Exchange(ref closeRequested, 1);
            Volatile.Write(ref inputReady, 0);
            return session.StopAsync();
        }
        internal void RequestClose()
        {
            // Cancellation callbacks must not synchronously wait for network I/O or stream readers.
            CloseAsync().ContinueWith(task => { var observed = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        internal void CompleteFailure(Exception error) { protocol.Complete(error); }
        internal static void ValidateSize(uint columns, uint rows)
        { if (columns == 0 || rows == 0 || columns > 1000 || rows > 1000) throw new ArgumentOutOfRangeException("columns", "PTY size must be between 1 and 1000."); }
    }
}
