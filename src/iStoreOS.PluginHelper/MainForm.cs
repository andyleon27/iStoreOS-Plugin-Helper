using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper
{
    public partial class MainForm : Form
    {
        private readonly SshService ssh = new SshService();
        private readonly PluginPackageAnalyzer analyzer = new PluginPackageAnalyzer();
        private readonly CompatibilityService compatibility = new CompatibilityService();
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private RouterEnvironment router;
        private PluginPackageInfo plugin;
        private bool isConnecting;
        private bool connectionLossReported;

        public MainForm()
        {
            InitializeComponent();
            SetPasswordVisibility(false);
            AppendLog(ProductInfo.DisplayVersion + " (" + ProductInfo.Version + ") 就绪：连接路由器、选择兼容的本地 .run，点击安装。临时切换官方源后尝试恢复原配置。");
            foreach (string warning in uiResources.Warnings) AppendLog("Warning：" + warning);
            connectionTimer.Start();
        }

        private async void ConnectButton_Click(object sender, EventArgs e)
        {
            if (isConnecting || isSoftwareBusy || isInstallBusy) return;
            string host = hostBox.Text.Trim();
            string username = usernameBox.Text.Trim();
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.IndexOfAny(new[] { '/', '\\', ' ', '\t', '\r', '\n' }) >= 0)
            { connectionLabel.Text = "请输入有效的路由器地址。"; SetStatusAppearance(connectionLabel, UiTone.Error); return; }
            if (string.IsNullOrWhiteSpace(username))
            { connectionLabel.Text = "请输入用户名。"; SetStatusAppearance(connectionLabel, UiTone.Error); return; }
            if (string.IsNullOrEmpty(passwordBox.Text))
            { connectionLabel.Text = "请输入密码。"; SetStatusAppearance(connectionLabel, UiTone.Error); return; }

            isConnecting = true;
            SetWorkflowStage(0);
            installationRequiresReview = false; lastWorkflowResult = null; connectionLossReported = false; RefreshInstallButton();
            softwareButton.Enabled = false;
            ResetSoftwareState();
            SetConnectionInputs(false);
            router = null; // Never compare a newly selected router against stale detection data.
            DisplayEnvironment();
            RefreshCompatibility();
            connectionLabel.Text = "正在连接…"; SetStatusAppearance(connectionLabel, UiTone.Neutral);
            string password = passwordBox.Text;
            passwordBox.Clear();
            SetPasswordVisibility(false);
            try
            {
                var progress = new Progress<string>(AppendLog);
                ConnectionResult result = await ssh.ConnectAndDetectAsync(host, username, password, progress, closing.Token);
                if (closing.IsCancellationRequested) return;
                router = result.Success ? result.Environment : null;
                DisplayConnectionOutcome(result);
                SetWorkflowStage(result.Success ? 1 : 0);
                DisplayEnvironment();
                RefreshCompatibility();
            }
            catch (Exception exception)
            {
                if (!closing.IsCancellationRequested)
                {
                    connectionLabel.Text = "连接检测失败，请查看详细日志。";
                    SetStatusAppearance(connectionLabel, UiTone.Error);
                    AppendLog(LogHelper.ExceptionDetails(exception, password));
                }
            }
            finally
            {
                password = null;
                isConnecting = false;
                if (!closing.IsCancellationRequested) SetConnectionInputs(true);
                if (!closing.IsCancellationRequested) softwareButton.Enabled = !ssh.HasPendingInstallation && router != null && router.IsConnected && router.PackageManager != PackageManagerKind.Unknown;
                RefreshInstallButton();
            }
        }

        private void DisplayConnectionOutcome(ConnectionResult result)
        {
            connectionLabel.Text = result.Success ? "✓ 已连接到 iStoreOS" : "✗ " + result.UserMessage;
            SetStatusAppearance(connectionLabel, result.Success ? UiTone.Success : UiTone.Error);
            uiToolTip.SetToolTip(connectionLabel, connectionLabel.Text);
        }

        private void SetConnectionInputs(bool enabled)
        {
            connectButton.Enabled = enabled; hostBox.Enabled = enabled;
            usernameBox.Enabled = enabled; passwordBox.Enabled = enabled;
            passwordToggleButton.Enabled = enabled;
            connectButton.Text = enabled ? "连接" : "连接中…";
        }

        private void ChooseButton_Click(object sender, EventArgs e)
        {
            if (isInstallBusy) return;
            if (fileDialog.ShowDialog(this) == DialogResult.OK) SelectPlugin(fileDialog.FileName);
        }

        private void DropLabel_DragEnter(object sender, DragEventArgs e)
        {
            if (isInstallBusy) { e.Effect = DragDropEffects.None; return; }
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            e.Effect = paths != null && paths.Length == 1 &&
                string.Equals(Path.GetExtension(paths[0]), ".run", StringComparison.OrdinalIgnoreCase)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void DropLabel_DragDrop(object sender, DragEventArgs e)
        {
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths != null && paths.Length == 1) SelectPlugin(paths[0]);
        }

        private void SelectPlugin(string path)
        {
            if (isInstallBusy) return;
            try
            {
                plugin = analyzer.AnalyzeFile(path);
                installationRequiresReview = false; lastWorkflowResult = null;
                fileNameBox.Text = plugin.FileName; filePathBox.Text = plugin.FilePath;
                uiToolTip.SetToolTip(fileNameBox, plugin.FileName); uiToolTip.SetToolTip(filePathBox, plugin.FilePath);
                fileNameBox.SelectionStart = 0; filePathBox.SelectionStart = 0;
                SetWorkflowStage(2);
                fileSizeLabel.Text = "文件大小：" + (plugin.FileSize / 1048576.0).ToString("N2") + " MiB（" + plugin.FileSize.ToString("N0") + " 字节）";
                pluginNameLabel.Text = plugin.PluginName + " " + Display(plugin.PluginVersion);
                pluginSummaryLabel.Text = Display(plugin.Architecture) + " · SDK " + Display(plugin.SdkSeries);
                filePathPreviewLabel.Text = plugin.FilePath; uiToolTip.SetToolTip(filePathPreviewLabel, plugin.FilePath);
                pluginVersionLabel.Text = "版本：" + Display(plugin.PluginVersion);
                pluginArchitectureLabel.Text = "架构：" + Display(plugin.Architecture);
                pluginSdkLabel.Text = "SDK：" + Display(plugin.SdkSeries);
                pluginTypeLabel.Text = "类型：" + Display(plugin.PackageType);
                AppendLog("已选择 .run：" + plugin.FilePath + "；大小：" + plugin.FileSize + " 字节。");
                AppendLog("解析结果：插件 " + plugin.PluginName + "；版本 " + Display(plugin.PluginVersion) +
                    "；架构 " + Display(plugin.Architecture) + "；SDK " + Display(plugin.SdkSeries) + "；类型 " + Display(plugin.PackageType));
                RefreshCompatibility();
            }
            catch (Exception exception)
            {
                // A failed selection cannot leave a previous package looking compatible.
                plugin = null;
                pluginSummaryLabel.Text = ""; filePathPreviewLabel.Text = "";
                fileNameBox.Clear(); filePathBox.Clear(); fileSizeLabel.Text = "文件大小：—";
                pluginNameLabel.Text = "尚未选择插件"; pluginVersionLabel.Text = "版本：—";
                pluginArchitectureLabel.Text = "架构：—"; pluginSdkLabel.Text = "SDK：—"; pluginTypeLabel.Text = "类型：—";
                compatibilityLabel.Text = "无法读取插件文件，请检查文件路径和访问权限。";
                SetStatusAppearance(compatibilityLabel, UiTone.Error);
                compatibilityDetailLabel.Text = compatibilityLabel.Text;
                SetStatusAppearance(compatibilityDetailLabel, UiTone.Error);
                AppendLog("文件分析失败：" + LogHelper.ExceptionDetails(exception, null));
            }
            RefreshInstallButton();
        }

        private void DisplayEnvironment()
        {
            connectionSummaryLabel.Text = router == null ? "" : Display(router.Version) + " · SDK " + Display(router.SdkSeries) + " · " + Display(router.Architecture) + " · " + router.PackageManager;
            if (router != null && router.IsConnected && !connectionLossReported)
            { connectionLabel.Text = "已连接  iStoreOS " + connectionSummaryLabel.Text; SetStatusAppearance(connectionLabel, UiTone.Success); }
            systemDetailsBox.Text = router == null ? "连接路由器后显示当前设备信息。" : "路由器地址：" + hostBox.Text.Trim() + "\r\n连接状态：" + (router.IsConnected ? "已连接" : "已断开") + "\r\nSSH 指纹：" + router.HostKeyFingerprint;
            versionLabel.Text = router == null ? "—" : Display(router.Version);
            sdkLabel.Text = router == null ? "—" : Display(router.SdkSeries);
            architectureLabel.Text = router == null ? "—" : Display(router.Architecture);
            managerLabel.Text = router == null ? "—" : router.PackageManager.ToString();
            quickStartLabel.Text = router == null ? "QuickStart：—" : router.QuickStartAvailable ? "QuickStart：可用" : "QuickStart：不可用";
            quickStartLabel.ForeColor = UiTheme.Fore(router == null ? UiTone.Neutral : router.QuickStartAvailable ? UiTone.Success : UiTone.Warning);
        }

        private void RefreshCompatibility()
        {
            if (plugin == null)
            {
                compatibilityLabel.Text = "";
                compatibilityDetailLabel.Text = "尚未选择插件";
                SetStatusAppearance(compatibilityLabel, UiTone.Neutral);
                RefreshInstallButton();
                return;
            }
            compatibility.Evaluate(plugin, router);
            compatibilityLabel.Text = plugin.CompatibilityStatus == CompatibilityStatus.Compatible ? "✓ 当前设备兼容 · " + Display(plugin.Architecture) + " · SDK " + Display(plugin.SdkSeries) :
                plugin.CompatibilityStatus == CompatibilityStatus.Incompatible ? "✗ 当前插件与设备不兼容" : router == null ? "请先连接路由器，再检查兼容性。" : "无法确定兼容性，请检查插件包。";
            compatibilityDetailLabel.Text = plugin.CompatibilityStatus + "；" + plugin.CompatibilityMessage;
            SetStatusAppearance(compatibilityDetailLabel, plugin.CompatibilityStatus == CompatibilityStatus.Compatible ? UiTone.Success : plugin.CompatibilityStatus == CompatibilityStatus.Incompatible ? UiTone.Error : UiTone.Warning);
            uiToolTip.SetToolTip(compatibilityLabel, plugin.CompatibilityMessage);
            SetStatusAppearance(compatibilityLabel, plugin.CompatibilityStatus == CompatibilityStatus.Compatible ? UiTone.Success :
                plugin.CompatibilityStatus == CompatibilityStatus.Incompatible ? UiTone.Error : UiTone.Warning);
            AppendLog("兼容性：" + plugin.CompatibilityStatus + "；" + plugin.CompatibilityMessage);
            RefreshInstallButton();
        }

        private void ConnectionTimer_Tick(object sender, EventArgs e)
        {
            if (isConnecting || isInstallBusy || router == null || !router.IsConnected || ssh.IsConnected || connectionLossReported) return;
            connectionLossReported = true;
            if (ssh.CanReconnect)
            {
                connectionLabel.Text = "连接已中断，点击安装时将自动重连并复核设备。";
                SetStatusAppearance(connectionLabel, UiTone.Warning); AppendLog("SSH 中断；保留会话内认证信息和原设备身份供自动重连，不写入磁盘。"); RefreshInstallButton(); return;
            }
            router.IsConnected = false;
            softwareButton.Enabled = false;
            if (!isSoftwareBusy) softwareStatusLabel.Text = "SSH 已断开，软件环境状态需重新检测。";
            connectionLabel.Text = "SSH 连接已断开，请重新连接并检测。";
            SetStatusAppearance(connectionLabel, UiTone.Error); SetStatusAppearance(softwareStatusLabel, UiTone.Warning);
            AppendLog("SSH 连接已断开，兼容性结论已失效。");
            RefreshCompatibility();
        }

        private void AppendLog(string message)
        {
            if (IsDisposed || Disposing || closing.IsCancellationRequested) return;
            if (logBox.TextLength > 200000) logBox.Clear();
            string line = LogHelper.Timestamp((message ?? string.Empty).Replace("\r", " ").Replace("\n", " ")) + Environment.NewLine;
            logBox.AppendText(line);
            RecordDiagnosticLine(line);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            connectionTimer.Stop();
            closing.Cancel();
            passwordBox.Clear();
            ssh.Dispose();
            base.OnFormClosed(e);
        }

        private static string Display(string value) { return string.IsNullOrWhiteSpace(value) ? "未识别" : value; }
    }
}
