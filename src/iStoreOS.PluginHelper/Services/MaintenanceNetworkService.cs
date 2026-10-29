using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    internal interface IMaintenanceNetworkSession
    {
        bool Connected { get; }
        Task<ComputerProxyCommandResult> ReadAsync(ComputerProxyCommand command, CancellationToken token);
        IList<int> Candidates();
        Task<bool> CheckWindowsRawAsync(int port, CancellationToken token);
    }
    internal sealed class MaintenanceNetworkService
    {
        internal async Task<MaintenanceNetworkSnapshot> CheckAsync(IMaintenanceNetworkSession session, CancellationToken token)
        {
            var r = new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.Unavailable, Code = "RawUnavailable" };
            if (!session.Connected) { r.Code = "NotConnected"; r.CheckedAt = DateTimeOffset.UtcNow; return r; }
            r.RouterRaw = await ReadProbe(session, ComputerProxyCommand.DirectRaw, token).ConfigureAwait(false);
            // API failure is diagnostic only. Never include it in the Raw gate.
            r.RouterApi = await ReadProbe(session, ComputerProxyCommand.DirectApi, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!session.Connected) throw new IOException("Maintenance connection changed.");
            if (r.RouterRaw.TransportOk && r.RouterRaw.HttpStatus == 200) {
                r.State = MaintenanceNetworkState.Available; r.Path = MaintenanceNetworkPath.Direct; r.Code = "RouterRawAvailable";
            } else {
                foreach (int port in session.Candidates()) {
                    ComputerProxyPolicy.ValidatePort(port); token.ThrowIfCancellationRequested();
                    if (await session.CheckWindowsRawAsync(port, token).ConfigureAwait(false)) {
                        r.State = MaintenanceNetworkState.Available; r.Path = MaintenanceNetworkPath.ComputerProxy; r.LocalProxyPort = port; r.Code = "WindowsRawAvailable"; break;
                    }
                }
            }
            if (!session.Connected) throw new IOException("Maintenance connection changed.");
            r.CheckedAt = DateTimeOffset.UtcNow; return r;
        }
        static async Task<ComputerProxyProbe> ReadProbe(IMaintenanceNetworkSession session, ComputerProxyCommand command, CancellationToken token)
        {
            try { return ComputerProxyPolicy.ParseProbe(command, await session.ReadAsync(command, token).ConfigureAwait(false)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return ComputerProxyPolicy.ParseProbe(command, null); }
        }
    }
    internal sealed class SshMaintenanceNetworkSession : IMaintenanceNetworkSession
    {
        readonly SshClient primary; readonly Func<bool> current;
        internal SshMaintenanceNetworkSession(SshClient primary, Func<bool> current) { this.primary = primary; this.current = current; }
        public bool Connected { get { try { return current() && primary.IsConnected; } catch (ObjectDisposedException) { return false; } } }
        public async Task<ComputerProxyCommandResult> ReadAsync(ComputerProxyCommand command, CancellationToken token)
        {
            if (command != ComputerProxyCommand.DirectRaw && command != ComputerProxyCommand.DirectApi) throw new ArgumentOutOfRangeException("command");
            if (!Connected) throw new IOException("Maintenance SSH unavailable.");
            using (var request = primary.CreateCommand(ComputerProxyPolicy.GetCommand(command))) {
                request.CommandTimeout = TimeSpan.FromSeconds(8); await request.ExecuteAsync(token).ConfigureAwait(false);
                string output = request.Result ?? ""; if (output.Length > 65536) throw new InvalidDataException("Maintenance network output limit.");
                return new ComputerProxyCommandResult { ExitCode = request.ExitStatus ?? -1, Output = output };
            }
        }
        public IList<int> Candidates() { return WindowsComputerProxyDetector.GetListeningCandidates(); }
        internal static HttpWebRequest WindowsRequest(int port)
        {
            ComputerProxyPolicy.ValidatePort(port);
            var request = WindowsThirdPartyHttpClient.CreateRequest(ComputerProxyPolicy.RawUrl, 65536);
            request.Proxy = new WebProxy("http://127.0.0.1:" + port, false) { Credentials = null };
            request.UseDefaultCredentials = false; request.Credentials = null; request.Timeout = 8000; request.ReadWriteTimeout = 8000;
            return request;
        }
        public async Task<bool> CheckWindowsRawAsync(int port, CancellationToken token)
        {
            var request = WindowsRequest(port);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                using (timeout.Token.Register(request.Abort)) try {
                    using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false)) { token.ThrowIfCancellationRequested(); return response.StatusCode == HttpStatusCode.OK; }
                } catch (WebException error) { if (error.Response != null) error.Response.Dispose(); token.ThrowIfCancellationRequested(); return false; }
            }
        }
    }
}
