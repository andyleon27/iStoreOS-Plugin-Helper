using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class PluginInstallationService
    {
        public TimeSpan InactiveLimit { get; set; }
        public TimeSpan OverallLimit { get; set; }
        public TimeSpan PollInterval { get; set; }
        public PluginInstallationService()
        { InactiveLimit = TimeSpan.FromMinutes(15); OverallLimit = TimeSpan.FromMinutes(30); PollInterval = TimeSpan.FromSeconds(1); }

        public async Task<PluginInstallResult> RunAsync(IPluginInstallSession session, RouterEnvironment router, string filePath,
            Func<Task<SoftwareEnvironmentResult>> preflight, Func<InstallConfirmation, Task<bool>> confirm, IProgress<InstallProgress> progress, CancellationToken token)
        {
            var result = new PluginInstallResult(); RemoteTempSession remote = null;
            var timingSession = session as IInstallationTimingSession;
            var timing = timingSession == null ? new InstallationTiming() : timingSession.Timing;
            IDisposable uploadTiming = null;
            bool directoryAttempted = false;
            Action<string, string> report = (step, log) => { if (progress != null) progress.Report(new InstallProgress { Step = step, Log = log == null ? null : session.Sanitize(log), InstallerStarted = result.InstallerStarted }); };
            Action<string> warning = text => { result.Warnings.Add(text); report("提示", "Warning：" + text); };
            try
            {
                token.ThrowIfCancellationRequested();
                report("1/7 安装前重新校验", "重新读取本地文件，重新判断 SDK / Architecture。");
                result.Plugin = new PluginPackageAnalyzer().AnalyzeFile(filePath);
                new CompatibilityService().Evaluate(result.Plugin, router);
                if (result.Plugin.CompatibilityStatus != CompatibilityStatus.Compatible || router.PackageManager == PackageManagerKind.Unknown)
                    throw new InvalidOperationException("安装前兼容性检查未通过。");
                // Keep this handle until the workflow completes. FileShare.Read prevents writes,
                // replacement and deletion while the bytes being hashed are subsequently uploaded.
                using (var source = new FileStream(result.Plugin.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    result.Plugin.FileSize = source.Length;
                    if (source.Length == 0) throw new InvalidDataException("安装包为空。");
                    result.Sha256 = PluginPackageInspection.ComputeSha256(source);
                    report("1/7 软件环境", "本地文件：" + result.Plugin.FileName + "；bytes=" + source.Length + "；SHA256=" + result.Sha256);
                    token.ThrowIfCancellationRequested();
                    result.SoftwareEnvironment = await preflight();
                    if (result.SoftwareEnvironment == null || result.SoftwareEnvironment.Status != SoftwareEnvironmentStatus.Ready)
                    { result.Message = "软件环境未就绪，插件未安装。"; return result; }
                    token.ThrowIfCancellationRequested();
                    uploadTiming = timing.Measure("UploadIntegrity");
                    CheckSpace(session, checked(source.Length + PluginPackageInspection.SafetyMargin), report);
                    remote = RemoteTempSession.Create(); result.RemoteDirectory = remote.Directory;
                    session.CreateDirectory(remote); directoryAttempted = true;
                    result.Status = InstallStatus.FailedUpload;
                    report("2/7 上传插件", "远程临时目录：" + remote.Directory + "；SFTP 上传开始。");
                    long lastReported = -1, totalBytes = source.Length; var uploadSync = new object();
                    session.Upload(source, remote, uploaded =>
                    {
                        lock (uploadSync)
                        {
                            long percent = uploaded * 100 / totalBytes;
                            if (percent <= lastReported) return;
                            lastReported = percent;
                            if (progress != null) progress.Report(new InstallProgress { Step = "2/7 上传插件", UploadedBytes = uploaded, TotalBytes = totalBytes });
                        }
                    }, token);
                    token.ThrowIfCancellationRequested();
                    result.Status = InstallStatus.FailedIntegrity;
                    report("3/7 SHA256", "上传完成，正在核对远程大小和 SHA256。");
                    if (session.RemoteFileSize(remote) != source.Length) throw new InvalidDataException("上传文件大小不一致，禁止安装。");
                    if (PluginPackageInspection.ComputeSha256(source) != result.Sha256) throw new InvalidDataException("本地文件在上传期间变化，禁止安装。");
                    ProbeResult hash = session.RemoteHash(remote);
                    if (hash.ExitStatus == 127) warning("路由器没有 sha256sum；使用文件大小和 Makeself --check 校验，不安装额外工具。");
                    else if (hash.ExitStatus != 0 || PluginPackageInspection.ParseSha256(hash.Output) != result.Sha256)
                        throw new InvalidDataException("远程 SHA256 不匹配或读取失败，禁止安装。");
                    else report("3/7 SHA256", "✓ 上传文件 SHA256 校验通过。");
                    token.ThrowIfCancellationRequested();
                    report("4/7 Makeself", "读取 Makeself --info。");
                    ProbeResult info = session.Inspect(remote, false); LogCommand(report, "Makeself --info", info);
                    if (info.ExitStatus != 0) throw new InvalidDataException("Makeself --info 执行失败。");
                    result.Archive = PluginPackageInspection.ParseInfo(info.Output);
                    if (result.Archive.Architecture != null && result.Archive.Architecture != result.Plugin.Architecture)
                        throw new InvalidDataException("Makeself Target directory 与文件名架构冲突，禁止安装。");
                    if (result.Archive.SdkSeries != null && result.Archive.SdkSeries != result.Plugin.SdkSeries)
                        throw new InvalidDataException("Makeself Identification 与文件名 SDK 冲突，禁止安装。");
                    if (result.Archive.Architecture == null) warning("Makeself 信息未提供可用架构；仍依据重新读取的文件名与目标设备判断。");
                    long unpack = result.Archive.UncompressedBytes ?? checked(source.Length * 3);
                    if (!result.Archive.UncompressedBytes.HasValue) warning("无法从 Makeself 信息确定解压大小；按安装包大小的 3 倍另加 32 MiB 检查剩余空间。");
                    result.Status = InstallStatus.FailedPrecheck;
                    CheckSpace(session, checked(unpack + PluginPackageInspection.SafetyMargin), report);
                    token.ThrowIfCancellationRequested();
                    result.Status = InstallStatus.FailedIntegrity;
                    ProbeResult check = session.Inspect(remote, true); LogCommand(report, "Makeself --check", check);
                    if (!PluginPackageInspection.IntegrityPassed(check)) throw new InvalidDataException("安装包完整性验证失败，禁止安装。");
                    result.Status = InstallStatus.FailedPrecheck;
                    token.ThrowIfCancellationRequested();
                    report("4/7 安装准备", "检查 shell 内建 HUP trap；日志和结束状态通过受信任 SFTP 直接读取。");
                    session.CheckRunner();
                    report("4/7 安装准备", "✓ 后台启动能力检查通过；读取 " + router.PackageManager + " 软件包快照。");
                    uploadTiming.Dispose(); uploadTiming = null;
                    PackageSnapshot preview;
                    using (timing.Measure("SnapshotBefore")) preview = session.Snapshot(router.PackageManager);
                    string known = PluginVerificationService.KnownMainPackage(result.Plugin), existingVersion = null;
                    bool exists = known != null && preview.Packages.TryGetValue(known, out existingVersion) && session.PackageExists(router.PackageManager, known);
                    if (exists) report("等待安装确认", "检测到 " + result.Plugin.PluginName + " 已经安装：" + known + " " + existingVersion + "；需要明确确认继续安装 / 更新。");
                    var summary = new InstallConfirmation { Plugin = result.Plugin, Router = router, Sha256 = result.Sha256, SoftwareEnvironment = result.SoftwareEnvironment, ExistingPackage = exists ? known : null, ExistingVersion = existingVersion };
                    if (confirm == null || !await confirm(summary))
                    { result.Status = exists ? InstallStatus.AlreadyInstalled : InstallStatus.Cancelled; result.Message = exists ? "已保留现有插件，未执行安装。" : "已取消安装。"; return result; }
                    token.ThrowIfCancellationRequested();
                    // Snapshot immediately before launch, after the potentially lengthy user dialog.
                    using (timing.Measure("SnapshotBefore")) result.BeforePackages = session.Snapshot(router.PackageManager);
                    if (!exists && known != null && result.BeforePackages.Packages.ContainsKey(known))
                        throw new InvalidOperationException("确认期间主插件已被其他操作安装，请重新检查后确认。");
                    LogSnapshot(report, "BeforePackages", result.BeforePackages);
                    CheckSpace(session, checked(unpack + PluginPackageInspection.SafetyMargin), report);
                    token.ThrowIfCancellationRequested();
                    // Mark before dispatch: a lost acknowledgement must never cause a retry or cleanup.
                    result.InstallerStarted = true;
                    report("5/7 正在安装", "安装正在进行，请勿关闭程序或断开路由器电源。执行作者原始 sh package.run；不会自动重试。");
                    using (timing.Measure("InstallerRuntime"))
                    try
                    {
                        session.StartInstaller(remote);
                        await Monitor(session, remote, result, report);
                    }
                    catch (Exception exception)
                    { warning("安装状态未知：" + session.Sanitize(exception.Message) + "；未重试、未强杀安装器。"); }
                    report("6/7 独立验证", "读取 AfterPackages 并独立查询目标主 package。");
                    using (timing.Measure("SnapshotAfterVerify"))
                    try
                    {
                        result.AfterPackages = session.Snapshot(router.PackageManager);
                        LogSnapshot(report, "AfterPackages", result.AfterPackages);
                        result.Diff = PackageSnapshotService.Compare(result.BeforePackages, result.AfterPackages);
                        LogDiff(report, result.Diff);
                        result.Verification = new PluginVerificationService().Verify(result.Plugin, result.AfterPackages, result.Diff, name => session.PackageExists(router.PackageManager, name));
                        report("6/7 独立验证", result.Verification.Message);
                        if (result.BeforePackages.Packages.ContainsKey("luci-app-openclash") && !session.PackageExists(router.PackageManager, "luci-app-openclash"))
                        {
                            warning("安装前存在的 luci-app-openclash 未通过安装后存在性检查；请查看日志。");
                            result.Verification.Verified = false; result.Verification.StrongRule = true;
                        }
                        else if (result.BeforePackages.Packages.ContainsKey("luci-app-openclash")) report("6/7 独立验证", "luci-app-openclash 仍存在；未执行代理控制命令。");
                    }
                    catch (Exception exception) { warning("安装后验证不完整：" + session.Sanitize(exception.Message)); }
                    if (!result.InstallerEnded) { result.Status = InstallStatus.InstallerStateUnknown; result.Message = "安装状态未知；请勿重复安装，先查看日志及路由器状态。"; }
                    else if (result.InstallerExitCode != 0) { result.Status = InstallStatus.FailedInstaller; result.Message = "安装程序执行失败；请勿重复安装，先查看日志。"; }
                    else if (result.Verification == null) { result.Status = InstallStatus.FailedVerification; result.Message = "安装程序已结束，独立验证失败；请勿重复安装，先查看日志。"; }
                    else if (result.Verification.Verified) { result.Status = InstallStatus.SuccessVerified; result.Message = "✓ " + result.Plugin.PluginName + " 安装成功。"; }
                    else if (result.Verification.StrongRule) { result.Status = InstallStatus.FailedVerification; result.Message = "目标主 package 未通过独立验证；请勿重复安装，先查看日志。"; }
                    else { result.Status = InstallStatus.SuccessWithWarning; result.Message = "△ 安装完成，但自动验证不完整；请查看安装日志 / LuCI 菜单确认。"; }
                }
            }
            catch (OperationCanceledException) { result.Status = InstallStatus.Cancelled; result.Message = "已取消安装前准备，未执行安装器。"; }
            catch (Exception exception)
            { result.Message = "✗ " + session.Sanitize(exception.Message); report("操作停止", result.Message); }
            finally
            {
                if (uploadTiming != null) uploadTiming.Dispose();
                using (timing.Measure("RestoreCleanup"))
                {
                if (directoryAttempted && remote != null)
                {
                    if (result.InstallerStarted && !result.InstallerEnded)
                        warning("未确认安装器结束，保留远程临时目录：" + remote.Directory + "；不能提前删除安装文件。");
                    else
                    {
                        report("7/7 正在清理", "仅清理本次远程 session：" + remote.Directory);
                        try
                        {
                            report("7/7 正在清理", "/tmp 清理前 Available=" + session.TmpAvailable());
                        }
                        catch (Exception exception) { warning("清理前空间读数不可用：" + session.Sanitize(exception.Message)); }
                        try { result.CleanupSucceeded = session.Cleanup(remote); }
                        catch (Exception exception) { warning("清理异常：" + session.Sanitize(exception.Message)); }
                        if (!result.CleanupSucceeded) warning("远程临时文件未确认清理；请检查：" + remote.Directory);
                        else report("7/7 正在清理", "远程 session 与 package.run 已确认不存在。");
                        try { report("7/7 正在清理", "/tmp 清理后 Available=" + session.TmpAvailable()); }
                        catch (Exception exception) { warning("清理后空间读数不可用：" + session.Sanitize(exception.Message)); }
                    }
                }
                report("完成", "最终状态=" + result.Status + "；" + result.Message + "；installer exit=" + result.InstallerExitCode + "；临时文件=" + (result.CleanupSucceeded ? "已清理" : remote == null ? "未创建" : "未确认清理"));
                }
                if (timingSession == null) report("Timing", timing.Summary());
            }
            return result;
        }
        private async Task Monitor(IPluginInstallSession session, RemoteTempSession remote, PluginInstallResult result, Action<string, string> report)
        {
            long stdout = 0, stderr = 0; var timer = Stopwatch.StartNew(); TimeSpan lastActivity = TimeSpan.Zero;
            Decoder outDecoder = Encoding.UTF8.GetDecoder(), errDecoder = Encoding.UTF8.GetDecoder();
            var outPending = new StringBuilder(); var errPending = new StringBuilder();
            using (var monitoring = InstallerMonitoring.Open(session, remote))
            {
            try
            {
            while (true)
            {
                InstallerPoll poll = session.PollInstaller(remote, stdout, stderr);
                if (!string.IsNullOrEmpty(poll.Diagnostic)) report("5/7 正在安装", poll.Diagnostic);
                if (poll.Stdout.Length > 0 || poll.Stderr.Length > 0) lastActivity = timer.Elapsed;
                Emit(report, "stdout", outDecoder, outPending, poll.Stdout, false); Emit(report, "stderr", errDecoder, errPending, poll.Stderr, false);
                stdout += poll.Stdout.Length; stderr += poll.Stderr.Length;
                if (poll.ExitCode.HasValue && poll.Stdout.Length < 65536 && poll.Stderr.Length < 65536)
                {
                    result.InstallerEnded = true; result.InstallerExitCode = poll.ExitCode;
                    Emit(report, "stdout", outDecoder, outPending, new byte[0], true); Emit(report, "stderr", errDecoder, errPending, new byte[0], true);
                    report("5/7 正在安装", "安装器已结束；exit=" + poll.ExitCode); return;
                }
                if (timer.Elapsed > OverallLimit || timer.Elapsed - lastActivity > InactiveLimit)
                    throw new TimeoutException("监测超过上限（无输出 15 分钟 / 总计 30 分钟），远程安装器未被终止。");
                await Task.Delay(PollInterval); // Deliberately no cancellation after installer dispatch.
            }
            }
            finally
            {
                Emit(report, "stdout", outDecoder, outPending, new byte[0], true);
                Emit(report, "stderr", errDecoder, errPending, new byte[0], true);
                var statistics = monitoring as IInstallerMonitorStatistics;
                if (statistics != null) report("5/7 正在安装", "Installer monitoring SFTP connection count = " + statistics.ConnectionCount);
            }
            }
        }
        private static void Emit(Action<string, string> report, string channel, Decoder decoder, StringBuilder pending, byte[] data, bool flush)
        {
            char[] chars = new char[data.Length + 4]; int count = decoder.GetChars(data, 0, data.Length, chars, 0, flush);
            pending.Append(chars, 0, count);
            // Redact complete lines so a password spanning network chunks cannot leak in fragments.
            int consumed = 0;
            for (int i = 0; i < pending.Length; i++)
                if (pending[i] == '\n' || pending[i] == '\r')
                {
                    if (i > consumed) report("5/7 正在安装", "[" + channel + "] " + pending.ToString(consumed, i - consumed));
                    consumed = i + 1;
                }
            if (consumed > 0) pending.Remove(0, consumed);
            if (flush && pending.Length > 0) { report("5/7 正在安装", "[" + channel + "] " + pending); pending.Clear(); }
        }
        private static void CheckSpace(IPluginInstallSession session, long required, Action<string, string> report)
        {
            long available = session.TmpAvailable(); report("空间检查", "/tmp Available=" + available + " bytes；需要大于 " + required + " bytes。");
            if (available <= required) throw new IOException("路由器 /tmp 空间不足，无法上传或解压安装包。");
        }
        private static void LogCommand(Action<string, string> report, string name, ProbeResult result)
        { report("4/7 Makeself", name + " stdout：\n" + result.Output + "\nstderr：\n" + result.Error + "\nexit=" + result.ExitStatus); }
        internal static void LogSnapshot(Action<string, string> report, string name, PackageSnapshot snapshot)
        {
            report("Package Snapshot", name + ": " + snapshot.Packages.Count + " packages（完整快照保留在内存）");
        }
        internal static void LogDiff(Action<string, string> report, PackageDiff diff)
        {
            var text = new StringBuilder("Package Diff：新增 " + diff.Added.Count + "；升级 / 版本变化 " + diff.Updated.Count + "；删除 " + diff.Removed.Count + "\n");
            foreach (var p in diff.Added) text.AppendLine("Added " + p.Name + " " + p.AfterVersion);
            foreach (var p in diff.Updated) text.AppendLine("Updated " + p.Name + " " + p.BeforeVersion + " -> " + p.AfterVersion);
            foreach (var p in diff.Removed) text.AppendLine("Removed " + p.Name + " " + p.BeforeVersion);
            report("Package Diff", text.ToString());
        }
    }
}
