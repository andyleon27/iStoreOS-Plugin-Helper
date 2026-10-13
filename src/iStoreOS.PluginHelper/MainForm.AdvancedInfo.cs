using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;
using iStoreOS.PluginHelper.Controls;

namespace iStoreOS.PluginHelper
{
    public partial class MainForm
    {
        private AdvancedInfoForm advancedInfoForm;
        private TextBox sourceDetailsBox, systemDetailsBox, packageDiffBox;
        private Label compatibilityDetailLabel, advancedStepLabel;

        private TextBox ReadOnlyValue(string name)
        { return new TextBox { Name = name, Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = UiTheme.Ink, Font = uiResources.Font(9.5F, FontStyle.Regular), Margin = new Padding(0, 6, 0, 0) }; }
        private TextBox DetailText(string name, string text)
        { return new AdaptiveDetailTextBox { Name = name, Dock = DockStyle.Fill, Font = uiResources.Font(9F, FontStyle.Regular), Text = text, Margin = Padding.Empty }; }
        private Control DetailRow(string title, Control value)
        { return new InformationRow(title, value); }
        private Label AdvancedValue(string text, string name)
        { var value = Value(text, name); value.ForeColor = UiTheme.Ink; value.Font = uiResources.Font(9.5F, FontStyle.Regular); return value; }
        private Label AdvancedHeading(string text)
        { var label = Value(text, ""); label.ForeColor = UiTheme.HeadingText; label.Font = uiResources.Font(11F, FontStyle.Bold); return label; }
        private Control EmptyAware(Control content, string name, string title, string help, string icon, Func<bool> isEmpty, params Control[] observed)
        {
            var host = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = Color.White };
            var empty = new EmptyStateView(name, title, help, uiResources.Image(icon, 56), uiResources);
            host.Controls.Add(content); host.Controls.Add(empty);
            Action refresh = () => { if (host.IsDisposed) return; bool blank = isEmpty(); content.Visible = !blank; empty.Visible = blank; if (blank) empty.BringToFront(); else content.BringToFront(); };
            foreach (var control in observed) control.TextChanged += (sender, args) => refresh();
            host.VisibleChanged += (sender, args) => refresh(); refresh(); return host;
        }
        private static TabPage DetailTab(string title, string name, Control content)
        {
            var tab = new TabPage { Text = title, Name = name, BackColor = UiTheme.Background, Padding = Padding.Empty };
            var group = new RoundedPanel { Name = name + "Group", Radius = 12, Dock = DockStyle.Fill, Padding = new Padding(16), Margin = Padding.Empty };
            group.Controls.Add(content); tab.Controls.Add(group); return tab;
        }
        private Control BuildLogPanel()
        {
            var root = Stack(38, 12, 0, 12, 48);
            var tabs = new FlatPageTabs { Name = "advancedTabs", Dock = DockStyle.Fill, Margin = Padding.Empty };
            versionLabel = AdvancedValue("—", "versionLabel"); sdkLabel = AdvancedValue("—", "sdkLabel"); architectureLabel = AdvancedValue("—", "architectureLabel"); managerLabel = AdvancedValue("—", "managerLabel"); quickStartLabel = AdvancedValue("—", "quickStartLabel");
            var system = Stack(26, 10, 34, 34, 34, 34, 34, 12, 24, 0);
            system.Controls.Add(AdvancedHeading("系统信息"), 0, 0);
            system.Controls.Add(DetailRow("iStoreOS 版本", versionLabel), 0, 2); system.Controls.Add(DetailRow("OpenWrt SDK", sdkLabel), 0, 3);
            system.Controls.Add(DetailRow("系统架构", architectureLabel), 0, 4); system.Controls.Add(DetailRow("包管理器", managerLabel), 0, 5); system.Controls.Add(DetailRow("QuickStart", quickStartLabel), 0, 6);
            system.Controls.Add(AdvancedHeading("连接详情"), 0, 8);
            systemDetailsBox = DetailText("systemDetailsBox", "连接路由器后显示当前设备信息。"); system.Controls.Add(systemDetailsBox, 0, 9);
            tabs.TabPages.Add(DetailTab("系统信息", "systemTab", EmptyAware(system, "systemEmptyState", "连接路由器后显示设备信息", "设备版本、架构和连接信息将在这里显示。", "system",
                () => versionLabel.Text == "—", versionLabel, systemDetailsBox)));

            currentSourceLabel = AdvancedValue("—", "currentSourceLabel"); officialSourceLabel = AdvancedValue("—", "officialSourceLabel"); auxiliarySourceLabel = AdvancedValue("—", "auxiliarySourceLabel"); updateSourceLabel = AdvancedValue("—", "updateSourceLabel"); softwareStatusLabel = AdvancedValue("安装时自动检测软件源。", "softwareStatusLabel");
            var sources = Stack(24, 34, 10, 24, 34, 34, 34, 10, 24, 34, 40, 0);
            sources.Controls.Add(AdvancedHeading("当前软件源"), 0, 0); sources.Controls.Add(DetailRow("当前官方源", currentSourceLabel), 0, 1);
            sources.Controls.Add(AdvancedHeading("源可用性"), 0, 3); sources.Controls.Add(DetailRow("官方源状态", officialSourceLabel), 0, 4);
            sources.Controls.Add(DetailRow("辅助 / 自定义源", auxiliarySourceLabel), 0, 5); sources.Controls.Add(DetailRow("Package Update", updateSourceLabel), 0, 6);
            sources.Controls.Add(AdvancedHeading("检测详情"), 0, 8); sources.Controls.Add(DetailRow("当前结果", softwareStatusLabel), 0, 9);
            softwareButton = ActionButton("检测软件源", null, 130, false, "softwareButton"); softwareButton.Enabled = false;
            var sourceActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 0) }; sourceActions.Controls.Add(softwareButton); sources.Controls.Add(sourceActions, 0, 10);
            sourceDetailsBox = DetailText("sourceDetailsBox", "尚未检测软件源。检测记录、feed URL 和 update 输出会显示在这里。"); sources.Controls.Add(sourceDetailsBox, 0, 11);
            tabs.TabPages.Add(DetailTab("软件源", "sourceTab", EmptyAware(sources, "sourceEmptyState", "连接路由器后显示软件源信息", "官方源、辅助源及检测记录将在这里显示。", "software-source",
                () => versionLabel.Text == "—" && currentSourceLabel.Text == "—" && officialSourceLabel.Text == "—", versionLabel, currentSourceLabel, officialSourceLabel, sourceDetailsBox)));

            fileNameBox = ReadOnlyValue("fileNameBox"); filePathBox = ReadOnlyValue("filePathBox"); fileSizeLabel = Value("—", "fileSizeLabel");
            pluginVersionLabel = AdvancedValue("—", "pluginVersionLabel"); pluginArchitectureLabel = AdvancedValue("—", "pluginArchitectureLabel"); pluginSdkLabel = AdvancedValue("—", "pluginSdkLabel"); pluginTypeLabel = AdvancedValue("—", "pluginTypeLabel"); compatibilityDetailLabel = AdvancedValue("尚未选择插件", "compatibilityDetailLabel");
            var installation = Stack(30, 24, 10, 208, 12, 26, 32, 32, 32, 32, 32, 32, 32, 32, 12, 26, 140); installation.Dock = DockStyle.Top; installation.Height = 744;
            var currentState = AdvancedValue(installHeadlineLabel.Text, "advancedCurrentStateLabel"); currentState.Font = uiResources.Font(9.5F, FontStyle.Bold);
            installation.Controls.Add(DetailRow("当前状态", currentState), 0, 0);
            advancedStepLabel = Value("尚未开始安装", "advancedStepLabel"); advancedStepLabel.ForeColor = UiTheme.StatusHelp; installation.Controls.Add(advancedStepLabel, 0, 1);
            var stages = new InstallationStageView(uiResources); installation.Controls.Add(stages, 0, 3);
            installation.Controls.Add(AdvancedHeading("安装包信息"), 0, 5);
            installation.Controls.Add(DetailRow("完整文件名", fileNameBox), 0, 6); installation.Controls.Add(DetailRow("完整路径", filePathBox), 0, 7); installation.Controls.Add(DetailRow("文件大小", fileSizeLabel), 0, 8);
            installation.Controls.Add(DetailRow("插件版本", pluginVersionLabel), 0, 9); installation.Controls.Add(DetailRow("架构", pluginArchitectureLabel), 0, 10); installation.Controls.Add(DetailRow("目标 SDK", pluginSdkLabel), 0, 11);
            installation.Controls.Add(DetailRow("包类型", pluginTypeLabel), 0, 12); installation.Controls.Add(DetailRow("兼容性", compatibilityDetailLabel), 0, 13);
            installation.Controls.Add(AdvancedHeading("详细结果"), 0, 15);
            installResultBox = DetailText("installResultBox", "尚未执行安装。SHA256、Makeself、独立验证及收尾信息会在流程中记录。"); installation.Controls.Add(installResultBox, 0, 16);
            var installationScroll = new Panel { Name = "installationScroll", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty }; installationScroll.Controls.Add(installation);
            tabs.TabPages.Add(DetailTab("安装过程", "installationTab", EmptyAware(installationScroll, "installationEmptyState", "尚未开始安装", "选择插件后可查看安装包信息和阶段状态。", "plugin",
                () => plugin == null && lastWorkflowResult == null && !isInstallBusy && installResultBox.Text.StartsWith("尚未执行安装"), fileNameBox, installResultBox, installHeadlineLabel)));
            Action refreshStages = () => { if (stages.IsDisposed) return; currentState.Text = installHeadlineLabel.Text; currentState.ForeColor = installHeadlineLabel.ForeColor; stages.UpdateResult(lastWorkflowResult, isInstallBusy, installStepLabel.Text); };
            installStepLabel.TextChanged += (sender, args) => refreshStages(); installHeadlineLabel.TextChanged += (sender, args) => refreshStages(); installResultBox.TextChanged += (sender, args) => refreshStages(); installButton.TextChanged += (sender, args) => refreshStages();

            packageDiffBox = DetailText("packageDiffBox", "尚无软件包变化记录。");
            var diffLayout = Stack(26, 10, 0); diffLayout.Controls.Add(AdvancedHeading("软件包变化"), 0, 0); diffLayout.Controls.Add(new PackageDiffView(packageDiffBox, uiResources), 0, 2);
            tabs.TabPages.Add(DetailTab("Package Diff", "diffTab", diffLayout));
            logBox = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = UiTheme.LogBackground, ForeColor = UiTheme.LogForeground, BorderStyle = BorderStyle.None, WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, Name = "logBox", Font = uiResources.Font("Consolas", 9F, FontStyle.Regular), Margin = Padding.Empty };
            var logFrame = new RoundedPanel { Name = "advancedLogFrame", Radius = 10, Dock = DockStyle.Fill, Padding = new Padding(6), FillColor = UiTheme.LogBackground, BorderColor = UiTheme.LogBackground, Margin = Padding.Empty };
            var logEmpty = EmptyAware(logBox, "logEmptyState", "暂无日志", "连接、软件源和安装记录将在这里显示。", "log", () => logBox.TextLength == 0, logBox);
            logEmpty.BackColor = UiTheme.LogBackground; ((EmptyStateView)logEmpty.Controls.Find("logEmptyState", true)[0]).UseDarkSurface(); logFrame.Controls.Add(logEmpty);
            var logLayout = Stack(26, 10, 0); logLayout.Controls.Add(AdvancedHeading("完整日志"), 0, 0); logLayout.Controls.Add(logFrame, 0, 2);
            tabs.TabPages.Add(DetailTab("完整日志", "logsTab", logLayout));
            tabs.SelectedIndex = 0;
            root.Controls.Add(new SegmentedNavigation(tabs, uiResources) { Dock = DockStyle.Fill }, 0, 0); root.Controls.Add(tabs, 0, 2);

            var actions = Columns(2); actions.Name = "advancedToolbar"; actions.ColumnStyles.Clear(); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); actions.Padding = new Padding(0, 8, 0, 8);
            var logActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            saveLogButton = ActionButton("保存日志", "log", 120, false, "saveLogButton"); copyLogButton = ActionButton("复制日志", "copy", 120, false, "copyLogButton");
            logActions.Controls.Add(saveLogButton); logActions.Controls.Add(copyLogButton); actions.Controls.Add(logActions, 0, 0);
            var cancellation = Columns(2); cancellation.ColumnStyles.Clear(); cancellation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); cancellation.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            cancelPreparationHint = Value("检查或上传安装包时可取消准备。", "cancelPreparationHint"); cancelPreparationHint.ForeColor = UiTheme.StatusHelp; cancelPreparationHint.Font = uiResources.Font(8.5F, FontStyle.Regular); cancelPreparationHint.TextAlign = ContentAlignment.MiddleRight; cancelPreparationHint.Padding = new Padding(0, 0, 10, 0);
            cancelInstallButton = ActionButton("取消准备", null, 100, false, "cancelInstallButton"); cancelInstallButton.Dock = DockStyle.Fill; cancelInstallButton.Margin = Padding.Empty; cancelInstallButton.Enabled = false;
            cancellation.Controls.Add(cancelPreparationHint, 0, 0); cancellation.Controls.Add(cancelInstallButton, 1, 0); actions.Controls.Add(cancellation, 1, 0); root.Controls.Add(actions, 0, 4);

            // Hidden compatibility entry points for scoped log exports; never part of MainForm's visual tree.
            saveSoftwareLogButton = ActionButton("保存检测日志", null, 1, false, "saveSoftwareLogButton"); saveSoftwareLogButton.Visible = false;
            saveInstallLogButton = ActionButton("保存安装日志", null, 1, false, "saveInstallLogButton"); saveInstallLogButton.Visible = false;
            installProgressBar = new ProgressBar { Name = "installProgressBar", Visible = false };
            softwareProgressBar = new ProgressBar { Name = "softwareProgressBar", Visible = false };
            logPanel = new Panel { Dock = DockStyle.Fill, Name = "logPanel", Font = Font }; logPanel.Controls.Add(root);
            foreach (var hidden in new Control[] { saveSoftwareLogButton, saveInstallLogButton, installProgressBar, softwareProgressBar, connectionSummaryLabel, pluginSummaryLabel, filePathPreviewLabel }) logPanel.Controls.Add(hidden);
            saveLogButton.Click += SaveLogButton_Click;
            installStepLabel.TextChanged += (sender, args) => { advancedStepLabel.Text = installStepLabel.Text; uiToolTip.SetToolTip(installStepLabel, installStepLabel.Text); };
            return logPanel;
        }
        private void ShowAdvancedInformation(bool logs)
        {
            if (advancedInfoForm == null || advancedInfoForm.IsDisposed) return;
            if (!advancedInfoForm.Visible) advancedInfoForm.Show(this);
            if (advancedInfoForm.WindowState == FormWindowState.Minimized) advancedInfoForm.WindowState = FormWindowState.Normal;
            if (logs) advancedInfoForm.SelectLogs();
            advancedInfoForm.Activate();
        }
        private void DisplayAdvancedSoftware(SoftwareEnvironmentResult result)
        {
            var text = new StringBuilder();
            if (result.CurrentSource != null) text.AppendLine("当前源 URL：" + result.CurrentSource.Url);
            AppendProbes(text, "官方 feed", result.OfficialHealth); AppendProbes(text, "辅助 / 自定义 feed", result.AdditionalHealth);
            foreach (var update in result.Updates)
                text.AppendLine("Package Update：exit=" + update.ExitCode + "，Success=" + update.Success + "，TimedOut=" + update.TimedOut + "，分类=" + update.ErrorCategory)
                    .AppendLine(update.Stdout).AppendLine(update.Stderr);
            text.AppendLine("切换尝试：" + result.SwitchAttempted + "；实际切换：" + result.SourceSwitchOccurred)
                .AppendLine("恢复尝试：" + result.RecoveryAttempted + "；恢复结果：" + result.RecoverySucceeded).AppendLine("备份：" + result.BackupPath);
            sourceDetailsBox.Text = text.ToString();
        }
        private static void AppendProbes(StringBuilder text, string title, RepositoryHealthResult health)
        {
            text.AppendLine().AppendLine(title + "：" + (health == null ? "未检测" : health.ToString()));
            if (health == null) return;
            foreach (var probe in health.Probes)
            {
                var feed = probe.Feed;
                text.AppendLine((feed == null ? "feed" : feed.Name + " [" + feed.Kind + "]") + "：" + (probe.Success ? "可用" : "异常") + " HTTP=" + probe.HttpStatus + " exit=" + probe.ExitCode)
                    .AppendLine(feed == null ? "" : feed.IndexUrl).AppendLine(probe.ErrorMessage);
            }
        }
        private void DisplayAdvancedInstallation(PluginInstallResult result)
        {
            var text = new StringBuilder(); AppendInstallation(text, result);
            installResultBox.Text = text.ToString(); packageDiffBox.Text = FormatDiff(result == null ? null : result.Diff);
        }
        private void DisplayAdvancedWorkflow(InstallationWorkflowResult result)
        {
            var text = new StringBuilder(); text.AppendLine("安装状态：" + result.State);
            if (result.Failure != null) text.AppendLine("失败分类：" + result.Failure.Kind).AppendLine(result.Failure.UserMessage).AppendLine("证据：" + result.Failure.Evidence);
            text.AppendLine("临时换源：" + result.SourceChanged + "；恢复尝试：" + result.RestoreAttempted + "；原源已恢复：" + result.SourceRestored)
                .AppendLine("原源 SHA256：" + result.OriginalHash).AppendLine("源备份：" + result.BackupPath).AppendLine("安装器待确认：" + result.PendingInstaller);
            foreach (string warning in result.Warnings) text.AppendLine("提示：" + warning);
            text.AppendLine(); AppendInstallation(text, result.Installation);
            var diffs = new StringBuilder();
            for (int i = 0; i < result.Attempts.Count; i++)
            {
                var attempt = result.Attempts[i];
                text.AppendLine().AppendLine("Attempt " + (i + 1) + "，Source=" + attempt.SourceKey); AppendInstallation(text, attempt.Result);
                if (attempt.Failure != null) text.AppendLine("分类：" + attempt.Failure.Kind).AppendLine("证据：" + attempt.Failure.Evidence);
                diffs.AppendLine("Attempt " + (i + 1) + "，Source=" + attempt.SourceKey).AppendLine(FormatDiff(attempt.Result == null ? null : attempt.Result.Diff)).AppendLine();
            }
            installResultBox.Text = text.ToString(); packageDiffBox.Text = diffs.Length > 0 ? diffs.ToString() : FormatDiff(result.Installation == null ? null : result.Installation.Diff);
        }
        private static void AppendInstallation(StringBuilder text, PluginInstallResult result)
        {
            if (result == null) { text.AppendLine("尚无安装器结果。"); return; }
            text.AppendLine("原安装服务结果：" + result.Status).AppendLine(result.Message).AppendLine("SHA256：" + (result.Sha256 ?? "未确认"))
                .AppendLine("临时目录：" + result.RemoteDirectory).AppendLine("安装器启动：" + result.InstallerStarted + "；结束：" + result.InstallerEnded + "；exit=" + result.InstallerExitCode)
                .AppendLine("临时文件已清理：" + result.CleanupSucceeded);
            if (result.Archive == null) text.AppendLine("Makeself：未取得解析结果；校验过程见完整日志。");
            else text.AppendLine("Makeself：" + result.Archive.MakeselfVersion + "；Identification=" + result.Archive.Identification)
                .AppendLine("目标目录：" + result.Archive.TargetDirectory + "；压缩方式：" + result.Archive.Compression + "；解压字节：" + result.Archive.UncompressedBytes)
                .AppendLine("包内架构 / SDK：" + result.Archive.Architecture + " / " + result.Archive.SdkSeries);
            if (result.Verification != null) text.AppendLine("独立验证：" + result.Verification.Verified + "；StrongRule=" + result.Verification.StrongRule)
                .AppendLine("主 package：" + result.Verification.PackageName + "；版本：" + result.Verification.Version).AppendLine(result.Verification.Message);
            foreach (string warning in result.Warnings) text.AppendLine("提示：" + warning);
        }
        private static string FormatDiff(PackageDiff diff)
        {
            if (diff == null) return "尚无软件包变化记录。";
            var text = new StringBuilder(); text.AppendLine("新增 " + diff.Added.Count + " · 版本变化 " + diff.Updated.Count + " · 删除 " + diff.Removed.Count).AppendLine();
            foreach (var item in diff.Added) text.AppendLine("新增  " + item.Name + "  " + item.AfterVersion);
            foreach (var item in diff.Updated) text.AppendLine("变化  " + item.Name + "  " + item.BeforeVersion + " → " + item.AfterVersion);
            foreach (var item in diff.Removed) text.AppendLine("删除  " + item.Name + "  " + item.BeforeVersion);
            return text.ToString();
        }
    }
}
