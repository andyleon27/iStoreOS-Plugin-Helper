using System;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace iStoreOS.PluginHelper.Services
{
    internal static class SshConnectionFactory
    {
        internal static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(8);
        private sealed class DiagnosticContext { internal string Role; internal Action<string> Log; }
        private static readonly ConditionalWeakTable<BaseClient, DiagnosticContext> Diagnostics = new ConditionalWeakTable<BaseClient, DiagnosticContext>();
        internal static PasswordConnectionInfo Password(string host, int port, string user, string password)
        { return new PasswordConnectionInfo(host, port, user, password) { Timeout = ConnectionTimeout }; }
        // Leave every algorithm collection/order exactly as supplied by the mature library.
        internal static SshClient Ssh(ConnectionInfo settings, string role, Action<string> log)
        { return Observe(new SshClient(settings), role, log); }
        internal static SftpClient Sftp(ConnectionInfo settings, bool installation, Action<string> log)
        { return Observe(installation ? new InstallationSftpClient(settings) : new SftpClient(settings), installation ? "安装 SFTP" : "维护 SFTP", log); }
        internal static ScpClient Scp(ConnectionInfo settings, Action<string> log)
        { return Observe(new ScpClient(settings, RemotePathTransformation.ShellQuote) { UseDirectoryFlag = true }, "安装 SCP", log); }
        private static T Observe<T>(T client, string role, Action<string> log) where T : BaseClient
        {
            client.ConnectionInfo.Timeout = ConnectionTimeout; client.KeepAliveInterval = TimeSpan.FromSeconds(30);
            // Every production caller must attach the existing TOFU or pinned-fingerprint rule.
            // An accidentally omitted trust callback must fail closed, never accept a helper connection.
            client.HostKeyReceived += (sender, args) => args.CanTrust = false;
            if (log != null) {
                Diagnostics.Add(client, new DiagnosticContext { Role = role, Log = log });
                client.ErrorOccurred += (sender, args) => Failure(client, args.Exception);
            }
            return client;
        }
        internal static string Fingerprint(byte[] key)
        { using (var sha = SHA256.Create()) return "SHA256:" + Convert.ToBase64String(sha.ComputeHash(key)).TrimEnd('='); }
        internal static void Trust(BaseClient client, Func<byte[], bool> trusted, Action<bool> decision = null)
        {
            if (trusted == null) throw new ArgumentNullException("trusted");
            client.HostKeyReceived += (sender, args) => { args.CanTrust = trusted(args.HostKey); if (decision != null) decision(args.CanTrust); };
        }
        internal static void Connect(BaseClient client)
        { try { client.Connect(); Success(client); } catch (Exception error) { Failure(client, error); throw; } }
        internal static async Task ConnectAsync(BaseClient client, CancellationToken token)
        { try { await client.ConnectAsync(token).ConfigureAwait(false); Success(client); } catch (Exception error) { Failure(client, error); throw; } }
        private static void Success(BaseClient client)
        {
            DiagnosticContext context; if (!Diagnostics.TryGetValue(client, out context)) return;
            try { Emit(context, SshDiagnostics.RuntimeVersion()); Emit(context, SshDiagnostics.Negotiated(client.ConnectionInfo, context.Role, false)); }
            catch { /* Optional diagnostics must not turn a successful connection into a failure. */ }
        }
        internal static void Failure(BaseClient client, Exception error)
        {
            DiagnosticContext context; if (client == null || !Diagnostics.TryGetValue(client, out context)) return;
            // Diagnostics must never mask authentication/cancellation/protocol failure or grant trust.
            try {
                Emit(context, SshDiagnostics.RuntimeVersion());
                Emit(context, "SSH 错误[" + context.Role + "]：" + SshErrorClassifier.Name(SshErrorClassifier.Classify(error)) + "；DisconnectReason=" + SshErrorClassifier.DisconnectReasonName(error));
                Emit(context, SshDiagnostics.Negotiated(client.ConnectionInfo, context.Role, true));
            } catch { /* Cancellation/disposal or a diagnostic failure must not replace the original error. */ }
        }
        private static void Emit(DiagnosticContext context, string message)
        { try { context.Log(message); } catch { /* A UI log callback is not a transport failure. */ } }
    }
}
