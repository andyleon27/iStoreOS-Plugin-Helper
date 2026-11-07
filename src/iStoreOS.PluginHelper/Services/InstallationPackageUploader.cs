using System;
using System.IO;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace iStoreOS.PluginHelper.Services
{
    // Only local package uploads use this boundary. Menu transfers stay unchanged.
    internal interface IInstallationUploadClient : IDisposable
    {
        void Connect(CancellationToken token);
        void Upload(Stream source, string path, Action<long> progress, CancellationToken token);
    }

    internal sealed class SftpSubsystemUnavailableException : IOException
    {
        public SftpSubsystemUnavailableException(Exception error) : base(error.Message, error) { }
    }

    internal static class InstallationUploadPolicy
    {
        public static bool IsUnavailable(Exception error, bool authenticated, bool trusted, bool transportFailed)
        {
            // Exact library messages, exact exception type, and no wrapped failures. In particular,
            // SshConnectionException / SshAuthenticationException / timeout must never downgrade.
            if (!authenticated || !trusted || transportFailed || error == null ||
                error.GetType() != typeof(SshException) || error.InnerException != null) return false;
            return error.Message == "Channel was closed." || error.Message == "Subsystem 'sftp' could not be executed.";
        }
    }

    internal sealed class InstallationSftpClient : SftpClient
    {
        public bool HostKeyAccepted { get; set; }
        private volatile bool transportFailed;
        public InstallationSftpClient(ConnectionInfo info) : base(info)
        { ErrorOccurred += (sender, args) => transportFailed = true; }
        protected override void OnConnected()
        {
            // BaseClient invokes this only after the SSH transport is authenticated.
            // SftpClient.OnConnected creates/initializes the SFTP subsystem; file writes have not begun.
            try { base.OnConnected(); }
            catch (Exception error)
            {
                if (InstallationUploadPolicy.IsUnavailable(error, ConnectionInfo.IsAuthenticated, HostKeyAccepted, transportFailed))
                    throw new SftpSubsystemUnavailableException(error);
                throw;
            }
        }
    }

    internal sealed class InstallationPackageUploader
    {
        private readonly Func<IInstallationUploadClient> sftp, scp;
        private readonly Func<bool> connected;
        private readonly Func<string, string> sanitize;
        private readonly Action prepareScp;
        public InstallationPackageUploader(Func<IInstallationUploadClient> sftp, Func<IInstallationUploadClient> scp,
            Func<bool> connected, Func<string, string> sanitize, Action prepareScp)
        { this.sftp = sftp; this.scp = scp; this.connected = connected; this.sanitize = sanitize; this.prepareScp = prepareScp; }

        public void Upload(Stream source, RemoteTempSession remote, Action<long> progress, Action<string> log, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (source == null || !source.CanRead || !source.CanSeek) throw new ArgumentException("安装包必须是可读取、可定位的锁定文件流。");
            if (remote == null) throw new ArgumentNullException("remote");
            RemoteTempSession.ValidateDirectory(remote.Directory);
            string sftpFailure = null;
            try
            {
                using (var client = sftp())
                {
                    try { client.Connect(token); }
                    catch (SftpSubsystemUnavailableException error) { sftpFailure = Describe(error.InnerException ?? error); }
                    if (sftpFailure == null)
                    {
                        token.ThrowIfCancellationRequested(); source.Position = 0;
                        client.Upload(source, remote.RunPath, progress, token);
                        token.ThrowIfCancellationRequested(); remote.SftpUnavailable = false; Report(log, "SFTP 上传完成。"); return;
                    }
                } // Dispose/join the failed transfer before any fallback or remote cleanup.
            }
            catch (Exception error)
            {
                token.ThrowIfCancellationRequested();
                if (error is OperationCanceledException) throw;
                throw new IOException("SFTP 上传失败：" + Describe(error));
            }
            token.ThrowIfCancellationRequested();
            remote.SftpUnavailable = true; // Only the typed, authenticated subsystem initialization failure above reaches here.
            if (!connected()) throw new IOException("SFTP 初始化失败：" + sftpFailure + "；首页 SSH 已断开，禁止 SCP fallback。");
            Report(log, "SFTP 不可用，自动切换 SCP 上传；SFTP 初始化诊断：" + sftpFailure);
            try
            {
                prepareScp(); // Refuse an existing target (including dangling symlinks) before SCP can overwrite it.
                token.ThrowIfCancellationRequested();
                using (var client = scp())
                {
                    token.ThrowIfCancellationRequested(); client.Connect(token);
                    token.ThrowIfCancellationRequested(); source.Position = 0;
                    client.Upload(source, remote.RunPath, progress, token);
                    token.ThrowIfCancellationRequested();
                }
                Report(log, "SCP 上传完成。");
            }
            catch (Exception error)
            {
                token.ThrowIfCancellationRequested();
                if (error is OperationCanceledException) throw;
                throw new IOException("安装包上传失败；SFTP 初始化：" + sftpFailure + "；SCP：" + Describe(error));
            }
        }
        private string Describe(Exception error)
        { return sanitize(error.GetType().Name + "：" + error.Message); }
        private void Report(Action<string> log, string text) { if (log != null) log(sanitize(text)); }
    }

    internal sealed class SftpInstallationUploadClient : IInstallationUploadClient
    {
        private readonly SftpClient client;
        private readonly IDisposable settings;
        public SftpInstallationUploadClient(SftpClient client) { this.client = client; settings = client.ConnectionInfo as IDisposable; client.OperationTimeout = TimeSpan.FromSeconds(60); }
        public void Connect(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (token.Register(Abort)) SshConnectionFactory.ConnectAsync(client, token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
        }
        public void Upload(Stream source, string path, Action<long> progress, CancellationToken token)
        {
            try { using (token.Register(Abort))
                client.UploadFileAsync(source, path, false, new InlineProgress(progress), token).GetAwaiter().GetResult(); }
            catch (Exception error) { SshConnectionFactory.Failure(client, error); throw; }
        }
        private sealed class InlineProgress : IProgress<UploadFileProgressReport>
        {
            private readonly Action<long> report;
            public InlineProgress(Action<long> report) { this.report = report; }
            public void Report(UploadFileProgressReport value) { if (report != null) report(checked((long)value.TotalBytesUploaded)); }
        }
        private void Abort() { try { client.Dispose(); } catch { /* Cancellation callback must not escape on a ThreadPool thread. */ } }
        public void Dispose() { try { client.Dispose(); } finally { if (settings != null) settings.Dispose(); } }
    }

    internal sealed class ScpInstallationUploadClient : IInstallationUploadClient
    {
        private readonly ScpClient client;
        private readonly IDisposable settings;
        public ScpInstallationUploadClient(ScpClient client) { this.client = client; settings = client.ConnectionInfo as IDisposable; client.OperationTimeout = TimeSpan.FromSeconds(60); }
        public void Connect(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (token.Register(Abort)) SshConnectionFactory.ConnectAsync(client, token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
        }
        public void Upload(Stream source, string path, Action<long> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            EventHandler<ScpUploadEventArgs> handler = (sender, args) => { if (progress != null) progress(args.Uploaded); };
            client.Uploading += handler;
            try { using (token.Register(Abort)) { token.ThrowIfCancellationRequested(); client.Upload(source, path); } }
            catch (Exception error) { SshConnectionFactory.Failure(client, error); throw; }
            finally { client.Uploading -= handler; }
            token.ThrowIfCancellationRequested();
        }
        private void Abort() { try { client.Dispose(); } catch { /* See SFTP cancellation boundary above. */ } }
        public void Dispose() { try { client.Dispose(); } finally { if (settings != null) settings.Dispose(); } }
    }
}
