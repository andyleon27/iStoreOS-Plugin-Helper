using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class SoftwareEnvironmentService
    {
        public async Task<SoftwareEnvironmentResult> RunAsync(ISoftwareSourceSession session, RouterEnvironment router,
            IProgress<SoftwareProgress> progress, Func<SoftwareRepairPlan, Task<bool>> confirmRepair, CancellationToken token)
        {
            var result = new SoftwareEnvironmentResult();
            Action<string, string> report = (step, log) => { if (progress != null) progress.Report(new SoftwareProgress { Step = step, Log = log == null ? null : session.Sanitize(log), Result = result }); };
            Action<string> logOnly = log => report("正在检测软件环境…", log);
            var sources = new SoftwareSourceService(); var probes = new RepositoryProbeService();
            var quick = new QuickStartService(); var packages = new PackageManagerService();
            SoftwareSourceSnapshot snapshot = null;
            List<SoftwareSourceInfo> candidates = null;
            bool mutationMayHaveOccurred = false;
            try
            {
                if (router == null || !router.IsConnected || router.PackageManager == PackageManagerKind.Unknown)
                { result.Status = SoftwareEnvironmentStatus.Unsupported; result.Message = "请连接具有 APK / OPKG 源配置的设备。"; return result; }
                token.ThrowIfCancellationRequested();
                report("正在读取当前软件源…", null);
                snapshot = sources.Read(session, router.PackageManager, logOnly);
                snapshot.CurrentSource = quick.ReadCurrent(session, logOnly);
                result.CurrentSource = snapshot.CurrentSource;
                logOnly("当前 QuickStart source：" + (snapshot.CurrentSource == null ? "自定义 / 未知来源" : snapshot.CurrentSource.ToString() + " | " + snapshot.CurrentSource.Url));
                result.OfficialHealth = probes.Probe(session, snapshot.OfficialFeeds, report, token, "官方源");
                logOnly("当前官方源：" + result.OfficialHealth);
                result.AdditionalHealth = probes.Probe(session, snapshot.AdditionalFeeds, report, token, "固定 / 自定义源");
                if (result.AdditionalHealth.Successful < result.AdditionalHealth.Total)
                    logOnly("Warning：有 " + (result.AdditionalHealth.Total - result.AdditionalHealth.Successful) + " 个固定 / 自定义源探测异常；继续真实 package update，不修改这些源。");
                token.ThrowIfCancellationRequested();
                PackageUpdateResult initial = packages.Update(session, router.PackageManager, snapshot.OfficialFeeds, snapshot.AdditionalFeeds, report);
                result.Updates.Add(initial);
                if (initial.Success && result.OfficialHealth.Status == RepositoryHealth.FULL)
                { result.Status = SoftwareEnvironmentStatus.Ready; result.Message = "✓ 软件环境已就绪；当前源无需修改。"; return result; }
                token.ThrowIfCancellationRequested();
                if (!initial.Success && (!initial.OfficialRepositoryFailure || PackageManagerService.IsNonSourceFailure(initial) ||
                    (initial.AdditionalRepositoryFailure && !initial.OfficialRepositoryFailure)))
                { result.Status = SoftwareEnvironmentStatus.Failed; result.Message = "package update 失败，错误涉及固定 / 自定义源或包兼容性；不轮换官方镜像，详情见日志。"; return result; }
                if (initial.Success)
                {
                    // A transient probe failure may resolve during the package update.
                    result.OfficialHealth = probes.Probe(session, snapshot.OfficialFeeds, report, token, "官方源复核");
                    if (result.OfficialHealth.Status == RepositoryHealth.FULL)
                    { result.Status = SoftwareEnvironmentStatus.Ready; result.Message = "✓ 软件环境已就绪；当前源无需修改。"; return result; }
                }
                if (result.OfficialHealth.Status == RepositoryHealth.FULL && !initial.OfficialRepositoryFailure)
                { result.Status = SoftwareEnvironmentStatus.Failed; result.Message = "package update 失败，但没有明确的官方仓库错误；保持当前源，详情见日志。"; return result; }
                if (string.IsNullOrWhiteSpace(snapshot.RomText))
                { result.Status = SoftwareEnvironmentStatus.Unsupported; result.Message = "ROM 源模板不可读，无法安全生成候选源；未修改配置。"; return result; }
                report("正在检测 QuickStart 候选源…", null);
                var search = new OfficialSourceSearch(snapshot, router.PackageManager, new RepositoryProbeRound());
                bool prepared = false;
                foreach (SoftwareRepairPlan plan in search.Find(session, () => {
                    candidates = quick.ReadCandidates(session, logOnly); return candidates;
                }, report, token, result.Candidates))
                {
                    if (!prepared)
                    {
                        token.ThrowIfCancellationRequested();
                        report("等待确认自动修复…", "当前源存在问题；已找到首个 FULL 候选，首次 POST 前等待用户确认。");
                        if (confirmRepair == null || !await confirmRepair(plan))
                        { result.Status = SoftwareEnvironmentStatus.Cancelled; result.Message = "已取消自动修复；软件源未修改。"; return result; }
                        token.ThrowIfCancellationRequested();
                        ProbeResult beforeBackup = session.ReadOfficial(router.PackageManager, false);
                        if (beforeBackup.ExitStatus != 0 || beforeBackup.Output != snapshot.OriginalText)
                            throw new InvalidOperationException("检测期间官方源配置已变化，请重新检测；未执行切换。");
                        result.BackupPath = session.BackupOfficial(router.PackageManager);
                        ProbeResult backup = session.ReadBackup(result.BackupPath);
                        if (backup.ExitStatus != 0 || backup.Output != snapshot.OriginalText)
                            throw new InvalidOperationException("备份校验失败，禁止切换软件源。");
                        logOnly("原始 distfeeds 原文已备份：" + result.BackupPath + "；原 identity：" + (snapshot.CurrentSource == null ? "未知" : snapshot.CurrentSource.Identity));
                        prepared = true;
                    }
                    token.ThrowIfCancellationRequested();
                    report("正在切换到 " + plan.ProposedSource + "…", "切换尝试：从 " + (result.CurrentSource == null ? "自定义 / 未知来源" : result.CurrentSource.ToString()) + " 到 " + plan.ProposedSource);
                    result.SwitchAttempted = true;
                    mutationMayHaveOccurred = true; // POST may mutate before a connection/response error.
                    bool acknowledged = false;
                    try { acknowledged = quick.Switch(session, plan.ProposedSource.Identity, logOnly); }
                    catch (Exception exception) { logOnly("POST 异常：" + session.Sanitize(exception.Message)); }
                    SoftwareSourceInfo current = quick.ReadCurrent(session, logOnly);
                    ProbeResult configured = session.ReadOfficial(router.PackageManager, false);
                    if (configured.ExitStatus == 0 && configured.Output != snapshot.OriginalText) result.SourceSwitchOccurred = true;
                    string expected = SoftwareSourceService.RebaseRom(snapshot.RomText, plan.ProposedSource.Url);
                    logOnly("QuickStart POST acknowledgement=" + acknowledged + "；以实际 identity / distfeeds 验证切换结果。");
                    if (current == null || current.Identity != plan.ProposedSource.Identity || configured.ExitStatus != 0 ||
                        !SoftwareSourceService.MatchesFeeds(configured.Output, expected, router.PackageManager))
                    { logOnly("切换未通过当前 identity / distfeeds 完整路径验证，尝试下一个 FULL 候选。"); continue; }
                    logOnly("实际 identity / distfeeds 已确认切换成功。");
                    result.CurrentSource = current;
                    // Exact configured feed equality binds the previously verified FULL URL set.
                    var actualFeeds = SoftwareSourceService.ParseFeeds(configured.Output, router.PackageManager, snapshot.DistfeedsPath, RepositoryKind.Official);
                    result.OfficialHealth = plan.Probe;
                    token.ThrowIfCancellationRequested();
                    PackageUpdateResult update = packages.Update(session, router.PackageManager, actualFeeds, snapshot.AdditionalFeeds, report);
                    result.Updates.Add(update);
                    if (update.Success)
                    { result.Status = SoftwareEnvironmentStatus.Ready; result.Message = "✓ 软件环境已就绪；已保留新的健康源。"; mutationMayHaveOccurred = false; return result; }
                    if (!update.OfficialRepositoryFailure || PackageManagerService.IsNonSourceFailure(update))
                    { logOnly("真实 update 表明继续换官方源无法解决此错误，结束尝试并恢复原配置。"); break; }
                    logOnly("候选 FULL，但真实 package update 失败，尝试下一个 FULL 候选。");
                }
                result.Status = SoftwareEnvironmentStatus.Failed;
                result.Message = "自动修复未通过真实 package update。";
            }
            catch (OperationCanceledException) { result.Status = SoftwareEnvironmentStatus.Cancelled; result.Message = "检测已取消。"; }
            catch (Exception exception) { result.Status = SoftwareEnvironmentStatus.Failed; result.Message = "软件环境检测失败，详情见日志。"; logOnly("软件环境异常：" + session.Sanitize(exception.Message)); }
            finally
            {
                if (mutationMayHaveOccurred && snapshot != null && !string.IsNullOrEmpty(result.BackupPath))
                {
                    result.RecoveryAttempted = true;
                    // Recovery is independent of cancellation: never abandon a modified source.
                    report("正在恢复原始官方源…", "修复未成功，开始恢复；备份保留于 " + result.BackupPath);
                    try
                    {
                        ProbeResult current = session.ReadOfficial(router.PackageManager, false);
                        bool exact = current.ExitStatus == 0 && current.Output == snapshot.OriginalText;
                        if (!exact && snapshot.CurrentSource != null && candidates != null && candidates.Any(c => c.Identity == snapshot.CurrentSource.Identity))
                        {
                            try { quick.Switch(session, snapshot.CurrentSource.Identity, logOnly); }
                            catch (Exception exception) { logOnly("官方接口恢复失败：" + session.Sanitize(exception.Message)); }
                            current = session.ReadOfficial(router.PackageManager, false);
                            exact = current.ExitStatus == 0 && current.Output == snapshot.OriginalText;
                        }
                        if (!exact)
                        {
                            logOnly("使用原始备份原文进行最后 recovery（仅官方 distfeeds）。");
                            ProbeResult restore = session.RestoreBackup(router.PackageManager, result.BackupPath);
                            if (restore.ExitStatus != 0) throw new InvalidOperationException("备份恢复退出码 " + restore.ExitStatus);
                            current = session.ReadOfficial(router.PackageManager, false);
                            exact = current.ExitStatus == 0 && current.Output == snapshot.OriginalText;
                        }
                        result.RecoverySucceeded = exact;
                        if (!exact) throw new InvalidOperationException("恢复后的文本与原始 distfeeds 不一致。");
                        result.CurrentSource = snapshot.CurrentSource;
                        result.OfficialHealth = null; // Do not show failed candidate health as restored-source health.
                        result.Message += " 已验证恢复原始 distfeeds 原文。";
                        logOnly("恢复成功：原始 distfeeds 原文一致；没有修改辅助 / 自定义源。");
                    }
                    catch (Exception exception)
                    {
                        result.Status = SoftwareEnvironmentStatus.RecoveryFailed;
                        result.Message = "恢复失败，请检查设备；原始备份：" + result.BackupPath;
                        logOnly("Recovery 失败：" + session.Sanitize(exception.Message) + "；原始备份保留于 " + result.BackupPath);
                    }
                }
                string switchState = !result.SwitchAttempted ? "False" : result.SourceSwitchOccurred ? "True" : "Unknown（已尝试 POST，未确认配置变化）";
                report(result.Message ?? "软件环境检测结束。", "最终状态：" + result.Status + "；" + result.Message + "；source switch=" + switchState + "；POST attempted=" + result.SwitchAttempted);
            }
            return result;
        }
    }
}
