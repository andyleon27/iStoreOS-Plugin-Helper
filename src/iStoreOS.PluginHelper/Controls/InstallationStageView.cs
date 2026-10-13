using System;
using System.Drawing;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Controls
{
    // Readonly presentation: evidence comes from existing results and the existing
    // current-step text. No stage completion is inferred just from elapsed time.
    public sealed class InstallationStageView : TableLayoutPanel
    {
        private readonly Label[] values = new Label[8];
        private readonly StatusDot[] dots = new StatusDot[8];
        public InstallationStageView(UiResources resources)
        {
            Name = "installationStages"; Dock = DockStyle.Fill; RowCount = 8; ColumnCount = 3; Margin = Padding.Empty;
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 24)); ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var titles = new[] { "预检", "软件源", "上传", "完整性校验", "安装", "验证", "恢复软件源", "清理" };
            for (int i = 0; i < titles.Length; i++)
            {
                RowStyles.Add(new RowStyle(SizeType.Percent, 12.5F));
                dots[i] = new StatusDot { Name = "advancedStageDot" + i, Anchor = AnchorStyles.None, DotColor = UiTheme.Waiting };
                Controls.Add(dots[i], 0, i);
                Controls.Add(new Label { Text = titles[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = UiTheme.Ink,
                    Font = resources.Font(9F, FontStyle.Regular), Margin = Padding.Empty }, 1, i);
                values[i] = new Label { Name = "advancedStageStatus" + i, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true,
                    Font = resources.Font(9F, FontStyle.Regular), Margin = Padding.Empty };
                Controls.Add(values[i], 2, i);
            }
            UpdateResult(null, false, "");
        }
        private void State(int index, string text, Color color) { values[index].Text = text; values[index].ForeColor = color; dots[index].DotColor = color; }
        public void UpdateResult(InstallationWorkflowResult workflow, bool busy, string currentStep)
        {
            for (int i = 0; i < values.Length; i++) State(i, "等待", UiTheme.Muted);
            if (busy)
            {
                // Highlight only the stage explicitly named in the original step.
                string step = currentStep ?? ""; int active = -1;
                if (step.Contains("恢复") && step.Contains("源")) active = 6;
                else if (step.Contains("清理")) active = 7;
                else if (step.Contains("上传")) active = 2;
                else if (step.Contains("完整性") || step.Contains("校验")) active = 3;
                else if (step.Contains("验证")) active = 5;
                else if (step.Contains("预检") || step.Contains("检查网络")) active = 0;
                else if (step.Contains("软件源") || step.Contains("镜像")) active = 1;
                else if (step.Contains("安装器") || step.Contains("正在安装")) active = 4;
                if (active >= 0) State(active, "进行中", UiTheme.Blue);
                return;
            }
            if (workflow == null) return;
            var install = workflow.Installation;
            var failure = workflow.Failure;
            bool started = install != null && install.InstallerStarted;
            if (started || workflow.Software != null && workflow.Software.Status == SoftwareEnvironmentStatus.Ready) State(0, "通过", UiTheme.Success);
            else if (workflow.States.Contains(InstallationState.Preflight)) State(0, "已执行", UiTheme.SecondaryText);
            if (failure != null && (failure.Kind == InstallationFailureKind.Offline || failure.Kind == InstallationFailureKind.DnsFailure || failure.Kind == InstallationFailureKind.TimeInvalid || failure.Kind == InstallationFailureKind.NoSpace || failure.Kind == InstallationFailureKind.PackageManagerBusy)) State(0, "未通过", UiTheme.Error);
            if (workflow.Software != null) State(1, workflow.Software.Status == SoftwareEnvironmentStatus.Ready ? "就绪" : "查看检测结果", workflow.Software.Status == SoftwareEnvironmentStatus.Ready ? UiTheme.Success : UiTheme.Warning);
            if (install != null)
            {
                if (started) { State(2, "完成", UiTheme.Success); State(3, "通过", UiTheme.Success); }
                if (install.Status == InstallStatus.FailedUpload) State(2, "未完成", UiTheme.Error);
                if (install.Status == InstallStatus.FailedIntegrity) State(3, "未通过", UiTheme.Error);
                if (install.InstallerEnded) State(4, install.InstallerExitCode == 0 ? "已结束 · exit 0" : "已结束 · 查看结果", install.InstallerExitCode == 0 ? UiTheme.Success : UiTheme.Error);
                else if (install.InstallerStarted) State(4, "状态待确认", UiTheme.Warning);
                else if (install.Verification != null && install.Verification.Verified) State(4, "已存在 · 未执行安装器", UiTheme.SecondaryText);
                if (install.Verification != null) State(5, install.Verification.Verified ? "通过" : "未确认", install.Verification.Verified ? UiTheme.Success : UiTheme.Warning);
                if (install.CleanupSucceeded) State(7, "完成", UiTheme.Success);
                else State(7, "未确认 · 查看结果", UiTheme.Warning);
            }
            if (workflow.SourceRestored) State(6, "完成", UiTheme.Success);
            else if (workflow.RestoreAttempted || workflow.SourceChanged) State(6, "待确认 · 查看结果", UiTheme.Warning);
            else State(6, "无需恢复", UiTheme.SecondaryText);
        }
    }
}
