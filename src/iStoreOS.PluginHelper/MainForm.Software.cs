using System;
using System.Drawing;
using System.IO;
using System.Linq;
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
        private bool isSoftwareBusy;
        private bool closeAfterSoftware;
        private CancellationTokenSource softwareCancellation;
        private readonly StringBuilder softwareTranscript = new StringBuilder();

        private void ResetSoftwareState()
        {
            currentSourceLabel.Text = "—"; officialSourceLabel.Text = "—";
            auxiliarySourceLabel.Text = "—"; updateSourceLabel.Text = "—";
            softwareStatusLabel.Text = "安装时自动检测软件源；这里可单独检查。";
            sourceDetailsBox.Text = "尚未检测软件源。检测记录、feed URL 和 update 输出会显示在这里。";
            SetStatusAppearance(softwareStatusLabel, UiTone.Neutral);
            SetStatusAppearance(officialSourceLabel, UiTone.Neutral); SetStatusAppearance(auxiliarySourceLabel, UiTone.Neutral); SetStatusAppearance(updateSourceLabel, UiTone.Neutral);
        }
        private async void SoftwareButton_Click(object sender, EventArgs e)
        {
            if (isSoftwareBusy || isConnecting || isInstallBusy || router == null || !router.IsConnected) return;
            isSoftwareBusy = true;
            SetWorkflowStage(1);
            RefreshInstallButton();
            ResetSoftwareState(); softwareTranscript.Clear();
            softwareCancellation = new CancellationTokenSource();
            softwareButton.Enabled = false; RefreshLogButtons();
            SetConnectionInputs(false); connectButton.Text = "连接并检测";
            softwareProgressBar.Visible = true; softwareProgressBar.MarqueeAnimationSpeed = 30;
            AppendSoftwareLog("开始软件环境检测；包管理器 " + router.PackageManager + "；版本 " + router.Version + "；SDK " + router.SdkSeries + "；架构 " + router.Architecture);
            try
            {
                var progress = new Progress<SoftwareProgress>(item =>
                {
                    if (IsDisposed || closing.IsCancellationRequested) return;
                    softwareStatusLabel.Text = item.Step;
                    if (!string.IsNullOrEmpty(item.Log)) AppendSoftwareLog(item.Log);
                    if (item.Result != null) DisplaySoftwareResult(item.Result);
                });
                SoftwareEnvironmentResult result = await ssh.CheckSoftwareEnvironmentAsync(router, progress, ConfirmSoftwareRepairAsync, softwareCancellation.Token);
                DisplaySoftwareResult(result);
                softwareStatusLabel.Text = result.Message;
                SetStatusAppearance(softwareStatusLabel, result.Status == SoftwareEnvironmentStatus.Ready ? UiTone.Success : result.Status == SoftwareEnvironmentStatus.Cancelled ? UiTone.Warning : UiTone.Error);
            }
            catch (Exception exception)
            {
                softwareStatusLabel.Text = "软件环境检测失败，详情见日志。";
                SetStatusAppearance(softwareStatusLabel, UiTone.Error);
                AppendSoftwareLog("软件环境操作失败：" + exception.GetType().Name);
            }
            finally
            {
                isSoftwareBusy = false; softwareCancellation.Dispose(); softwareCancellation = null;
                softwareProgressBar.MarqueeAnimationSpeed = 0; softwareProgressBar.Visible = false;
                SetConnectionInputs(true);
                softwareButton.Enabled = !ssh.HasPendingInstallation && router != null && router.IsConnected && ssh.IsConnected && router.PackageManager != PackageManagerKind.Unknown;
                RefreshLogButtons();
                RefreshInstallButton();
                if (closeAfterSoftware) Close();
            }
        }
        private void DisplaySoftwareResult(SoftwareEnvironmentResult result)
        {
            DisplayAdvancedSoftware(result);
            softwareStatusLabel.Text = result.Message ?? "正在检测软件源…";
            currentSourceLabel.Text = UiResultFormatter.SourceLabel(result.CurrentSource);
            uiToolTip.SetToolTip(currentSourceLabel, result.CurrentSource == null ? currentSourceLabel.Text : result.CurrentSource.ToString() + "\n" + result.CurrentSource.Url);
            officialSourceLabel.Text = result.OfficialHealth == null ? "—" : (result.OfficialHealth.Status == RepositoryHealth.FULL ? "完整可用 " : result.OfficialHealth.Status == RepositoryHealth.PARTIAL ? "部分可用 " : "不可用 ") + result.OfficialHealth.Successful + "/" + result.OfficialHealth.Total;
            auxiliarySourceLabel.Text = result.AdditionalHealth == null ? "—" : result.AdditionalHealth.Total == 0 ? "无启用项" :
                result.AdditionalHealth.Successful == result.AdditionalHealth.Total ? "正常" : "有 " + (result.AdditionalHealth.Total - result.AdditionalHealth.Successful) + " 个异常（见日志）";
            PackageUpdateResult update = result.Updates.LastOrDefault();
            updateSourceLabel.Text = update == null ? "—" : update.Success ? "成功" : update.TimedOut ? "超时" : "失败";
            SetStatusAppearance(officialSourceLabel, result.OfficialHealth == null ? UiTone.Neutral : result.OfficialHealth.Status == RepositoryHealth.FULL ? UiTone.Success : result.OfficialHealth.Status == RepositoryHealth.PARTIAL ? UiTone.Warning : UiTone.Error);
            SetStatusAppearance(auxiliarySourceLabel, result.AdditionalHealth == null ? UiTone.Neutral : result.AdditionalHealth.Successful == result.AdditionalHealth.Total ? UiTone.Success : UiTone.Warning);
            SetStatusAppearance(updateSourceLabel, update == null ? UiTone.Neutral : update.Success ? UiTone.Success : UiTone.Error);
            SetStatusAppearance(softwareStatusLabel, result.Status == SoftwareEnvironmentStatus.Ready ? UiTone.Success : result.Status == SoftwareEnvironmentStatus.Unknown ? UiTone.Neutral : result.Status == SoftwareEnvironmentStatus.Cancelled ? UiTone.Warning : UiTone.Error);
        }
        private Task<bool> ConfirmSoftwareRepairAsync(SoftwareRepairPlan plan)
        {
            var answer = new TaskCompletionSource<bool>();
            BeginInvoke(new Action(() =>
            {
                var cancellation = isInstallBusy ? installCancellation : softwareCancellation;
                if (closeAfterSoftware || closeAfterInstall || closing.IsCancellationRequested || cancellation == null || cancellation.IsCancellationRequested)
                { answer.TrySetResult(false); return; }
                using (var dialog = new SoftwareRepairConfirmationForm(plan))
                    answer.TrySetResult(dialog.ShowDialog(this) == DialogResult.OK);
            }));
            return answer.Task;
        }
        private void AppendSoftwareLog(string text)
        {
            string line = LogHelper.Timestamp(text) + Environment.NewLine;
            softwareTranscript.Append(line); // Complete update logs survive UI display trimming.
            if (logBox.TextLength > 200000) logBox.Clear();
            logBox.AppendText(line); logBox.SelectionStart = logBox.TextLength; logBox.ScrollToCaret();
            RecordDiagnosticLine(line);
        }
        private void SaveSoftwareLogButton_Click(object sender, EventArgs e)
        {
            SaveLogSnapshot(false);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (Height > area.Height) { Height = area.Height; Top = area.Top; }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!isInstallBusy && (installerStateUnresolved || ssh.HasPendingInstaller))
            {
                e.Cancel = true; installStepLabel.Text = "旧安装器状态尚未确认，请先点击再次尝试检查；程序不会强制终止安装。";
            }
            if (isInstallBusy)
            {
                e.Cancel = true;
                if (installerDispatched)
                    installStepLabel.Text = "安装正在进行，请勿关闭程序或断开路由器电源。";
                else
                {
                    closeAfterInstall = true; installCancellation.Cancel();
                    installStepLabel.Text = "正在结束安装前准备，完成必要恢复与清理后关闭。";
                }
            }
            if (isSoftwareBusy)
            {
                e.Cancel = true; closeAfterSoftware = true;
                softwareCancellation.Cancel();
                softwareStatusLabel.Text = "正在结束当前命令；如发生换源，会先恢复原配置再关闭。";
            }
            base.OnFormClosing(e);
        }
    }
}
