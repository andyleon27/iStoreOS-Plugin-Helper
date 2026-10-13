using System;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class SshService : IDisposable
    {
        private readonly object sync = new object();
        private readonly HostKeyTrustStore trustStore = new HostKeyTrustStore();
        private SshClient client;
        private SshClient pendingClient;
        private bool disposed;
        private bool connecting;
        private bool softwareBusy;
        private Func<string, string> sanitizeLog = text => text ?? "";
        private Func<PasswordConnectionInfo> connectionFactory;
        private readonly InstallationWorkflowService installWorkflow = new InstallationWorkflowService();
        public bool CanReconnect { get { lock (sync) return !disposed && connectionFactory != null; } }
        public bool HasPendingInstallation { get { return installWorkflow.HasPendingWork; } }
        public bool HasPendingInstaller { get { return installWorkflow.HasPendingInstaller; } }

        public Task<InstallationWorkflowResult> InstallOneClickAsync(RouterEnvironment expected, string filePath,
            IProgress<InstallationWorkflowProgress> progress, CancellationToken token)
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("SshService");
                if (connecting || softwareBusy) throw new InvalidOperationException("已有操作正在进行。");
                if (connectionFactory == null) throw new InvalidOperationException("请先连接路由器。");
                softwareBusy = true;
            }
            return Task.Run(async () =>
            {
                try
                {
                    var session = new SshInstallationWorkflowSession(() => client, ReconnectForInstallation,
                        () => CreateTrustedTransfer(expected.HostKeyFingerprint), text => sanitizeLog(text));
                    return await installWorkflow.RunAsync(session, expected, filePath, progress, token);
                }
                finally { lock (sync) softwareBusy = false; }
            });
        }
        private SftpClient CreateTrustedTransfer(string fingerprint)
        {
            var transfer = new SftpClient(connectionFactory()) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            transfer.HostKeyReceived += (sender, args) =>
            {
                string actual;
                using (SHA256 hash = SHA256.Create()) actual = "SHA256:" + Convert.ToBase64String(hash.ComputeHash(args.HostKey)).TrimEnd('=');
                args.CanTrust = actual == fingerprint && trustStore.IsTrusted(transfer.ConnectionInfo.Host, transfer.ConnectionInfo.Port, actual);
            };
            return transfer;
        }
        private RouterEnvironment ReconnectForInstallation(RouterEnvironment expected, bool force, Action<string> log, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (force || !IsConnected)
            {
                Func<PasswordConnectionInfo> settings;
                lock (sync) { if (disposed || connectionFactory == null) throw new InvalidOperationException("当前会话认证信息不可用。"); settings = connectionFactory; }
                var replacement = new SshClient(settings()) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                bool keyRejected = false;
                replacement.HostKeyReceived += (sender, args) =>
                {
                    string fingerprint;
                    using (SHA256 hash = SHA256.Create()) fingerprint = "SHA256:" + Convert.ToBase64String(hash.ComputeHash(args.HostKey)).TrimEnd('=');
                    args.CanTrust = fingerprint == expected.HostKeyFingerprint && trustStore.IsTrusted(replacement.ConnectionInfo.Host, replacement.ConnectionInfo.Port, fingerprint);
                    keyRejected = !args.CanTrust;
                    log("重连 Host Key：" + fingerprint + (args.CanTrust ? "；受信任设备一致。" : "；拒绝身份变化。"));
                };
                try
                {
                    lock (sync) { if (disposed) throw new OperationCanceledException(); pendingClient = replacement; }
                    replacement.Connect(); token.ThrowIfCancellationRequested();
                    SshClient previous;
                    lock (sync) { if (disposed) throw new OperationCanceledException(); previous = client; client = replacement; pendingClient = null; }
                    if (previous != null) previous.Dispose();
                }
                catch
                {
                    lock (sync) pendingClient = null; replacement.Dispose();
                    if (keyRejected) throw new InstallationPolicyException(InstallationFailureKind.IdentityChanged, "SSH 指纹变化，拒绝自动恢复。"); throw;
                }
            }
            var read = new SshReadOnlySession(client, token);
            RouterEnvironment actual = new RouterDetectionService().Detect(read, log, token);
            actual.HostKeyFingerprint = expected.HostKeyFingerprint;
            actual.QuickStartAvailable = new QuickStartDetectionService().Detect(read, log, token);
            return actual;
        }

        public bool IsConnected
        {
            get { lock (sync) { return !disposed && client != null && client.IsConnected; } }
        }

        public Task<ConnectionResult> ConnectAndDetectAsync(string host, string username, string password,
            IProgress<string> progress, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("SshService");
                if (connecting || softwareBusy) throw new InvalidOperationException("连接或软件环境检测正在进行。");
                connecting = true;
            }
            // All synchronous SSH.NET network operations run on a worker thread.
            return Task.Run(() => ConnectAndDetect(host, username, password, progress, cancellationToken));
        }

        private ConnectionResult ConnectAndDetect(string host, string username, string password,
            IProgress<string> progress, CancellationToken cancellationToken)
        {
            Action<string> log = text => { if (progress != null) progress.Report(LogHelper.Redact(text, password)); };
            SshClient current = null;
            bool hostKeyChanged = false;
            bool sshConnected = false;
            string fingerprint = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SshClient previous;
                lock (sync) { previous = client; client = null; connectionFactory = null; sanitizeLog = text => text ?? ""; }
                if (previous != null) previous.Dispose();
                log("开始连接：" + host + ":22（密码认证；仅执行只读检测）。");
                var connection = new PasswordConnectionInfo(host, 22, username, password)
                { Timeout = TimeSpan.FromSeconds(8) };
                current = new SshClient(connection) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                current.HostKeyReceived += (sender, args) =>
                {
                    using (SHA256 hash = SHA256.Create())
                        fingerprint = "SHA256:" + Convert.ToBase64String(hash.ComputeHash(args.HostKey)).TrimEnd('=');
                    bool firstSeen;
                    args.CanTrust = trustStore.TryTrust(host, 22, fingerprint, out firstSeen);
                    hostKeyChanged = !args.CanTrust;
                    log("SSH Host Key：" + args.HostKeyName + "；Fingerprint：" + fingerprint);
                    log(hostKeyChanged ? "服务器密钥与本次运行中已信任的记录不同，拒绝连接。" :
                        (firstSeen ? "首次信任此指纹（仅在本次程序运行中记录）。" : "服务器指纹与已信任记录一致。"));
                };
                lock (sync)
                {
                    if (disposed) throw new OperationCanceledException();
                    pendingClient = current;
                }
                current.Connect();
                sshConnected = true;
                cancellationToken.ThrowIfCancellationRequested();
                log("SSH 连接成功。");
                var session = new SshReadOnlySession(current, cancellationToken);
                RouterEnvironment environment = new RouterDetectionService().Detect(session, log, cancellationToken);
                environment.HostKeyFingerprint = fingerprint;
                environment.QuickStartAvailable = new QuickStartDetectionService().Detect(session, log, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!current.IsConnected) throw new SshConnectionException("SSH session closed during detection.");
                lock (sync)
                {
                    if (disposed) throw new OperationCanceledException();
                    client = current;
                    sanitizeLog = text => LogHelper.RedactPreservingLines(text, password);
                    connectionFactory = () => new PasswordConnectionInfo(host, 22, username, password) { Timeout = TimeSpan.FromSeconds(8) };
                    pendingClient = null;
                    current = null; // The service owns the live session until reconnect/close.
                }
                log("只读环境检测完成。");
                return new ConnectionResult { Success = true, Environment = environment, UserMessage = "已连接" };
            }
            catch (Exception exception)
            {
                ConnectionError error = cancellationToken.IsCancellationRequested || disposed ? ConnectionError.Cancelled :
                    (hostKeyChanged ? ConnectionError.HostKeyChanged : ClassifyError(exception));
                if (sshConnected && error == ConnectionError.Other) error = ConnectionError.EnvironmentDetectionFailed;
                log("连接检测失败：" + error + "；" + LogHelper.ExceptionDetails(exception, password));
                return new ConnectionResult { Error = error, UserMessage = GetUserMessage(error) };
            }
            finally
            {
                lock (sync) { pendingClient = null; connecting = false; }
                if (current != null) current.Dispose();
            }
        }

        public static ConnectionError ClassifyError(Exception exception)
        {
            for (Exception item = exception; item != null; item = item.InnerException)
            {
                if (item is SshAuthenticationException) return ConnectionError.AuthenticationFailure;
                if (item is SshOperationTimeoutException || item is TimeoutException) return ConnectionError.Timeout;
                var socket = item as SocketException;
                if (socket != null)
                    return socket.SocketErrorCode == SocketError.TimedOut ? ConnectionError.Timeout : ConnectionError.NetworkUnreachable;
                if (item is OperationCanceledException) return ConnectionError.Cancelled;
            }
            return exception is SshException ? ConnectionError.SshProtocolError : ConnectionError.Other;
        }

        public Task<SoftwareEnvironmentResult> CheckSoftwareEnvironmentAsync(RouterEnvironment environment,
            IProgress<SoftwareProgress> progress, Func<SoftwareRepairPlan, Task<bool>> confirmRepair, CancellationToken cancellationToken)
        {
            SshClient active;
            Func<string, string> redact;
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("SshService");
                if (connecting || softwareBusy) throw new InvalidOperationException("检测正在进行。");
                if (installWorkflow.HasPendingWork) throw new InvalidOperationException("上轮安装器状态或原软件源恢复尚未确认；请先点击再次尝试处理旧流程。");
                if (client == null || !client.IsConnected) throw new InvalidOperationException("请先连接路由器。");
                active = client; redact = sanitizeLog; softwareBusy = true;
            }
            return Task.Run(async () =>
            {
                try
                {
                    return await new SoftwareEnvironmentService().RunAsync(new SshSoftwareSourceSession(active, redact), environment,
                        progress, confirmRepair, cancellationToken);
                }
                finally { lock (sync) { softwareBusy = false; } }
            });
        }

        public Task<PluginInstallResult> InstallPluginAsync(RouterEnvironment environment, string filePath,
            IProgress<InstallProgress> progress, IProgress<SoftwareProgress> softwareProgress,
            Func<SoftwareRepairPlan, Task<bool>> confirmRepair, Func<InstallConfirmation, Task<bool>> confirmInstall, CancellationToken token)
        {
            SshClient active; Func<string, string> redact; Func<PasswordConnectionInfo> settings;
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("SshService");
                if (connecting || softwareBusy) throw new InvalidOperationException("已有连接、检测或安装操作正在进行。");
                if (client == null || !client.IsConnected || connectionFactory == null) throw new InvalidOperationException("请先连接路由器。");
                active = client; redact = sanitizeLog; settings = connectionFactory; softwareBusy = true;
            }
            return Task.Run(async () =>
            {
                try
                {
                    // Re-read the current environment through the same authenticated SSH session.
                    RouterEnvironment current = new RouterDetectionService().Detect(new SshReadOnlySession(active, token),
                        text => { if (progress != null) progress.Report(new InstallProgress { Step = "安装前环境复核", Log = redact(text) }); }, token);
                    current.HostKeyFingerprint = environment.HostKeyFingerprint;
                    Func<SftpClient> transfer = () =>
                    {
                        var sftp = new SftpClient(settings()) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                        sftp.HostKeyReceived += (sender, args) =>
                        {
                            string fingerprint;
                            using (SHA256 hash = SHA256.Create()) fingerprint = "SHA256:" + Convert.ToBase64String(hash.ComputeHash(args.HostKey)).TrimEnd('=');
                            args.CanTrust = fingerprint == current.HostKeyFingerprint && trustStore.IsTrusted(sftp.ConnectionInfo.Host, sftp.ConnectionInfo.Port, fingerprint);
                            if (progress != null) progress.Report(new InstallProgress { Step = "2/7 上传插件", Log = "SFTP Host Key：" + fingerprint + (args.CanTrust ? "；与已连接 SSH 一致。" : "；拒绝不一致的指纹。") });
                        };
                        return sftp;
                    };
                    return await new PluginInstallationService().RunAsync(new SshPluginInstallSession(active, transfer, redact), current, filePath,
                        () => new SoftwareEnvironmentService().RunAsync(new SshSoftwareSourceSession(active, redact), current, softwareProgress, confirmRepair, token),
                        confirmInstall, progress, token);
                }
                finally { lock (sync) { softwareBusy = false; } }
            });
        }

        private static string GetUserMessage(ConnectionError error)
        {
            switch (error)
            {
                case ConnectionError.AuthenticationFailure: return "用户名或密码错误";
                case ConnectionError.NetworkUnreachable: return "无法连接到路由器，请检查地址和网络";
                case ConnectionError.Timeout: return "连接或检测超时，请检查路由器是否可达";
                case ConnectionError.SshProtocolError: return "SSH 协议错误，请查看详细日志";
                case ConnectionError.HostKeyChanged: return "服务器 SSH 密钥已变化，已拒绝连接；请核对设备";
                case ConnectionError.EnvironmentDetectionFailed: return "SSH 已连接，但环境检测失败，请查看日志";
                case ConnectionError.Cancelled: return "连接检测已取消";
                default: return "连接检测失败，请查看详细日志";
            }
        }

        public void Dispose()
        {
            SshClient active;
            SshClient pending;
            lock (sync)
            {
                disposed = true;
                active = client;
                pending = pendingClient;
                client = null;
                pendingClient = null;
                sanitizeLog = text => text ?? "";
                connectionFactory = null;
            }
            if (active != null) active.Dispose();
            if (pending != null) pending.Dispose();
        }

        private sealed class SshReadOnlySession : IReadOnlyRouterSession
        {
            private readonly SshClient ssh;
            private readonly CancellationToken cancellationToken;
            public SshReadOnlySession(SshClient ssh, CancellationToken cancellationToken)
            { this.ssh = ssh; this.cancellationToken = cancellationToken; }

            public ProbeResult Execute(RouterProbe probe)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (SshCommand command = ssh.CreateCommand(ReadOnlyRouterCommands.Get(probe)))
                {
                    command.CommandTimeout = TimeSpan.FromSeconds(6);
                    string output = command.Execute();
                    cancellationToken.ThrowIfCancellationRequested();
                    object status = command.ExitStatus;
                    return new ProbeResult
                    {
                        ExitStatus = status == null ? -1 : Convert.ToInt32(status),
                        Output = output,
                        Error = command.Error
                    };
                }
            }
        }
    }
}
