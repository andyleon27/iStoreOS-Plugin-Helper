using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // V0.3.2 composes the proven V0.3 engine. The engine itself is unchanged.
    public sealed class InstallationWorkflowService
    {
        public TimeSpan BusyInterval { get; set; }
        public int BusyChecks { get; set; }
        public TimeSpan ReconnectInterval { get; set; }
        public int ReconnectAttempts { get; set; }
        public PluginInstallationService Engine { get; set; }
        private Context unfinished;
        private string failedFileHash;
        private string failedFingerprint;
        private bool failedFileWasDispatched;
        private bool running;
        public bool HasPendingWork { get { lock (this) return unfinished != null; } }
        public bool HasPendingInstaller { get { lock (this) return unfinished != null && unfinished.Dispatched && !unfinished.Finished; } }
        public InstallationWorkflowService()
        { BusyInterval = TimeSpan.FromSeconds(2); BusyChecks = 7; ReconnectInterval = TimeSpan.FromSeconds(2); ReconnectAttempts = 3; Engine = new PluginInstallationService(); }

        private sealed class Candidate
        { public string Key; public SoftwareSourceInfo Source; public string Text; public List<RepositoryFeed> Feeds; }
        private sealed class Context
        {
            public IInstallationWorkflowSession Session;
            public RouterEnvironment Router;
            public SoftwareSourceSnapshot Original;
            public string ExpectedText, Backup, FilePath, Hash;
            public string ReconciledLog;
            public bool Mutated, Dispatched, StrictHash, Finished, UsingBackup;
            public RemoteTempSession Active;
            public PluginInstallResult ActiveResult;
            public readonly List<RemoteTempSession> Directories = new List<RemoteTempSession>();
            public IProgress<InstallationWorkflowProgress> Progress;
            public InstallationWorkflowResult Result;
            public CancellationToken Token;
            public InstallationState State;
            public void Report(InstallationState state, string log = null, InstallProgress transfer = null)
            {
                State = state; Result.State = state;
                if (Result.States.Count == 0 || Result.States.Last() != state) Result.States.Add(state);
                if (Progress != null) Progress.Report(new InstallationWorkflowProgress { State = state, Message = state == InstallationState.SourceProbe && UsingBackup ? "当前镜像连接异常，正在尝试备用镜像…" : Message(state),
                    Log = log == null ? null : Session.Sanitize(log), InstallerDispatched = Dispatched, Transfer = transfer, Software = Result.Software });
            }
            public CancellationToken PreparationToken { get { return Dispatched ? CancellationToken.None : Token; } }
        }
        public async Task<InstallationWorkflowResult> RunAsync(IInstallationWorkflowSession session, RouterEnvironment expected, string filePath,
            IProgress<InstallationWorkflowProgress> progress, CancellationToken token)
        {
            lock (this) { if (running) throw new InvalidOperationException("已有安装流程正在执行。"); running = true; }
            var result = new InstallationWorkflowResult();
            var context = new Context { Session = session, Router = expected, FilePath = filePath, Token = token, Progress = progress, Result = result };
            try
            {
                context.Report(InstallationState.Preflight, "新一轮安装：清空候选缓存，重新检查 SSH、环境、网络、时间、空间和包管理器。");
                await EnsureConnection(context, false);
                result.Router = context.Router;
                if (unfinished != null)
                {
                    // A prior dispatch/restoration ambiguity belongs to the original device and file.
                    Context previous = unfinished;
                    if (!SameRouter(previous.Router, context.Router)) throw new InstallationPolicyException(InstallationFailureKind.IdentityChanged, "不能在另一台设备处理上轮安装。");
                    previous.Session = session; previous.Progress = progress; previous.Token = CancellationToken.None; previous.Result = result;
                    result.Router = previous.Router; result.SourceChanged = previous.Mutated; result.OriginalHash = TextHash(previous.Original.OriginalText); result.BackupPath = previous.Backup;
                    if (previous.Dispatched && !previous.Finished)
                    {
                        context = previous;
                        await Reconcile(context);
                        result.Installation = context.ActiveResult;
                        if (!context.Finished)
                        { result.PendingInstaller = true; SetFailure(context, InstallFailureClassifier.Failure(InstallationFailureKind.ConnectionLost, "旧安装器未确认结束；本次不会执行第二个安装器。")); return result; }
                        if (context.ActiveResult.Verification != null && context.ActiveResult.Verification.Verified && context.ActiveResult.InstallerExitCode == 0)
                        { MarkSuccess(context, "重连后旧安装器已结束，主 package 独立验证通过；没有重复执行 .run。"); return result; }
                        var failure = context.ActiveResult.InstallerExitCode != 0 ? InstallFailureClassifier.Classify(context.ReconciledLog, previous.Original.OfficialFeeds, previous.Original.AdditionalFeeds) :
                            InstallFailureClassifier.Failure(InstallationFailureKind.VerificationIncomplete);
                        if (!failure.CanRotateOfficial) { SetFailure(context, failure); return result; }
                        // This deliberate retry is a new round, but only after the old runner ended and its source is restored.
                        await FinalizeContext(previous);
                        if (previous.Mutated && !result.SourceRestored) { SetFailure(context, InstallFailureClassifier.Failure(InstallationFailureKind.Tool, "旧流程原源尚未恢复。")); return result; }
                        context = new Context { Session = session, Router = previous.Router, FilePath = filePath, Token = token, Progress = progress, Result = result };
                        context.Report(InstallationState.Preflight, "旧安装器已结束且仅有官方下载错误；新一轮重新预检和探源。");
                        unfinished = null;
                    }
                    if (unfinished != null) await FinalizeContext(previous);
                    if (previous.Mutated && !result.SourceRestored)
                        throw new InstallationPolicyException(InstallationFailureKind.Tool, "上轮原源仍未恢复，禁止把临时镜像当作新基线。");
                    unfinished = null;
                }
                var plugin = new PluginPackageAnalyzer().AnalyzeFile(filePath);
                new CompatibilityService().Evaluate(plugin, context.Router);
                if (plugin.CompatibilityStatus != CompatibilityStatus.Compatible || context.Router.PackageManager == PackageManagerKind.Unknown)
                    throw new InstallationPolicyException(InstallationFailureKind.Compatibility, plugin.CompatibilityMessage);
                using (var lockedFile = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (lockedFile.Length == 0) throw new InstallationPolicyException(InstallationFailureKind.Integrity, "安装包为空。");
                    context.Hash = PluginPackageInspection.ComputeSha256(lockedFile);
                    var sources = new RecoveringSources(this, context);
                    context.Original = new SoftwareSourceService().Read(sources, context.Router.PackageManager, log => context.Report(InstallationState.Preflight, log));
                    context.Original.CurrentSource = new QuickStartService().ReadCurrent(sources, log => context.Report(InstallationState.Preflight, log));
                    context.ExpectedText = context.Original.OriginalText; result.OriginalHash = TextHash(context.ExpectedText);
                    var dynamicSources = new QuickStartService().ReadCandidates(sources, log => context.Report(InstallationState.Preflight, log));
                    if (context.Original.CurrentSource != null && !MatchesMirror(context.Original.CurrentSource, context.Original.OfficialFeeds))
                    { context.Report(InstallationState.Preflight, "QuickStart 当前 identity 与实际 distfeeds URL 不一致；不使用此 identity 去重或决定安装源，按真实配置探测。"); context.Original.CurrentSource = null; }
                    var readinessFeeds = context.Original.OfficialFeeds.Concat(context.Original.AdditionalFeeds).ToList();
                    if (!string.IsNullOrEmpty(context.Original.RomText))
                        foreach (var source in dynamicSources) readinessFeeds.AddRange(SoftwareSourceService.ParseFeeds(SoftwareSourceService.RebaseRom(context.Original.RomText, source.Url), context.Router.PackageManager, context.Original.DistfeedsPath, RepositoryKind.Official));
                    await CheckReadiness(context, readinessFeeds, lockedFile.Length);
                    await WaitForManager(context);
                    context.Report(InstallationState.SourceProbe, "重新探测当前源及 QuickStart 动态候选；不使用硬编码镜像地址。");
                    result.Software = new SoftwareEnvironmentResult { CurrentSource = context.Original.CurrentSource };
                    var probes = new RepositoryProbeService();
                    Action<string, string> probeLog = (step, log) => context.Report(InstallationState.SourceProbe, log);
                    result.Software.OfficialHealth = probes.Probe(sources, context.Original.OfficialFeeds, probeLog, context.PreparationToken, "当前官方源");
                    result.Software.AdditionalHealth = probes.Probe(sources, context.Original.AdditionalFeeds, probeLog, context.PreparationToken, "辅助 / 自定义源");
                    var full = new List<Candidate>(); var configurations = new HashSet<string>(StringComparer.Ordinal); var mirrors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (result.Software.OfficialHealth.Status == RepositoryHealth.FULL)
                    {
                        var current = new Candidate { Key = "Current:" + TextHash(context.Original.OriginalText), Source = context.Original.CurrentSource, Text = context.Original.OriginalText, Feeds = context.Original.OfficialFeeds };
                        full.Add(current); configurations.Add(FeedKey(current.Feeds));
                        if (current.Source != null) mirrors.Add(current.Source.Url.TrimEnd('/'));
                    }
                    foreach (var source in dynamicSources.OrderBy(s => new Uri(s.Url).Scheme == "https" ? 0 : 1).ThenBy(s => s.Identity, StringComparer.Ordinal).ThenBy(s => s.Url, StringComparer.Ordinal))
                    {
                        context.PreparationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(context.Original.RomText)) break;
                        if (!mirrors.Add(source.Url.TrimEnd('/'))) continue;
                        string text = SoftwareSourceService.RebaseRom(context.Original.RomText, source.Url);
                        var feeds = SoftwareSourceService.ParseFeeds(text, context.Router.PackageManager, context.Original.DistfeedsPath, RepositoryKind.Official);
                        string key = FeedKey(feeds); if (!configurations.Add(key)) continue;
                        var health = probes.Probe(sources, feeds, probeLog, context.PreparationToken, "候选 " + source.Identity);
                        result.Software.Candidates.Add(new SoftwareRepairPlan { OriginalSource = context.Original.CurrentSource, ProposedSource = source, Probe = health });
                        if (health.Status == RepositoryHealth.FULL)
                        {
                            if (key == FeedKey(context.Original.OfficialFeeds))
                            {
                                // A transient probe may recover when the same actual indexes are tested again.
                                // Keep the user's exact text (including comments), and prefer it without a POST.
                                result.Software.OfficialHealth = health;
                                full.Insert(0, new Candidate { Key = "Current:" + TextHash(context.Original.OriginalText), Source = context.Original.CurrentSource ?? source, Text = context.Original.OriginalText, Feeds = context.Original.OfficialFeeds });
                            }
                            else full.Add(new Candidate { Key = source.Identity + "|" + source.Url, Source = source, Text = text, Feeds = feeds });
                        }
                    }
                    // Retry checks installed state only after fresh readiness and source probes.
                    if (failedFileHash == context.Hash && failedFingerprint == context.Router.HostKeyFingerprint)
                    {
                        context.Report(InstallationState.Verifying, "新一轮已重新预检 / 探源；检查上一轮之后插件是否已完成。");
                        var existing = VerifyExisting(context, plugin);
                        if (existing != null) { result.Installation = existing; MarkSuccess(context, "目标版本已独立验证，未重新执行 .run。"); return result; }
                    }
                    if (full.Count == 0)
                    { SetFailure(context, InstallFailureClassifier.Failure(InstallationFailureKind.MirrorsExhausted, "没有 FULL 官方源可供本轮安装。")); return result; }
                    foreach (Candidate candidate in full)
                    {
                        context.PreparationToken.ThrowIfCancellationRequested();
                        await EnsureConnection(context, false); await WaitForManager(context);
                        if (candidate.Text != context.ExpectedText)
                        {
                            await CheckReadiness(context, readinessFeeds, lockedFile.Length);
                            try { await SelectSource(context, sources, candidate); }
                            catch (InstallationPolicyException ex)
                            {
                                if (ex.Kind != InstallationFailureKind.OfficialRepository) throw;
                                context.Report(InstallationState.SourceProbe, "切换后索引未通过，继续下一个尚未尝试的 FULL 候选。"); continue;
                            }
                        }
                        context.Report(InstallationState.SourceProbe, "本轮源=" + candidate.Key + "；运行真实 package update。");
                        var update = new PackageManagerService().Update(sources, context.Router.PackageManager, candidate.Feeds, context.Original.AdditionalFeeds,
                            (step, log) => context.Report(InstallationState.SourceProbe, log));
                        result.Software.Updates.Add(update);
                        if (!update.Success)
                        {
                            var failure = InstallFailureClassifier.Classify((update.Stdout ?? "") + "\n" + (update.Stderr ?? ""), candidate.Feeds, context.Original.AdditionalFeeds);
                            if (failure.Kind == InstallationFailureKind.UnknownInstaller) failure = InstallFailureClassifier.Failure(InstallationFailureKind.UpdateFailure, (update.Stdout ?? "") + "\n" + (update.Stderr ?? ""));
                            if (!failure.CanRotateOfficial) { SetFailure(context, failure); return result; }
                            context.Report(InstallationState.SourceProbe, "此源 update 有明确官方仓库错误，不执行 .run，继续下一个唯一 FULL 源。"); continue;
                        }
                        result.Software.Status = SoftwareEnvironmentStatus.Ready; result.Software.Message = "软件源检查与 package update 通过。";
                        result.Software.CurrentSource = candidate.Source;
                        context.Report(InstallationState.SourceReady, "源已就绪：" + candidate.Key + "；每个唯一配置最多执行一次 .run。");
                        var logs = new StringBuilder();
                        var adapter = new WorkflowInstaller(this, context, candidate.Feeds, result.Attempts.Count > 0 ||
                            (failedFileWasDispatched && failedFileHash == context.Hash && failedFingerprint == context.Router.HostKeyFingerprint));
                        var engineProgress = new InlineProgress<InstallProgress>(item => {
                            if (!string.IsNullOrEmpty(item.Log)) logs.AppendLine(item.Log);
                            var state = item.InstallerStarted ? item.Step.Contains("验证") || item.Step.Contains("Snapshot") || item.Step.Contains("Diff") ? InstallationState.Verifying : InstallationState.Installing :
                                item.Step.Contains("上传") ? InstallationState.Uploading : item.Step.Contains("SHA256") || item.Step.Contains("Makeself") || item.Step.Contains("确认") ? InstallationState.IntegrityCheck : context.State;
                            // Engine cleanup is deliberately deferred until source restoration.
                            if (item.Step.Contains("清理") || item.Step == "完成") { if (item.Log != null) context.Report(context.State, "原安装服务收尾（目录清理由编排层在原源恢复后执行）：" + item.Log.Replace("已确认不存在", "尚未清理").Replace("临时文件=已清理", "临时文件=待编排层清理")); return; }
                            context.Report(state, item.Log, item);
                        });
                        PluginInstallResult installed = await Engine.RunAsync(adapter, context.Router, filePath, () => Task.FromResult(result.Software),
                            summary => Task.FromResult(true), engineProgress, context.PreparationToken);
                        // The legacy engine marks before dispatch; a wrapper policy veto proves no dispatch happened.
                        if (!adapter.DidDispatch && installed.InstallerStarted) { installed.InstallerStarted = false; installed.Status = InstallStatus.FailedPrecheck; }
                        installed.CleanupSucceeded = false; context.ActiveResult = installed; context.Finished = !installed.InstallerStarted || installed.InstallerEnded;
                        result.Installation = installed;
                        var attempt = new InstallationAttempt { SourceKey = candidate.Key, Result = installed }; result.Attempts.Add(attempt);
                        if (!context.Finished)
                        { result.PendingInstaller = true; SetFailure(context, InstallFailureClassifier.Failure(InstallationFailureKind.ConnectionLost, "安装器未确认结束，禁止第二次执行与提前删除文件。")); return result; }
                        if (installed.Status == InstallStatus.SuccessVerified)
                        { MarkSuccess(context, "主 package 独立验证成功。"); return result; }
                        if (installed.Status == InstallStatus.SuccessWithWarning)
                        { SetFailure(context, InstallFailureClassifier.Failure(InstallationFailureKind.VerificationIncomplete)); return result; }
                        attempt.Failure = adapter.PolicyFailure ?? FailureFor(installed, logs.ToString(), candidate.Feeds, context.Original.AdditionalFeeds);
                        if (!attempt.Failure.CanRotateOfficial || !installed.InstallerEnded || !context.StrictHash)
                        { SetFailure(context, attempt.Failure); return result; }
                        context.Report(InstallationState.SourceProbe, "官方仓库下载失败；已记录此轮 package diff，保留已安装依赖，有限续装下一个 FULL 源。");
                    }
                    SetFailure(context, InstallFailureClassifier.Failure(InstallationFailureKind.MirrorsExhausted));
                }
            }
            catch (OperationCanceledException) { SetFailure(context, InstallFailureClassifier.Failure(InstallationFailureKind.Cancelled)); }
            catch (InstallationPolicyException ex) { SetFailure(context, InstallFailureClassifier.Failure(ex.Kind, ex.Message)); }
            catch (Exception ex) { SetFailure(context, InstallFailureClassifier.Failure(session.IsConnected ? InstallationFailureKind.Tool : InstallationFailureKind.ConnectionLost, session.Sanitize(ex.ToString()))); }
            finally
            {
                InstallationState terminal = result.State;
                try { FinalizeContext(context).GetAwaiter().GetResult(); }
                catch (Exception ex) { result.Warnings.Add("收尾异常：" + session.Sanitize(ex.Message)); }
                if (terminal == InstallationState.Success && result.Warnings.Count > 0 && result.Installation != null) result.Installation.Warnings.AddRange(result.Warnings);
                context.Report(terminal, "本轮结束：" + terminal + "；尝试次数=" + result.Attempts.Count + "；源修改=" + result.SourceChanged + "；原源恢复=" + result.SourceRestored);
                if (result.Failure != null && context.Hash != null) { failedFileWasDispatched = context.Dispatched || (failedFileWasDispatched && failedFileHash == context.Hash && failedFingerprint == context.Router.HostKeyFingerprint); failedFileHash = context.Hash; failedFingerprint = context.Router.HostKeyFingerprint; }
                if (result.State == InstallationState.Success) { failedFileHash = null; failedFingerprint = null; failedFileWasDispatched = false; }
                if (context.Original != null) unfinished = (context.Dispatched && !context.Finished) || (context.Mutated && !result.SourceRestored) ? context : null;
                lock (this) running = false;
            }
            return result;
        }
        private async Task EnsureConnection(Context c, bool force)
        {
            Exception last = null; var previous = c.State;
            for (int i = 0; i < Math.Max(1, Math.Min(5, ReconnectAttempts)); i++)
            {
                c.PreparationToken.ThrowIfCancellationRequested();
                if (force || !c.Session.IsConnected) c.Report(InstallationState.ReconnectChecking, "SSH 自动恢复 " + (i + 1) + "；重新核对受信任指纹和设备环境。");
                try
                {
                    RouterEnvironment actual = c.Session.ConnectAndDetect(c.Router, force, log => c.Report(c.State, log), c.PreparationToken);
                    if (!SameRouter(c.Router, actual)) throw new InstallationPolicyException(InstallationFailureKind.IdentityChanged, "重连的指纹 / SDK / 架构 / 包管理器与原设备不一致。");
                    c.Router = actual; c.Result.Router = actual; c.Report(previous); return;
                }
                catch (InstallationPolicyException) { throw; }
                catch (Exception ex) { last = ex; force = true; }
                if (i + 1 < Math.Max(1, Math.Min(5, ReconnectAttempts))) await Task.Delay(ReconnectInterval, c.PreparationToken);
            }
            throw new InstallationPolicyException(InstallationFailureKind.ConnectionLost, "有限重连未成功：" + c.Session.Sanitize(last == null ? "" : last.Message));
        }
        private static bool SameRouter(RouterEnvironment a, RouterEnvironment b)
        { return a != null && b != null && b.IsConnected && a.HostKeyFingerprint == b.HostKeyFingerprint && a.Architecture == b.Architecture && a.SdkSeries == b.SdkSeries && a.PackageManager == b.PackageManager; }
        private async Task WaitForManager(Context c)
        {
            for (int i = 0; i < Math.Max(1, Math.Min(30, BusyChecks)); i++)
            {
                c.PreparationToken.ThrowIfCancellationRequested();
                bool busy = Read(c, () => c.Session.PackageManagerBusy(c.Router.PackageManager));
                if (!busy) return;
                c.Report(c.State, "包管理器占用，有限等待 " + (i + 1) + "/" + Math.Max(1, Math.Min(30, BusyChecks)) + "；不终止进程。");
                if (i + 1 < Math.Max(1, Math.Min(30, BusyChecks))) await Task.Delay(BusyInterval, c.PreparationToken);
            }
            throw new InstallationPolicyException(InstallationFailureKind.PackageManagerBusy, "包管理器持续占用，等待超时。");
        }
        private async Task CheckReadiness(Context c, IList<RepositoryFeed> feeds, long bytes)
        {
            await EnsureConnection(c, false);
            var value = Read(c, () => c.Session.ReadReadiness(feeds));
            c.Report(InstallationState.Preflight, "Readiness：default route=" + value.DefaultRoute + "; DNS=" + value.DnsResolved + "/" + value.DnsChecked + "; epoch=" + value.UnixTime + "; /tmp=" + value.TmpBytes + "; persistent=" + value.PersistentBytes + "; readonly=" + value.PersistentReadOnly);
            var failure = InstallationReadiness.Check(value, bytes, InstallationReadiness.UnixNow);
            if (failure != null) throw new InstallationPolicyException(failure.Kind, failure.UserMessage);
        }
        private async Task SelectSource(Context c, ISoftwareSourceSession sources, Candidate candidate)
        {
            await EnsureConnection(c, false);
            c.UsingBackup = true;
            c.Report(InstallationState.SourceProbe, "当前镜像连接异常，正在尝试备用镜像；候选=" + candidate.Key);
            var before = sources.ReadOfficial(c.Router.PackageManager, false);
            if (before.ExitStatus != 0 || before.Output != c.ExpectedText) throw new InstallationPolicyException(InstallationFailureKind.Tool, "官方配置在流程外发生变化，停止切换。");
            if (c.Backup == null)
            {
                c.Backup = sources.BackupOfficial(c.Router.PackageManager); c.Result.BackupPath = c.Backup;
                var backup = sources.ReadBackup(c.Backup);
                if (backup.ExitStatus != 0 || backup.Output != c.Original.OriginalText) throw new InstallationPolicyException(InstallationFailureKind.Tool, "原文备份校验失败，未切源。");
            }
            c.Mutated = true; c.Result.SourceChanged = true; c.Result.Software.SwitchAttempted = true;
            bool acknowledged = false;
            try { acknowledged = new QuickStartService().Switch(sources, candidate.Source.Identity, log => c.Report(InstallationState.SourceProbe, log)); }
            catch (Exception ex) { c.Report(InstallationState.SourceProbe, "POST 应答中断，不重复 POST：" + c.Session.Sanitize(ex.Message)); EnsureConnection(c, !c.Session.IsConnected).GetAwaiter().GetResult(); }
            var current = sources.ReadOfficial(c.Router.PackageManager, false);
            var identity = new QuickStartService().ReadCurrent(sources, log => c.Report(InstallationState.SourceProbe, log));
            if (current.ExitStatus != 0 || identity == null || identity.Identity != candidate.Source.Identity || !SoftwareSourceService.MatchesFeeds(current.Output, candidate.Text, c.Router.PackageManager))
                throw new InstallationPolicyException(InstallationFailureKind.Tool, "临时换源未通过实际 identity / 完整索引路径验证。");
            c.ExpectedText = current.Output; c.Result.Software.SourceSwitchOccurred = current.Output != c.Original.OriginalText;
            c.Result.Software.CurrentSource = identity;
            var health = new RepositoryProbeService().Probe(sources, SoftwareSourceService.ParseFeeds(current.Output, c.Router.PackageManager, c.Original.DistfeedsPath, RepositoryKind.Official),
                (step, log) => c.Report(InstallationState.SourceProbe, log), c.PreparationToken, "切换后实际官方源");
            c.Result.Software.OfficialHealth = health;
            if (health.Status != RepositoryHealth.FULL) throw new InstallationPolicyException(InstallationFailureKind.OfficialRepository, "切换后的真实配置不再 FULL，停止本轮并恢复原文。");
            c.Report(InstallationState.SourceProbe, "临时源已实际复核；POST acknowledgement=" + acknowledged);
        }
        private async Task FinalizeContext(Context c)
        {
            if (c.Original == null) return;
            c.Token = CancellationToken.None; // Recovery and cleanup are independent of preparation cancellation.
            if (c.Dispatched && !c.Finished)
            {
                c.Result.PendingInstaller = true;
                c.Result.Warnings.Add("安装器未确认结束；保留 session 和原源备份，不在可能仍下载时修改源或删除安装文件。再次尝试将先检查旧安装器。"); return;
            }
            if (c.Mutated)
            {
                c.Result.SourceChanged = true; c.Result.RestoreAttempted = true; c.Report(InstallationState.RestoringSource, "恢复安装前官方 distfeeds 原文；hash=" + TextHash(c.Original.OriginalText));
                try
                {
                    await EnsureConnection(c, false); await WaitForManager(c);
                    var current = c.Session.Sources.ReadOfficial(c.Router.PackageManager, false);
                    if (current.ExitStatus != 0 || current.Output != c.Original.OriginalText)
                    {
                        var restored = c.Session.RestoreOriginal(c.Router.PackageManager, c.Original.OriginalText, c.Backup);
                        if (restored.ExitStatus != 0) throw new IOException("原源恢复写入失败。");
                    }
                    current = c.Session.Sources.ReadOfficial(c.Router.PackageManager, false);
                    c.Result.SourceRestored = current.ExitStatus == 0 && current.Output == c.Original.OriginalText && TextHash(current.Output) == c.Result.OriginalHash;
                    if (!c.Result.SourceRestored) throw new IOException("原源恢复后的完整文本 / hash 不一致。");
                    if (c.Result.Software != null) { c.Result.Software.RecoveryAttempted = true; c.Result.Software.RecoverySucceeded = true; c.Result.Software.CurrentSource = c.Original.CurrentSource; c.Result.Software.OfficialHealth = null;
                        c.Result.Software.Status = SoftwareEnvironmentStatus.Unknown; c.Result.Software.Message = "已恢复安装前官方配置；原源健康状态未重新探测，update 结果属于安装期间。"; }
                    c.Report(InstallationState.RestoringSource, "原官方配置原文与 SHA256 已一致；辅助 / 自定义配置未写入。");
                }
                catch (Exception ex) { c.Result.Warnings.Add("插件安装流程已结束，但原软件源恢复失败，请查看高级信息。备份：" + c.Backup + "；" + c.Session.Sanitize(ex.Message)); }
            }
            c.Report(InstallationState.Cleaning, "恢复步骤结束，清理本轮已结束的独占 session。");
            bool cleaned = true;
            foreach (RemoteTempSession remote in c.Directories.ToArray())
            {
                try
                {
                    await EnsureConnection(c, false);
                    if (!c.Session.Installer.Cleanup(remote)) throw new IOException("目录未确认消失。");
                    c.Directories.Remove(remote); c.Report(InstallationState.Cleaning, "已清理：" + remote.Directory);
                }
                catch (Exception ex) { cleaned = false; c.Result.Warnings.Add("临时文件清理未确认：" + remote.Directory + "；" + c.Session.Sanitize(ex.Message)); }
            }
            if (c.Result.Installation != null) c.Result.Installation.CleanupSucceeded = cleaned && c.Directories.Count == 0;
            if (c.Backup != null && (!c.Mutated || c.Result.SourceRestored))
            {
                try { if (!c.Session.DeleteBackup(c.Backup)) c.Result.Warnings.Add("原配置已保留 / 恢复，但临时备份目录清理失败：" + c.Backup); }
                catch (Exception ex) { c.Result.Warnings.Add("备份清理失败：" + c.Session.Sanitize(ex.Message)); }
            }
            foreach (string warning in c.Result.Warnings) c.Report(InstallationState.Cleaning, "Warning：" + warning);
        }
        private async Task Reconcile(Context c)
        {
            c.Report(InstallationState.ReconnectChecking, "先监测上轮 session，不执行新的 .run；remote=" + c.Active.Directory);
            await EnsureConnection(c, false);
            var watch = Stopwatch.StartNew(); long stdout = 0, stderr = 0;
            var outText = new StringBuilder(); var errText = new StringBuilder();
            var outDecoder = Encoding.UTF8.GetDecoder(); var errDecoder = Encoding.UTF8.GetDecoder();
            while (watch.Elapsed <= Engine.OverallLimit)
            {
                var poll = Read(c, () => c.Session.Installer.PollInstaller(c.Active, stdout, stderr));
                char[] outChars = new char[poll.Stdout.Length + 4], errChars = new char[poll.Stderr.Length + 4];
                outText.Append(outChars, 0, outDecoder.GetChars(poll.Stdout, 0, poll.Stdout.Length, outChars, 0));
                errText.Append(errChars, 0, errDecoder.GetChars(poll.Stderr, 0, poll.Stderr.Length, errChars, 0));
                stdout += poll.Stdout.Length; stderr += poll.Stderr.Length;
                if (poll.ExitCode.HasValue && poll.Stdout.Length < 65536 && poll.Stderr.Length < 65536)
                { c.ActiveResult.InstallerEnded = true; c.ActiveResult.InstallerExitCode = poll.ExitCode; c.Finished = true; break; }
                await Task.Delay(Engine.PollInterval);
            }
            if (outText.Length > 0) c.Report(InstallationState.ReconnectChecking, "旧安装器完整 stdout：\n" + outText);
            if (errText.Length > 0) c.Report(InstallationState.ReconnectChecking, "旧安装器完整 stderr：\n" + errText);
            c.ReconciledLog = outText + "\n" + errText;
            if (!c.Finished) return;
            await WaitForManager(c); c.Report(InstallationState.Verifying, "旧安装器已结束，读取新快照并独立验证。");
            var after = c.Session.Installer.Snapshot(c.Router.PackageManager);
            c.ActiveResult.AfterPackages = after;
            c.ActiveResult.Diff = PackageSnapshotService.Compare(c.ActiveResult.BeforePackages, after);
            c.ActiveResult.Verification = new PluginVerificationService().Verify(c.ActiveResult.Plugin, after, c.ActiveResult.Diff, name => c.Session.Installer.PackageExists(c.Router.PackageManager, name));
            if (c.ActiveResult.BeforePackages.Packages.ContainsKey("luci-app-openclash") && !c.Session.Installer.PackageExists(c.Router.PackageManager, "luci-app-openclash")) c.ActiveResult.Verification.Verified = false;
            c.ActiveResult.Status = c.ActiveResult.InstallerExitCode == 0 && c.ActiveResult.Verification.Verified ? InstallStatus.SuccessVerified : InstallStatus.FailedVerification;
        }
        private static PluginInstallResult VerifyExisting(Context c, PluginPackageInfo plugin)
        {
            if (PluginVerificationService.KnownMainPackage(plugin) == null) return null;
            var after = c.Session.Installer.Snapshot(c.Router.PackageManager);
            var verification = new PluginVerificationService().Verify(plugin, after, new PackageDiff(), name => c.Session.Installer.PackageExists(c.Router.PackageManager, name));
            if (!verification.Verified) return null;
            return new PluginInstallResult { Plugin = plugin, Status = InstallStatus.SuccessVerified, Verification = verification, AfterPackages = after, CleanupSucceeded = true, Message = "目标版本已通过独立验证，未重复执行安装器。" };
        }
        private T Read<T>(Context c, Func<T> read)
        {
            try { return read(); }
            catch (Exception) { if (c.Session.IsConnected) throw; EnsureConnection(c, true).GetAwaiter().GetResult(); return read(); }
        }
        private static InstallationFailure FailureFor(PluginInstallResult value, string logs, IList<RepositoryFeed> official, IList<RepositoryFeed> additional)
        {
            if (value.Status == InstallStatus.Cancelled) return InstallFailureClassifier.Failure(InstallationFailureKind.Cancelled);
            if (value.Status == InstallStatus.FailedVerification || value.Status == InstallStatus.SuccessWithWarning) return InstallFailureClassifier.Failure(InstallationFailureKind.VerificationIncomplete);
            string text = (value.Message ?? "") + "\n" + logs;
            var failure = InstallFailureClassifier.Classify(text, official, additional);
            if (failure.Kind == InstallationFailureKind.UnknownInstaller && !value.InstallerStarted)
                return InstallFailureClassifier.Failure(value.Status == InstallStatus.FailedIntegrity ? InstallationFailureKind.Integrity : InstallationFailureKind.Tool, text);
            return failure;
        }
        private static void SetFailure(Context c, InstallationFailure failure)
        { c.Result.Failure = failure; c.Report(failure.Kind == InstallationFailureKind.Tool ? InstallationState.ToolFailure : failure.CanRetry ? InstallationState.RetryableFailure : InstallationState.ActionRequiredFailure, "失败分类：" + failure.Kind + "；" + failure.Evidence + "；" + failure.UserMessage); }
        private static void MarkSuccess(Context c, string log)
        { c.Report(InstallationState.Verifying, log); c.Result.State = InstallationState.Success; }
        private static string FeedKey(IList<RepositoryFeed> feeds) { return string.Join("\n", feeds.Select(f => f.IndexUrl ?? f.ParseError).OrderBy(s => s, StringComparer.Ordinal)); }
        private static bool MatchesMirror(SoftwareSourceInfo source, IList<RepositoryFeed> feeds)
        {
            Uri mirror; if (source == null || !Uri.TryCreate(source.Url, UriKind.Absolute, out mirror) || feeds.Count == 0) return false;
            return feeds.All(feed => { Uri index; return Uri.TryCreate(feed.IndexUrl, UriKind.Absolute, out index) && index.Scheme == mirror.Scheme && index.Host == mirror.Host && index.Port == mirror.Port && index.AbsolutePath.StartsWith(mirror.AbsolutePath.TrimEnd('/') + "/", StringComparison.Ordinal); });
        }
        private static string TextHash(string text) { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text))) return PluginPackageInspection.ComputeSha256(stream); }
        public static string Message(InstallationState state)
        {
            switch (state)
            {
                case InstallationState.Preflight: return "正在检查路由器环境和网络…";
                case InstallationState.SourceProbe: return "正在寻找可用软件源…";
                case InstallationState.SourceReady: return "软件源已就绪，正在准备安装…";
                case InstallationState.Uploading: return "正在上传插件…";
                case InstallationState.IntegrityCheck: return "正在校验安装包…";
                case InstallationState.Installing: return "正在安装，请勿关闭程序或断开路由器电源。";
                case InstallationState.ReconnectChecking: return "连接暂时中断，正在重新连接并检查安装状态…";
                case InstallationState.Verifying: return "正在验证安装结果…";
                case InstallationState.RestoringSource: return "正在恢复原软件源设置…";
                case InstallationState.Cleaning: return "正在清理临时文件…";
                case InstallationState.Success: return "安装成功";
                default: return "请连接路由器并选择插件。";
            }
        }

        private sealed class WorkflowInstaller : IPluginInstallSession
        {
            private readonly InstallationWorkflowService owner; private readonly Context c; private readonly bool retry;
            private string uploadedHash; private long uploadedBytes;
            public InstallationFailure PolicyFailure;
            public bool DidDispatch;
            public WorkflowInstaller(InstallationWorkflowService owner, Context c, IList<RepositoryFeed> feeds, bool retry)
            { this.owner = owner; this.c = c; this.retry = retry; }
            public string Sanitize(string text) { return c.Session.Sanitize(text); }
            public long TmpAvailable() { return owner.Read(c, () => c.Session.Installer.TmpAvailable()); }
            public void CreateDirectory(RemoteTempSession s) { c.Session.Installer.CreateDirectory(s); c.Directories.Add(s); c.Active = s; c.Finished = true; }
            public void Upload(Stream source, RemoteTempSession s, Action<long> progress, CancellationToken token)
            { uploadedHash = PluginPackageInspection.ComputeSha256(source); uploadedBytes = source.Length; if (uploadedHash != c.Hash) throw new InvalidDataException("本地 SHA256 不匹配，禁止继续。"); c.Session.Installer.Upload(source, s, progress, token); }
            public long RemoteFileSize(RemoteTempSession s) { return owner.Read(c, () => c.Session.Installer.RemoteFileSize(s)); }
            public ProbeResult RemoteHash(RemoteTempSession s)
            {
                var result = owner.Read(c, () => c.Session.Installer.RemoteHash(s)); c.StrictHash = result.ExitStatus == 0 && PluginPackageInspection.ParseSha256(result.Output) == uploadedHash;
                if (retry && !c.StrictHash) { PolicyFailure = InstallFailureClassifier.Failure(result.ExitStatus == 127 ? InstallationFailureKind.HashUnavailable : InstallationFailureKind.Integrity, "重新执行必须通过远程 SHA256；exit=" + result.ExitStatus); throw new InvalidDataException(PolicyFailure.UserMessage); }
                return result;
            }
            public ProbeResult Inspect(RemoteTempSession s, bool check) { return owner.Read(c, () => c.Session.Installer.Inspect(s, check)); }
            public PackageSnapshot Snapshot(PackageManagerKind manager) { return owner.Read(c, () => c.Session.Installer.Snapshot(manager)); }
            public bool PackageExists(PackageManagerKind manager, string name) { return owner.Read(c, () => c.Session.Installer.PackageExists(manager, name)); }
            public void CheckRunner() { owner.Read(c, () => { c.Session.Installer.CheckRunner(); return true; }); }
            public void StartInstaller(RemoteTempSession s)
            {
                try
                {
                    owner.EnsureConnection(c, false).GetAwaiter().GetResult(); owner.WaitForManager(c).GetAwaiter().GetResult();
                    // Check again immediately before dispatch; repair a vanished upload, never a corrupted one.
                    long size;
                    try { size = c.Session.Installer.RemoteFileSize(s); }
                    catch (FileNotFoundException) { size = -1; }
                    catch (IOException ex) { if (!c.Session.IsConnected || (!ex.Message.Contains("No such file") && !ex.Message.Contains("not found"))) throw; size = -1; }
                    if (size < 0)
                    {
                        c.Report(InstallationState.Uploading, "remote .run 已丢失，重新上传同一锁定的本地文件。");
                        using (var file = new FileStream(c.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) c.Session.Installer.Upload(file, s, bytes => { }, c.PreparationToken);
                        size = c.Session.Installer.RemoteFileSize(s);
                        if (!PluginPackageInspection.IntegrityPassed(c.Session.Installer.Inspect(s, true))) throw new InstallationPolicyException(InstallationFailureKind.Makeself, "重新上传的 Makeself --check 未通过。");
                    }
                    var hash = c.Session.Installer.RemoteHash(s);
                    if (size != uploadedBytes || (hash.ExitStatus != 127 && (hash.ExitStatus != 0 || PluginPackageInspection.ParseSha256(hash.Output) != uploadedHash)) || (retry && hash.ExitStatus == 127))
                        throw new InstallationPolicyException(InstallationFailureKind.Integrity, "启动前大小 / SHA256 再校验失败。");
                    owner.WaitForManager(c).GetAwaiter().GetResult();
                }
                catch (InstallationPolicyException ex) { PolicyFailure = InstallFailureClassifier.Failure(ex.Kind, ex.Message); throw; }
                // No cancellation, re-dispatch or mirror switching while this runner may be active.
                DidDispatch = true; c.Dispatched = true; c.Finished = false; c.Active = s; c.Report(InstallationState.Installing, "只执行作者原始 .run；不执行包卸载 / 回滚 / 代理控制。");
                try { c.Session.Installer.StartInstaller(s); }
                catch (Exception)
                {
                    owner.EnsureConnection(c, !c.Session.IsConnected).GetAwaiter().GetResult();
                    if (!c.Session.WasLaunched(s)) throw; // Unknown dispatch is retained, never re-launched.
                    c.Report(InstallationState.ReconnectChecking, "启动应答丢失，但已发现本轮 launch.once，继续监测同一安装器。");
                }
            }
            public InstallerPoll PollInstaller(RemoteTempSession s, long stdoutOffset, long stderrOffset)
            { return owner.Read(c, () => c.Session.Installer.PollInstaller(s, stdoutOffset, stderrOffset)); }
            public bool Cleanup(RemoteTempSession s) { return true; } // Outer layer owns the real, ordered cleanup.
        }
        private sealed class RecoveringSources : ISoftwareSourceSession
        {
            private readonly InstallationWorkflowService owner; private readonly Context c;
            public RecoveringSources(InstallationWorkflowService owner, Context c) { this.owner = owner; this.c = c; }
            public string Sanitize(string text) { return c.Session.Sanitize(text); }
            public ProbeResult ReadOfficial(PackageManagerKind manager, bool rom) { return owner.Read(c, () => c.Session.Sources.ReadOfficial(manager, rom)); }
            public IDictionary<string, string> ReadAdditional(PackageManagerKind manager) { return owner.Read(c, () => c.Session.Sources.ReadAdditional(manager)); }
            public ProbeResult QuickStartGet(bool list) { return owner.Read(c, () => c.Session.Sources.QuickStartGet(list)); }
            public ProbeResult QuickStartSwitch(string identity) { return c.Session.Sources.QuickStartSwitch(identity); }
            public ProbeResult ProbeIndex(string url) { return owner.Read(c, () => c.Session.Sources.ProbeIndex(url)); }
            public PackageUpdateResult Update(PackageManagerKind manager) { return c.Session.Sources.Update(manager); }
            public string BackupOfficial(PackageManagerKind manager) { return c.Session.Sources.BackupOfficial(manager); }
            public ProbeResult ReadBackup(string path) { return owner.Read(c, () => c.Session.Sources.ReadBackup(path)); }
            public ProbeResult RestoreBackup(PackageManagerKind manager, string path) { return c.Session.Sources.RestoreBackup(manager, path); }
        }
    }
}
