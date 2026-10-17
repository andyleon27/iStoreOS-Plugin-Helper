using System;
using System.IO;
using System.Net.Sockets;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public interface IConnectedInstallerFileSession : IInstallerFileSession { bool IsConnected { get; } }
    public interface IInstallerMonitoringSession { IDisposable OpenInstallerMonitor(RemoteTempSession session); }
    public interface IInstallerMonitorStatistics { int ConnectionCount { get; } }
    public static class InstallerMonitoring
    {
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
        public static IDisposable Open(IPluginInstallSession session, RemoteTempSession remote)
        { var monitored = session as IInstallerMonitoringSession; return monitored == null ? new EmptyScope() : monitored.OpenInstallerMonitor(remote); }
        internal static IDisposable ReleaseOnDispose(IDisposable scope, Action release) { return new Lease(scope, release); }
        private sealed class Lease : IDisposable, IInstallerMonitorStatistics
        {
            private readonly IDisposable scope; private readonly Action release; private bool disposed;
            public Lease(IDisposable scope, Action release) { this.scope = scope; this.release = release; }
            public int ConnectionCount { get { var statistics = scope as IInstallerMonitorStatistics; return statistics == null ? 0 : statistics.ConnectionCount; } }
            public void Dispose() { if (disposed) return; disposed = true; try { scope.Dispose(); } finally { release(); } }
        }
    }
    // One instance per monitor scope; the only reconnect operation is a trusted file-session factory.
    public sealed class PersistentInstallerMonitor : IDisposable, IInstallerMonitorStatistics
    {
        private readonly Func<IInstallerFileSession> factory;
        private readonly RemoteTempSession remote;
        private IInstallerFileSession client;
        private bool disposed;
        public int ConnectionCount { get; private set; }
        public PersistentInstallerMonitor(Func<IInstallerFileSession> factory, RemoteTempSession remote)
        {
            if (factory == null || remote == null) throw new ArgumentNullException();
            RemoteTempSession.ValidateDirectory(remote.Directory); this.factory = factory; this.remote = remote;
        }
        public InstallerPoll Poll(long stdoutOffset, long stderrOffset)
        {
            if (disposed) throw new ObjectDisposedException("PersistentInstallerMonitor");
            if (stdoutOffset < 0 || stderrOffset < 0) throw new ArgumentException("日志 offset 不得为负数。");
            int before = ConnectionCount;
            EnsureConnected();
            try { return Annotate(new SftpInstallerPoller().PollConnected(client, remote, stdoutOffset, stderrOffset), before); }
            catch (Exception error)
            {
                if (!IsConnectionFailure(error)) throw;
                Close(); EnsureConnected(); // At most one retry, same session and unchanged caller-owned offsets.
                try { return Annotate(new SftpInstallerPoller().PollConnected(client, remote, stdoutOffset, stderrOffset), before); }
                catch { Close(); throw; }
            }
        }
        private InstallerPoll Annotate(InstallerPoll poll, int before)
        {
            if (ConnectionCount > 1 && ConnectionCount > before)
                poll.Diagnostic = (string.IsNullOrEmpty(poll.Diagnostic) ? "" : poll.Diagnostic + "；") + "SFTP 监测已通过原受信任工厂重连；继续同一 session / byte offset，未重新启动安装器。";
            return poll;
        }
        private void EnsureConnected()
        {
            var connected = client as IConnectedInstallerFileSession;
            if (client != null && (connected == null || connected.IsConnected)) return;
            Close();
            client = factory();
            if (client == null) throw new IOException("SFTP 日志连接工厂未返回连接。");
            try { client.Connect(); ConnectionCount++; }
            catch { Close(); throw; } // Authentication / host-key failures never downgrade or loop.
        }
        private static bool IsConnectionFailure(Exception error)
        {
            return error is SshConnectionException || error is SshOperationTimeoutException || error is SocketException ||
                error is EndOfStreamException || (error.InnerException != null && IsConnectionFailure(error.InnerException));
        }
        private void Close() { var old = client; client = null; if (old != null) old.Dispose(); }
        public void Dispose() { if (disposed) return; disposed = true; Close(); }
    }
}
