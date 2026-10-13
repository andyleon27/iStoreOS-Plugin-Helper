using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper
{
    public partial class MainForm
    {
        private bool isInstallBusy;
        private bool installerDispatched;
        private bool closeAfterInstall;
        private bool installationRequiresReview;
        private CancellationTokenSource installCancellation;
        private readonly StringBuilder installTranscript = new StringBuilder();
        private void RefreshInstallButton()
        {
            installButton.Enabled = !isInstallBusy && !isConnecting && !isSoftwareBusy && !installationRequiresReview &&
                router != null && router.IsConnected && router.PackageManager != PackageManagerKind.Unknown && plugin != null && plugin.CompatibilityStatus == CompatibilityStatus.Compatible;
            installButton.Text = isInstallBusy ? "正在安装…" : lastWorkflowResult != null && lastWorkflowResult.CanRetry ? "再次尝试" : "安装插件";
            uiToolTip.SetToolTip(installButton, installButton.Enabled ? "自动预检、检查软件源、校验安装包、执行并验证；临时换源会恢复原设置。" :
                isInstallBusy || isConnecting || isSoftwareBusy ? "请等待当前操作完成。" : installationRequiresReview ? "请查看结果；重新选择安装包或连接后可开始新操作。" :
                router == null || !router.IsConnected ? "请先连接并检测路由器。" : "请选择与当前设备兼容的本地 .run 文件。");
            cancelPreparationHint.Text = !isInstallBusy ? "检查或上传安装包时可取消准备。" : installerDispatched ? "安装已开始，不可取消。" : "可取消当前安装前准备。";
            cancelInstallButton.Enabled = isInstallBusy && !installerDispatched && installCancellation != null && !installCancellation.IsCancellationRequested;
        }
        private async void InstallButton_Click(object sender, EventArgs e)
        {
            if (!installButton.Enabled || isInstallBusy) return;
            isInstallBusy = true; installerDispatched = false; closeAfterInstall = false;
            installTranscript.Clear(); softwareTranscript.Clear(); ResetSoftwareState();
            installCancellation = new CancellationTokenSource();
            RefreshInstallButton(); softwareButton.Enabled = false; chooseButton.Enabled = false; dropLabel.AllowDrop = false;
            SetConnectionInputs(false); connectButton.Text = "连接";
            RefreshLogButtons();
            SetInstallSummary("正在准备安装", "程序将自动检查网络、软件源和安装包。", UiTone.Neutral, false);
            try
            {
                var progress = new Progress<InstallationWorkflowProgress>(item =>
                {
                    if (IsDisposed || Disposing) return;
                    installStepLabel.Text = item.Message;
                    installerDispatched = item.InstallerDispatched;
                    if (installerDispatched) closeAfterInstall = false;
                    if (item.Transfer != null && item.Transfer.UploadedBytes.HasValue && item.Transfer.TotalBytes.HasValue)
                        installStepLabel.Text = "正在上传插件… " + (item.Transfer.UploadedBytes.Value * 100 / item.Transfer.TotalBytes.Value) + "%";
                    if (!string.IsNullOrEmpty(item.Log)) AppendInstallLog(item.Log);
                    if (item.Software != null) DisplaySoftwareResult(item.Software);
                    RefreshInstallButton();
                });
                InstallationWorkflowResult result = await ssh.InstallOneClickAsync(router, plugin.FilePath, progress, installCancellation.Token);
                installationRequiresReview = result.State == InstallationState.Success || !result.CanRetry;
                DisplayWorkflowResult(result);
            }
            catch (Exception exception)
            {
                installationRequiresReview = false;
                AppendInstallLog("工具异常：" + exception.GetType().Name);
                DisplayWorkflowResult(new InstallationWorkflowResult { State = InstallationState.ToolFailure,
                    Failure = Services.InstallFailureClassifier.Failure(InstallationFailureKind.Tool) });
            }
            finally
            {
                isInstallBusy = false; installCancellation.Dispose(); installCancellation = null;
                cancelInstallButton.Enabled = false; SetConnectionInputs(true); chooseButton.Enabled = true; dropLabel.AllowDrop = true;
                softwareButton.Enabled = !ssh.HasPendingInstallation && router != null && router.IsConnected && ssh.IsConnected && router.PackageManager != PackageManagerKind.Unknown;
                RefreshLogButtons(); RefreshInstallButton();
                if (closeAfterInstall) Close();
            }
        }
        private Task<bool> ConfirmInstallAsync(InstallConfirmation summary)
        {
            var answer = new TaskCompletionSource<bool>();
            BeginInvoke(new Action(() =>
            {
                if (closeAfterInstall || installCancellation == null || installCancellation.IsCancellationRequested)
                { answer.TrySetResult(false); return; }
                using (var dialog = new InstallConfirmationForm(summary)) answer.TrySetResult(dialog.ShowDialog(this) == DialogResult.OK);
            }));
            return answer.Task;
        }
        private void CancelInstallButton_Click(object sender, EventArgs e)
        {
            if (!isInstallBusy || installerDispatched || installCancellation == null) return;
            installCancellation.Cancel(); cancelInstallButton.Enabled = false;
            installStepLabel.Text = "正在结束安装前准备并清理临时文件…";
        }
        private void AppendInstallLog(string text)
        {
            string line = LogHelper.Timestamp(text) + Environment.NewLine;
            installTranscript.Append(line);
            if (logBox.TextLength > 200000) logBox.Clear();
            logBox.AppendText(line); logBox.SelectionStart = logBox.TextLength; logBox.ScrollToCaret();
            RecordDiagnosticLine(line);
        }
        private void SaveInstallLogButton_Click(object sender, EventArgs e)
        {
            SaveLogSnapshot(true);
        }
    }
}
