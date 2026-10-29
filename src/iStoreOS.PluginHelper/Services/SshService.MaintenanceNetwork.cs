using System;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed partial class SshService
    {
        readonly Func<SshClient, Func<bool>, IMaintenanceNetworkSession> maintenanceNetworkFactory;
        readonly Func<SshClient, string, IMaintenanceMenuSession> maintenanceMenuFactory;
        MaintenanceNetworkSnapshot maintenanceNetwork;
        SshClient maintenanceNetworkClient;
        CancellationTokenSource maintenanceNetworkLifetime;
        ComputerProxyTunnelService maintenanceMenuProxy;
        private void ResetMaintenanceNetworkLocked() { maintenanceNetwork = null; maintenanceNetworkClient = null; }
        public MaintenanceNetworkSnapshot GetMaintenanceNetworkSnapshot()
        {
            lock (sync) return !disposed && client != null && ReferenceEquals(maintenanceNetworkClient, client) && maintenanceNetwork != null ? maintenanceNetwork :
                new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.NotChecked, Code = "NotChecked" };
        }
        public MaintenanceMenuAvailability GetMaintenanceLaunchAvailability(RouterEnvironment expected)
        {
            lock (sync) {
                var basis = MenuAvailabilityLocked(expected);
                if (!basis.CanOpen) return basis.Status == MaintenanceMenuAvailabilityStatus.Busy && maintenanceNetworkLifetime != null ?
                    new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.CheckingNetwork, "正在检测 GitHub Raw 网络路径…") : basis;
                var network = GetMaintenanceNetworkSnapshot();
                if (network.State == MaintenanceNetworkState.NotChecked || network.State == MaintenanceNetworkState.Checking)
                    return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.CheckingNetwork, "正在检测 GitHub Raw 网络路径…");
                if (network.Path == MaintenanceNetworkPath.Direct && network.Available)
                    return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.Ready, "软路由可直接访问 GitHub Raw；点击后再次复核。");
                if (network.Path == MaintenanceNetworkPath.ComputerProxy && network.Available)
                    return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.ReadyViaComputerProxy, "可通过电脑 HTTP 代理运行；点击后复核并建立临时隧道。");
                return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.NetworkUnavailable, "GitHub Raw 直连和电脑 HTTP 代理均不可用，将在后台重试检测。");
            }
        }
        public Task<MaintenanceNetworkSnapshot> CheckMaintenanceNetworkAsync(RouterEnvironment expected, CancellationToken token)
        {
            SshClient primary; CancellationTokenSource lifetime;
            lock (sync) {
                if (disposed) throw new ObjectDisposedException("SshService");
                if (!CanChangeConnectionLocked()) throw new InvalidOperationException("已有任务正在进行。");
                if (!MenuAvailabilityLocked(expected).CanOpen) return Task.FromResult(new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.Unavailable, Code = "NotConnected", CheckedAt = DateTimeOffset.UtcNow });
                primary = client; lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); lifetime.CancelAfter(TimeSpan.FromSeconds(35));
                maintenanceNetworkLifetime = lifetime; softwareBusy = true;
                maintenanceNetworkClient = primary; maintenanceNetwork = new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.Checking, Code = "Checking" };
            }
            return Task.Run(async () => {
                MaintenanceNetworkSnapshot result = null;
                try { result = await SelectMaintenanceNetworkAsync(primary, lifetime.Token).ConfigureAwait(false); return result; }
                finally {
                    lock (sync) {
                        if (ReferenceEquals(maintenanceNetworkLifetime, lifetime)) {
                            maintenanceNetworkLifetime = null; softwareBusy = false;
                            if (!disposed && ReferenceEquals(client, primary)) maintenanceNetwork = result ?? UnavailableNetwork("CheckInterrupted");
                        }
                    }
                    lifetime.Dispose();
                }
            });
        }
        async Task<MaintenanceNetworkSnapshot> SelectMaintenanceNetworkAsync(SshClient primary, CancellationToken token)
        {
            try {
                var session = maintenanceNetworkFactory(primary, () => { lock (sync) return !disposed && ReferenceEquals(client, primary); });
                return await new MaintenanceNetworkService().CheckAsync(session, token).ConfigureAwait(false);
            } catch (OperationCanceledException) { throw; }
            catch (Exception) { return UnavailableNetwork("CheckFailed"); }
        }
        static MaintenanceNetworkSnapshot UnavailableNetwork(string code)
        { return new MaintenanceNetworkSnapshot { State = MaintenanceNetworkState.Unavailable, CheckedAt = DateTimeOffset.UtcNow, Code = code }; }
        public Task<MaintenanceMenuRunResult> RunMaintenanceMenuWithNetworkAsync(RouterEnvironment expected,
            Action<MaintenanceMenuTerminal> terminalReady, IProgress<MaintenanceOperationLog> progress, CancellationToken token,
            uint columns = 100, uint rows = 30, Func<Tuple<uint, uint>> terminalSize = null)
        {
            if (terminalReady == null) throw new ArgumentNullException("terminalReady"); MaintenanceMenuTerminal.ValidateSize(columns, rows);
            SshClient primary; CancellationTokenSource lifetime; string fingerprint;
            lock (sync) {
                if (disposed) throw new ObjectDisposedException("SshService");
                if (!CanChangeConnectionLocked()) throw new InvalidOperationException("已有安装、检测或维护任务正在进行。");
                if (!MenuAvailabilityLocked(expected).CanOpen) return MenuRejected(MaintenancePreparationStatus.Rejected, "请重新连接并确认设备身份。");
                primary = client; fingerprint = expected.HostKeyFingerprint;
                lifetime = CancellationTokenSource.CreateLinkedTokenSource(token); menuLifetime = lifetime; softwareBusy = true;
            }
            return Task.Run(async () => {
                MaintenanceMenuRunResult result = null; ComputerProxyTunnelService proxy = null; MaintenanceNetworkSnapshot network = null;
                Action<string, MaintenancePreparationStage> log = (message, stage) => {
                    if (progress != null) try { progress.Report(new MaintenanceOperationLog(Guid.Empty, MaintenanceLogOrigin.PluginHelper, message, stage)); } catch (Exception) { }
                };
                Func<Task<MaintenanceMenuRunResult>> execute = async () => {
                    log("正在复核 GitHub Raw 网络路径…", MaintenancePreparationStage.Network);
                    network = await SelectMaintenanceNetworkAsync(primary, lifetime.Token).ConfigureAwait(false);
                    lock (sync) { if (!disposed && ReferenceEquals(client, primary)) { maintenanceNetwork = network; maintenanceNetworkClient = primary; } }
                    if (!network.Available) return result = RejectedNetwork("GitHub Raw 直连和电脑 HTTP 代理均不可用。", network);
                    if (network.Path == MaintenanceNetworkPath.ComputerProxy) {
                        log("网络路径：电脑代理；正在建立临时隧道…", MaintenancePreparationStage.Network);
                        lock (sync) {
                            if (disposed || !ReferenceEquals(client, primary) || !ReferenceEquals(menuLifetime, lifetime)) throw new OperationCanceledException();
                            proxy = BuildComputerProxyLocked(primary, fingerprint, network.LocalProxyPort, new MaintenanceProxyProgress(log)); maintenanceMenuProxy = proxy;
                        }
                        var created = await proxy.CreateAsync(lifetime.Token).ConfigureAwait(false);
                        if (created.Outcome == ComputerProxyOutcome.Cancelled) throw new OperationCanceledException(lifetime.Token);
                        if (created.Outcome != ComputerProxyOutcome.Ready) { CacheMaintenanceFailure(primary, "TunnelCreateFailed"); return result = RejectedNetwork("电脑代理隧道未能创建：" + created.Code, network); }
                        var verified = await proxy.VerifyAsync(lifetime.Token).ConfigureAwait(false);
                        if (verified.Outcome == ComputerProxyOutcome.Cancelled) throw new OperationCanceledException(lifetime.Token);
                        if (verified.Outcome != ComputerProxyOutcome.Verified) { CacheMaintenanceFailure(primary, "TunnelRawUnavailable"); return result = RejectedNetwork("电脑代理隧道无法访问 GitHub Raw。", network); }
                    }
                    log(network.Path == MaintenanceNetworkPath.Direct ? "网络路径：软路由直连" : "网络路径：电脑代理", MaintenancePreparationStage.Network);
                    lifetime.Token.ThrowIfCancellationRequested();
                    var menu = maintenanceMenuFactory(primary, fingerprint);
                    result = await maintenancePreparation.RunMenuAsync(menu, terminalReady, progress, lifetime.Token, null, columns, rows, terminalSize, network.Path,
                        network.Path == MaintenanceNetworkPath.ComputerProxy ? (int?)network.LocalProxyPort : null).ConfigureAwait(false);
                    result.NetworkPath = network.Path; return result;
                };
                try { result = await execute().ConfigureAwait(false); }
                catch (OperationCanceledException) { result = new MaintenanceMenuRunResult { NetworkPath = network == null ? MaintenanceNetworkPath.None : network.Path, Preparation = new MaintenancePreparationResult { Status = MaintenancePreparationStatus.Cancelled, Error = "菜单启动或会话已取消。" } }; }
                catch (Exception error) { result = RejectedNetwork("菜单网络编排失败：" + error.GetType().Name, network); }
                try {
                    if (proxy != null) {
                        log("电脑代理：正在关闭隧道并确认端口释放…", MaintenancePreparationStage.Cleanup);
                        var closed = await proxy.CloseAsync().ConfigureAwait(false);
                        if (result != null) {
                            result.ProxyCleanup = closed;
                            if (!closed.Succeeded && result.Preparation != null) result.Preparation.CleanupWarning = (result.Preparation.CleanupWarning ?? "") + "\n电脑代理端口释放未确认：" + closed.Release;
                        }
                    }
                } finally {
                    lock (sync) {
                        if (ReferenceEquals(menuLifetime, lifetime)) { menuLifetime = null; maintenanceMenuProxy = null; softwareBusy = false; }
                    }
                    lifetime.Dispose();
                }
                return result;
            });
        }
        sealed class MaintenanceProxyProgress : IProgress<ComputerProxyLog>
        {
            readonly Action<string, MaintenancePreparationStage> log;
            internal MaintenanceProxyProgress(Action<string, MaintenancePreparationStage> log) { this.log = log; }
            public void Report(ComputerProxyLog entry) { log("电脑代理诊断 | 阶段=" + entry.Phase + " | 域名=" + (entry.Domain ?? "-") + " | URL=" + (entry.Url ?? "-") +
                " | HTTP=" + (entry.HttpStatus.HasValue ? entry.HttpStatus.ToString() : "unknown") + " | 代码=" + entry.Code + " | 异常类型=" + (entry.ErrorType ?? "-"), MaintenancePreparationStage.Network); }
        }
        private void CacheMaintenanceFailure(SshClient primary, string code)
        { lock (sync) { if (!disposed && ReferenceEquals(client, primary)) { maintenanceNetwork = UnavailableNetwork(code); maintenanceNetworkClient = primary; } } }
        static MaintenanceMenuRunResult RejectedNetwork(string message, MaintenanceNetworkSnapshot network)
        { return new MaintenanceMenuRunResult { NetworkPath = network == null ? MaintenanceNetworkPath.None : network.Path, Preparation = new MaintenancePreparationResult { Status = MaintenancePreparationStatus.Rejected, Error = message } }; }
    }
}
