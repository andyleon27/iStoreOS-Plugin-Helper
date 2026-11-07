using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    internal sealed class ComputerProxyConnectionContext
    {
        internal SshClient Primary;
        internal ComputerProxyEndpoint Endpoint;
        internal string Account;
        internal Func<PasswordConnectionInfo> Authentication;
        internal Func<byte[], bool> TrustHostKey;
        internal Func<bool> IsCurrent;
        internal Action<string> Log;
    }
    internal sealed class SshComputerProxyTunnelSession : IComputerProxyTunnelSession
    {
        ComputerProxyConnectionContext context;
        readonly SshClient primary;
        SshClient secondary; PasswordConnectionInfo settings; ForwardedPortRemote forward;
        int primaryErrors; bool hostKeyValidated, shutdownAttempted, secondaryMayBeConnected;
        public event EventHandler ConnectionLost;
        internal SshComputerProxyTunnelSession(ComputerProxyConnectionContext context)
        { this.context = context; primary = context.Primary; primary.ErrorOccurred += PrimaryError; }
        static bool Connected(SshClient client)
        { try { return client != null && client.IsConnected; } catch (ObjectDisposedException) { return false; } }
        public bool PrimaryConnected { get { return Connected(primary); } }
        public bool PrimaryUnchanged { get { return context != null && context.IsCurrent() && Volatile.Read(ref primaryErrors) == 0; } }
        public bool SecondaryConnected { get { return secondaryMayBeConnected || Connected(secondary); } }
        void PrimaryError(object sender, ExceptionEventArgs e) { Interlocked.Increment(ref primaryErrors); NotifyLost(); }
        void SecondaryError(object sender, ExceptionEventArgs e) { NotifyLost(); }
        void NotifyLost() { var handler = ConnectionLost; if (handler != null) handler(this, EventArgs.Empty); }
        public async Task<ComputerProxyCommandResult> ExecuteAsync(ComputerProxyCommand command, CancellationToken token)
        {
            if (!PrimaryConnected || !PrimaryUnchanged) throw new InvalidOperationException("Primary SSH unavailable.");
            using (var request = primary.CreateCommand(ComputerProxyPolicy.GetCommand(command))) {
                request.CommandTimeout = TimeSpan.FromSeconds(18);
                await request.ExecuteAsync(token).ConfigureAwait(false);
                string output = request.Result ?? ""; if (output.Length > 65536) throw new InvalidDataException("Proxy response limit.");
                return new ComputerProxyCommandResult { ExitCode = request.ExitStatus ?? -1, Output = output };
            }
        }
        public async Task ConnectSecondaryAsync(CancellationToken token)
        {
            if (secondary != null || shutdownAttempted) throw new InvalidOperationException("Secondary already used.");
            if (!PrimaryConnected || !PrimaryUnchanged) throw new InvalidOperationException("Primary SSH unavailable.");
            var candidate = context.Authentication();
            if (ReferenceEquals(candidate, primary.ConnectionInfo)) throw new InvalidOperationException("Independent connection information required.");
            settings = candidate;
            if (!string.Equals(settings.Host, context.Endpoint.DeviceHost, StringComparison.OrdinalIgnoreCase) || settings.Port != context.Endpoint.DevicePort || settings.Username != context.Account)
                throw new InvalidOperationException("Secondary device/account mismatch.");
            secondary = SshConnectionFactory.Ssh(settings, "电脑代理 SSH #2", context.Log);
            SshConnectionFactory.Trust(secondary, key => context != null && context.TrustHostKey(key), accepted => hostKeyValidated = accepted);
            secondary.ErrorOccurred += SecondaryError; secondaryMayBeConnected = true;
            await SshConnectionFactory.ConnectAsync(secondary, token).ConfigureAwait(false);
            if (!hostKeyValidated || !secondary.IsConnected) throw new SshConnectionException("Secondary identity was not confirmed.");
        }
        public Task StartForwardAsync(int localProxyPort, CancellationToken token)
        {
            ComputerProxyPolicy.ValidatePort(localProxyPort);
            return Task.Run(() => {
                token.ThrowIfCancellationRequested();
                if (!hostKeyValidated || !Connected(secondary) || forward != null) throw new InvalidOperationException("Trusted secondary required.");
                forward = new ForwardedPortRemote(System.Net.IPAddress.Loopback, ComputerProxyPolicy.RemotePort, System.Net.IPAddress.Loopback, (uint)localProxyPort);
                try { secondary.AddForwardedPort(forward); forward.Start(); token.ThrowIfCancellationRequested(); }
                catch (Exception error) { SshConnectionFactory.Failure(secondary, error); throw; }
            });
        }
        public Task CloseSecondaryAsync() { return Task.Run((Action)CloseOwned); }
        void CloseOwned()
        {
            if (shutdownAttempted) return; shutdownAttempted = true; Exception failure = null;
            // Disconnecting SSH #2 also releases listeners on Dropbear versions
            // that reject cancel-tcpip-forward. Primary SSH #1 is never disposed here.
            try { if (secondary != null) { secondary.ErrorOccurred -= SecondaryError; secondary.Disconnect(); } } catch (Exception error) { failure = error; }
            try { if (secondary != null) secondary.Dispose(); } catch (Exception error) { if (failure == null) failure = error; }
            try { if (forward != null) forward.Dispose(); } catch (Exception error) { if (failure == null) failure = error; }
            try { if (settings != null) settings.Dispose(); } catch (Exception error) { if (failure == null) failure = error; }
            secondaryMayBeConnected = failure != null;
            if (failure != null) throw failure;
        }
        public void Dispose()
        {
            try { CloseOwned(); }
            finally { primary.ErrorOccurred -= PrimaryError; context = null; }
        }
    }
}
