using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed partial class PluginMaintenanceExecutionService
    {
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        private readonly IThirdPartyScriptSource source;
        public MaintenanceOperationLogBuffer LogBuffer { get; private set; }
        public PluginMaintenanceExecutionService(IThirdPartyScriptSource source)
        { if (source == null) throw new ArgumentNullException("source"); this.source = source; LogBuffer = new MaintenanceOperationLogBuffer(); }

        // Preparation-only menu entry. Its successful result has already been cleaned and cannot execute.
        // Downloading the main menu/license is the online gate; no commit API or Release scan is required.
        public Task<MaintenancePreparationResult> PrepareMenuAsync(IMaintenancePreparationSession session,
            IProgress<MaintenanceOperationLog> progress, CancellationToken token)
        { return ProcessMenuAsync(session, progress, token); }

        public Task<MaintenancePreparationResult> PrepareMenuAtCommitAsync(IMaintenancePreparationSession session, string commit,
            IProgress<MaintenanceOperationLog> progress, CancellationToken token)
        {
            ThirdPartyScriptCatalog.ValidateCommit(commit);
            return ProcessMenuAsync(session, progress, token, commit);
        }

        private async Task<MaintenancePreparationResult> ProcessMenuAsync(IMaintenancePreparationSession session,
            IProgress<MaintenanceOperationLog> progress, CancellationToken token,
            string fixedCommit = null, MenuRunContext menuRun = null, IThirdPartyScriptSource downloadSource = null)
        {
            if (session == null) throw new ArgumentNullException("session");
            var downloads = downloadSource ?? source;
            if (token.IsCancellationRequested) return new MaintenancePreparationResult { Status = MaintenancePreparationStatus.Cancelled, Error = "脚本准备已取消。" };
            bool acquired;
            try { acquired = await operation.WaitAsync(0, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new MaintenancePreparationResult { Status = MaintenancePreparationStatus.Cancelled, Error = "脚本准备已取消。" }; }
            if (!acquired)
                return new MaintenancePreparationResult { Status = MaintenancePreparationStatus.Busy, Error = "已有一个插件维护任务正在进行。" };
            var result = new MaintenancePreparationResult { OperationId = Guid.NewGuid(), Status = MaintenancePreparationStatus.Failed, LocalCleanupSucceeded = true, RemoteCleanupSucceeded = true };
            LogBuffer.Begin(result.OperationId);
            Action<MaintenanceOperationLog> publish = entry => {
                LogBuffer.TryAppend(entry);
                // A disposed log consumer must not prevent task cleanup.
                if (progress != null) { try { progress.Report(entry); } catch (Exception) { } }
            };
            Action<string> log = text => publish(new MaintenanceOperationLog(result.OperationId, MaintenanceLogOrigin.PluginHelper, session.Sanitize(text), result.Stage));
            LocalMaintenanceWorkspace local = null;
            RemoteMaintenanceWorkspace remote = null;
            bool remoteTouched = false;
            string activeHttpUrl = null;
            // C# 5 cannot await in finally: join preparation and bounded cleanup before releasing the gate.
            Func<Task<MaintenancePreparationResult>> prepare = async () => {
                try
                {
                    token.ThrowIfCancellationRequested();
                    result.Stage = MaintenancePreparationStage.Gate;
                    if (!session.IsConnected)
                    {
                        result.Status = MaintenancePreparationStatus.Rejected;
                        result.Error = "SSH 未连接。";
                        log(result.Error); return result;
                    }
                    log("Third-party project: " + ThirdPartyScriptCatalog.Project);
                    log("Repository: " + ThirdPartyScriptCatalog.Repository);
                    bool mainMenu = fixedCommit == null;
                    if (mainMenu)
                    {
                        result.Stage = MaintenancePreparationStage.Download;
                        result.Descriptor = ThirdPartyScriptCatalog.DescribeMenuMain();
                    }
                    else
                    {
                        result.Stage = MaintenancePreparationStage.Commit;
                        result.Descriptor = ThirdPartyScriptCatalog.DescribeMenu(fixedCommit);
                    }
                    log("Ref: " + (fixedCommit == null ? "main" : "explicit commit") + "; Commit: " +
                        (result.Descriptor.Commit ?? "not resolved (direct branch download)"));
                    log("Script: " + result.Descriptor.FileName + "; purpose: " + result.Descriptor.Purpose);
                    log("Download URL: " + result.Descriptor.DownloadUrl);
                    log("License URL: " + result.Descriptor.LicenseUrl);
                    result.Stage = MaintenancePreparationStage.Download;
                    activeHttpUrl = result.Descriptor.DownloadUrl;
                    byte[] script = await downloads.DownloadAsync(result.Descriptor.DownloadUrl, 1048576, token).ConfigureAwait(false);
                    activeHttpUrl = result.Descriptor.LicenseUrl;
                    byte[] license = await downloads.DownloadAsync(result.Descriptor.LicenseUrl, 65536, token).ConfigureAwait(false);
                    activeHttpUrl = null;
                    token.ThrowIfCancellationRequested();
                    result.Stage = MaintenancePreparationStage.Validate;
                    MaintenanceScriptIntegrity.ValidateScript(script); MaintenanceScriptIntegrity.ValidateLicense(license);
                    // The bundled copy remains accessible even when the public source is unavailable.
                    MaintenanceScriptIntegrity.ValidateLicense(Encoding.UTF8.GetBytes(ThirdPartyScriptCatalog.BundledLicense()));
                    remote = RemoteMaintenanceWorkspace.CreateMenu(result.OperationId, menuRun != null);
                    result.RemoteDirectory = remote.Directory;
                    result.Stage = MaintenancePreparationStage.LocalFiles;
                    local = new LocalMaintenanceWorkspace(result.OperationId, remote.Files);
                    result.LocalDirectory = local.DirectoryPath;
                    local.Write(result.Descriptor.FileName, script); local.Write("LICENSE", license);
                    string sourceNotice = mainMenu ? "Ref: main\nCommit: not resolved (direct branch download)\nDownload URL: " +
                        result.Descriptor.DownloadUrl + "\nLicense URL: " + result.Descriptor.LicenseUrl + "\n" : "Commit: " + result.Descriptor.Commit + "\n";
                    local.Write("NOTICE.txt", new UTF8Encoding(false).GetBytes(ThirdPartyScriptCatalog.Notice + sourceNotice + "Script: " + result.Descriptor.FileName + "\n"));
                    if (menuRun != null) local.Write(RemoteMaintenanceWorkspace.MenuLauncherFile, MaintenanceMenuCommands.LauncherBytes(remote, menuRun.RunId, menuRun.NetworkPath));
                    result.Stage = MaintenancePreparationStage.LocalHash;
                    var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var file in remote.Files) { hashes.Add(file, MaintenanceScriptIntegrity.HashFile(local.FilePath(file))); log("SHA256 " + file + ": " + hashes[file]); }
                    result.ScriptSha256 = hashes[result.Descriptor.FileName]; result.LicenseSha256 = hashes["LICENSE"];
                    token.ThrowIfCancellationRequested();
                    result.Stage = MaintenancePreparationStage.RemoteDirectory;
                    remoteTouched = true;
                    await session.CreateWorkspaceAsync(remote, token).ConfigureAwait(false);
                    log("Remote temporary directory: " + remote.Directory);
                    result.Stage = MaintenancePreparationStage.Upload;
                    foreach (var file in remote.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        await session.UploadAsync(local.FilePath(file), remote, file, token).ConfigureAwait(false);
                        log("Uploaded to " + remote.FilePath(file));
                    }
                    result.Stage = MaintenancePreparationStage.RemoteHash;
                    foreach (var file in remote.Files)
                    {
                        string remoteHash = MaintenanceScriptIntegrity.ParseRemoteHash(await session.ReadHashAsync(remote, file, token).ConfigureAwait(false), remote.FilePath(file));
                        if (!string.Equals(remoteHash, hashes[file], StringComparison.Ordinal)) throw new InvalidDataException("远端 hash 不一致：" + file + "；拒绝准备执行。");
                    }
                    token.ThrowIfCancellationRequested();
                    result.IntegrityVerified = true;
                    result.Stage = MaintenancePreparationStage.Prepared;
                    result.Status = MaintenancePreparationStatus.PreparedExecutionDisabled;
                    log("Script integrity verified.");
                    if (menuRun == null)
                    {
                        log("Prepared menu payload (NOT executed): " + remote.FilePath(ThirdPartyScriptCatalog.MenuScriptFile));
                        log("Preview only; no script was executed.");
                        return result;
                    }
                    token.ThrowIfCancellationRequested();
                    result.Stage = MaintenancePreparationStage.Executing;
                    log("Opening menu PTY; Run: " + menuRun.RunId.ToString("N"));
                    try { await menuRun.ExecuteAsync((IMaintenanceMenuSession)session, remote, token).ConfigureAwait(false); }
                    finally { result.ExecutionAttempted = menuRun.DispatchAttempted; }
                    menuRun.ApplyResult(result);
                    return result;
                }
                catch (OperationCanceledException)
                { result.Status = result.ExecutionAttempted ? MaintenancePreparationStatus.FinalStateUnknown : MaintenancePreparationStatus.Cancelled;
                    result.Error = result.ExecutionAttempted ? "连接中断，无法确认操作最终状态。" : "脚本准备已取消；未执行脚本。"; log(result.Error); return result; }
                catch (Exception ex)
                { result.Status = result.ExecutionAttempted ? MaintenancePreparationStatus.FinalStateUnknown : MaintenancePreparationStatus.Failed;
                    string diagnostic = activeHttpUrl == null ? session.Sanitize(ex.Message) :
                        session.Sanitize(MaintenanceHttpDiagnostics.Failure(result.Stage, activeHttpUrl, ex));
                    result.Error = result.ExecutionAttempted ? "连接中断，无法确认操作最终状态。" : diagnostic;
                    log((result.ExecutionAttempted ? "Execution interrupted: " : "Preparation failed at " + result.Stage + ": ") + diagnostic);
                    if (result.ExecutionAttempted) log(result.Error); return result; }
            };
            try
            {
                await prepare().ConfigureAwait(false);
                var warnings = new List<string>();
                if (remoteTouched)
                {
                    // Cleanup gets its own bounded token, even after user cancellation.
                    try
                    {
                        using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                            result.RemoteCleanupSucceeded = menuRun != null && menuRun.DispatchAttempted ?
                                await menuRun.CleanupAsync((IMaintenanceMenuSession)session, remote, cleanup.Token).ConfigureAwait(false) :
                                await session.CleanupAsync(remote, cleanup.Token).ConfigureAwait(false);
                        if (!result.RemoteCleanupSucceeded) warnings.Add("路由器临时文件未能清理：" + remote.Directory);
                    }
                    catch (Exception ex) { result.RemoteCleanupSucceeded = false; warnings.Add("路由器清理失败：" + remote.Directory + "；" + session.Sanitize(ex.Message)); }
                }
                if (menuRun != null && menuRun.DispatchAttempted)
                {
                    menuRun.ApplyResult(result);
                    var runtime = menuRun.Runtime;
                    log("Menu state: " + runtime.Status + "; PID: " + runtime.ProcessId + "; start ticks: " + runtime.ProcessStartTicks +
                        "; Unix start: " + runtime.StartedAtUnixSeconds + "; exit: " + (runtime.ExitCode.HasValue ? runtime.ExitCode.ToString() : "unknown"));
                }
                if (local != null)
                {
                    try { local.Cleanup(); }
                    catch (Exception ex) { result.LocalCleanupSucceeded = false; warnings.Add("Windows 临时文件清理失败：" + session.Sanitize(ex.Message)); }
                }
                if (warnings.Count > 0)
                {
                    result.CleanupWarning = string.Join("；", warnings); log(result.CleanupWarning);
                    if (result.Status == MaintenancePreparationStatus.PreparedExecutionDisabled)
                    { result.Status = MaintenancePreparationStatus.Failed; result.Stage = MaintenancePreparationStage.Cleanup; result.Error = "完整性验证已通过，但清理未完成。"; }
                }
                else log(result.ExecutionAttempted ? "Temporary files cleaned." : "Temporary files cleaned; no script was executed.");
                result.Logs = new List<MaintenanceOperationLog>(LogBuffer.Snapshot()).AsReadOnly();
                return result;
            }
            finally { operation.Release(); }
        }
    }
}
