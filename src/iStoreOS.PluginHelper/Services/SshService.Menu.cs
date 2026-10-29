using System;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed partial class SshService
    {
        private readonly PluginMaintenanceExecutionService maintenancePreparation =
            new PluginMaintenanceExecutionService(new ThirdPartyScriptSource(new WindowsThirdPartyHttpClient()));
        private CancellationTokenSource menuLifetime;
        public bool HasActiveMaintenanceMenu { get { lock (sync) return menuLifetime != null; } }

        // Local snapshot only: no SSH command, HTTP request or new authentication.
        public MaintenanceMenuAvailability GetMaintenanceMenuAvailability(RouterEnvironment expected)
        { lock (sync) return MenuAvailabilityLocked(expected); }

        private MaintenanceMenuAvailability MenuAvailabilityLocked(RouterEnvironment expected)
        {
            if (disposed) return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.Closed, "当前连接已关闭。");
            if (connecting || softwareBusy) return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.Busy, "请等待当前连接、安装或检测任务完成。");
            if (installWorkflow.HasPendingWork) return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.PendingInstallation, "上轮安装任务尚未确认，请先在首页处理。");
            if (client == null || !client.IsConnected || connectionFactory == null)
                return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.NotConnected, "请先在首页连接路由器。");
            if (expected == null || !expected.IsConnected || !trustStore.IsTrusted(client.ConnectionInfo.Host, client.ConnectionInfo.Port, expected.HostKeyFingerprint))
                return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.IdentityUnconfirmed, "设备 Host Key 身份未确认，请重新连接并检测。");
            return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.Ready, "打开原项目菜单终端，安装、更新或卸载插件。");
        }

        public Task<MaintenanceMenuRunResult> RunMaintenanceMenuAsync(RouterEnvironment expected,
            Action<MaintenanceMenuTerminal> terminalReady, IProgress<MaintenanceOperationLog> progress,
            CancellationToken token, string commit = null, uint columns = 100, uint rows = 30, Func<Tuple<uint, uint>> terminalSize = null)
        {
            if (terminalReady == null) throw new ArgumentNullException("terminalReady");
            if (commit != null) ThirdPartyScriptCatalog.ValidateCommit(commit);
            MaintenanceMenuTerminal.ValidateSize(columns, rows);
            SshMaintenancePreparationSession session;
            CancellationTokenSource lifetime;
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("SshService");
                if (connecting || softwareBusy || installWorkflow.HasPendingWork)
                    throw new InvalidOperationException("已有安装、检测或插件维护任务正在进行。");
                if (token.IsCancellationRequested) return MenuRejected(MaintenancePreparationStatus.Cancelled, "菜单准备已取消。");
                var availability = MenuAvailabilityLocked(expected);
                if (!availability.CanOpen) return MenuRejected(MaintenancePreparationStatus.Rejected, availability.Message);
                string fingerprint = expected.HostKeyFingerprint;
                session = new SshMaintenancePreparationSession(client, () => CreateTrustedTransfer(fingerprint), text => sanitizeLog(text));
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                menuLifetime = lifetime;
                softwareBusy = true;
            }
            return Task.Run(async () => {
                try { return await maintenancePreparation.RunMenuAsync(session, terminalReady, progress, lifetime.Token, commit, columns, rows, terminalSize).ConfigureAwait(false); }
                finally
                {
                    lock (sync) { if (ReferenceEquals(menuLifetime, lifetime)) menuLifetime = null; softwareBusy = false; }
                    lifetime.Dispose();
                }
            });
        }
        private static Task<MaintenanceMenuRunResult> MenuRejected(MaintenancePreparationStatus status, string error)
        { return Task.FromResult(new MaintenanceMenuRunResult { Preparation = new MaintenancePreparationResult { Status = status, Error = error } }); }
    }
}
