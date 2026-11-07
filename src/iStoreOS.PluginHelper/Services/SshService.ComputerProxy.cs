using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed partial class SshService
    {
        readonly Func<ComputerProxyConnectionContext, IComputerProxyTunnelSession> computerProxyFactory;
        ComputerProxyTunnelService computerProxyTunnel;
        ComputerProxyCloseResult lastComputerProxyClose;
        public SshService() : this(context => new SshComputerProxyTunnelSession(context)) { }
        internal SshService(Func<ComputerProxyConnectionContext, IComputerProxyTunnelSession> computerProxyFactory,
            Func<Renci.SshNet.SshClient, Func<bool>, IMaintenanceNetworkSession> networkFactory = null,
            Func<Renci.SshNet.SshClient, string, IMaintenanceMenuSession> menuFactory = null)
        {
            if (computerProxyFactory == null) throw new ArgumentNullException("computerProxyFactory"); this.computerProxyFactory = computerProxyFactory;
            maintenanceNetworkFactory = networkFactory ?? ((primary, current) => new SshMaintenanceNetworkSession(primary, current));
            maintenanceMenuFactory = menuFactory ?? ((primary, fingerprint) => new SshMaintenancePreparationSession(primary, () => CreateTrustedTransfer(fingerprint), text => sanitizeLog(text)));
        }
        public bool HasComputerProxyTunnel { get { lock (sync) return computerProxyTunnel != null; } }
        public ComputerProxyEndpoint ComputerProxyEndpoint { get { lock (sync) return computerProxyTunnel == null ? null : computerProxyTunnel.Endpoint; } }
        public ComputerProxyCloseResult LastComputerProxyClose { get { lock (sync) return lastComputerProxyClose; } }
        public Task<ComputerProxyResult> CreateComputerProxyTunnelAsync(RouterEnvironment expected, int localProxyPort,
            IProgress<ComputerProxyLog> progress, CancellationToken token)
        {
            ComputerProxyPolicy.ValidatePort(localProxyPort); ComputerProxyTunnelService tunnel = null;
            lock (sync) {
                if (disposed) throw new ObjectDisposedException("SshService");
                if (!CanChangeConnectionLocked()) throw new InvalidOperationException("当前连接、安装或插件维护任务尚未结束。");
                if (token.IsCancellationRequested) return Task.FromResult(new ComputerProxyResult { Outcome = ComputerProxyOutcome.Cancelled, Code = "Cancelled", Probes = new List<ComputerProxyProbe>() });
                if (!MenuAvailabilityLocked(expected).CanOpen) return Task.FromResult(new ComputerProxyResult { Outcome = ComputerProxyOutcome.Rejected, Code = "TrustedConnectionRequired", Probes = new List<ComputerProxyProbe>() });
                tunnel = BuildComputerProxyLocked(client, expected.HostKeyFingerprint, localProxyPort, progress); softwareBusy = true;
            }
            return Task.Run(() => tunnel.CreateAsync(token));
        }
        public Task<ComputerProxyResult> VerifyComputerProxyTunnelAsync(CancellationToken token)
        {
            ComputerProxyTunnelService tunnel;
            lock (sync) { if (disposed) throw new ObjectDisposedException("SshService"); tunnel = computerProxyTunnel; }
            return tunnel == null ? Task.FromResult(new ComputerProxyResult { Outcome = ComputerProxyOutcome.Rejected, Code = "NoActiveTunnel", Probes = new List<ComputerProxyProbe>() }) : tunnel.VerifyAsync(token);
        }
        public Task<ComputerProxyCloseResult> CloseComputerProxyTunnelAsync()
        {
            ComputerProxyTunnelService tunnel; ComputerProxyCloseResult previous;
            lock (sync) { tunnel = computerProxyTunnel; previous = lastComputerProxyClose; }
            return tunnel != null ? tunnel.CloseAsync() : Task.FromResult(previous ?? new ComputerProxyCloseResult {
                SecondaryClosed = true, Release = ComputerProxyRelease.NotCreated, Errors = new List<string>() });
        }
        void ReleaseComputerProxy(ComputerProxyTunnelService tunnel, ComputerProxyCloseResult result)
        {
            CancellationTokenSource menu = null;
            lock (sync) {
                if (!ReferenceEquals(computerProxyTunnel, tunnel)) return;
                computerProxyTunnel = null; lastComputerProxyClose = result;
                if (ReferenceEquals(maintenanceMenuProxy, tunnel)) menu = menuLifetime;
                softwareBusy = menuLifetime != null || maintenanceNetworkLifetime != null;
            }
            if (menu != null) { try { menu.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { } }
        }
        private ComputerProxyTunnelService BuildComputerProxyLocked(SshClient primary, string fingerprint, int localProxyPort, IProgress<ComputerProxyLog> progress)
        {
            string host = primary.ConnectionInfo.Host; int port = primary.ConnectionInfo.Port;
            var endpoint = new ComputerProxyEndpoint { DeviceHost = host, DevicePort = port, LocalProxyPort = localProxyPort };
            var context = new ComputerProxyConnectionContext { Primary = primary, Endpoint = endpoint, Account = primary.ConnectionInfo.Username, Authentication = connectionFactory,
                Log = text => connectionDiagnostics(sanitizeLog(text)),
                IsCurrent = () => { lock (sync) return !disposed && ReferenceEquals(client, primary); }, TrustHostKey = key => IsComputerProxyHostKeyTrusted(host, port, fingerprint, key) };
            var session = computerProxyFactory(context); ComputerProxyTunnelService tunnel = null;
            try { tunnel = new ComputerProxyTunnelService(session, endpoint, progress, result => ReleaseComputerProxy(tunnel, result)); }
            catch { session.Dispose(); throw; }
            computerProxyTunnel = tunnel; lastComputerProxyClose = null; return tunnel;
        }
        internal bool IsComputerProxyHostKeyTrusted(string host, int port, string fingerprint, byte[] key)
        {
            if (key == null || key.Length == 0) return false;
            string actual; using (var sha = SHA256.Create()) actual = "SHA256:" + Convert.ToBase64String(sha.ComputeHash(key)).TrimEnd('=');
            return actual == fingerprint && trustStore.IsTrusted(host, port, actual);
        }
    }
}
