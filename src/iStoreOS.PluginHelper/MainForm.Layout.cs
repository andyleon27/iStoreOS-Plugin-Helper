using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;
using iStoreOS.PluginHelper.Controls;

namespace iStoreOS.PluginHelper
{
    public partial class MainForm
    {
        private Label connectionSummaryLabel, pluginSummaryLabel, filePathPreviewLabel;
        private Button saveLogButton;
        private LinkLabel detailsLink;
        private StatusDot connectionDot;
        private InstallationWorkflowResult lastWorkflowResult;
        private bool installerStateUnresolved;
        private Label Value(string text, string name)
        { return new Label { Text = text, Name = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = UiTheme.BodyText, Margin = Padding.Empty }; }
        private PictureBox Picture(string icon, int size)
        { return new PictureBox { Image = uiResources.Image(icon, size * 2), Size = new Size(size, size), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.None, Margin = Padding.Empty, BackColor = Color.Transparent }; }
        private PictureBox AccentedPicture(string icon, int size, Color accent)
        {
            var picture = Picture(icon, size);
            picture.Paint += (sender, args) =>
            {
                args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var brush = new SolidBrush(accent)) args.Graphics.FillEllipse(brush, 0, 0, picture.Width - 1, picture.Height - 1);
                if (picture.Image != null) { args.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; args.Graphics.DrawImage(picture.Image, picture.ClientRectangle); }
            };
            return picture;
        }
        private Button ActionButton(string text, string icon, int width, bool primary, string name)
        {
            return new RoundedButton { Text = text, Image = icon == null ? null : uiResources.Image(icon, 36), Width = width, Height = 32, Name = name, Margin = new Padding(0, 0, 10, 0),
                Kind = primary ? RoundedButtonKind.Primary : RoundedButtonKind.Neutral, Radius = primary ? 12 : 9, Font = uiResources.Font(9F, FontStyle.Regular) };
        }
        private static TableLayoutPanel Stack(params int[] rows)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows.Length, Margin = Padding.Empty };
            foreach (int height in rows) layout.RowStyles.Add(new RowStyle(height == 0 ? SizeType.Percent : SizeType.Absolute, height == 0 ? 100 : height));
            return layout;
        }
        private static TableLayoutPanel Columns(int count)
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = count, RowCount = 1, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            for (int i = 0; i < count; i++) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / count)); return layout;
        }
        private Panel Card(Control content, string name)
        {
            var card = new RoundedPanel { Name = name, Dock = DockStyle.Fill, Radius = 14, Padding = new Padding(16, 10, 16, 6), Margin = new Padding(0, 0, 0, 12) };
            card.Controls.Add(content); return card;
        }
        private Control Heading(string title, string icon)
        {
            var row = Columns(2); row.ColumnStyles.Clear(); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(AccentedPicture(icon, 22, UiTheme.IconAccent), 0, 0); var label = Value(title, ""); label.Font = uiResources.Font(11F, FontStyle.Bold); label.ForeColor = UiTheme.HeadingText; row.Controls.Add(label, 1, 0); return row;
        }
        private Control BuildTitle()
        {
            var layout = Stack(30, 18, 10);
            var row = Columns(3); row.ColumnStyles.Clear(); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            row.Controls.Add(Picture("app-logo", 32), 0, 0); var title = Value("iStoreOS 插件安装助手", "mainTitleLabel"); title.Font = uiResources.Font(15F, FontStyle.Bold); title.ForeColor = UiTheme.HeadingText; row.Controls.Add(title, 1, 0);
            var version = Value(ProductInfo.DisplayVersion, "appVersionLabel"); version.TextAlign = ContentAlignment.MiddleRight; version.ForeColor = UiTheme.VersionText; row.Controls.Add(version, 2, 0);
            layout.Controls.Add(row, 0, 0); var subtitle = Value("连接路由器 · 选择插件 · 一键安装", "subtitleLabel"); subtitle.ForeColor = UiTheme.Subtitle; subtitle.Padding = new Padding(46, 0, 0, 0); layout.Controls.Add(subtitle, 0, 1); return layout;
        }
        private Control Input(string caption, Control input, int spacing)
        {
            var panel = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, spacing, 0), BackColor = Color.Transparent };
            var label = Value(caption, ""); label.Dock = DockStyle.Top; label.Height = 20; label.ForeColor = UiTheme.FieldText;
            input.Dock = DockStyle.Fill; panel.Controls.Add(input); panel.Controls.Add(label); return panel;
        }
        private Control BuildConnectionCard()
        {
            var layout = Stack(26, 6, 52, 2, 18); layout.Controls.Add(Heading("连接路由器", "connect"), 0, 0);
            var inputs = Columns(5); inputs.ColumnStyles.Clear(); foreach (int width in new[] { 192, 132, 196, 44 }) inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width)); inputs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var hostInput = new RoundedTextBox { Name = "hostInput", Font = uiResources.Font(9.5F, FontStyle.Regular), Text = "192.168.100.1" }; hostBox = hostInput.Editor; hostBox.Name = "hostBox"; hostBox.MaxLength = 253;
            var usernameInput = new RoundedTextBox { Name = "usernameInput", Font = uiResources.Font(9.5F, FontStyle.Regular), Text = "root" }; usernameBox = usernameInput.Editor; usernameBox.Name = "usernameBox"; usernameBox.MaxLength = 128;
            var passwordInput = new RoundedTextBox { Name = "passwordInput", Font = uiResources.Font(9.5F, FontStyle.Regular), PasswordChar = '●' }; passwordBox = passwordInput.Editor; passwordBox.Name = "passwordBox"; passwordBox.MaxLength = 1024;
            passwordToggleButton = new RoundedIconButton { Name = "passwordToggleButton", Image = uiResources.Image("eye-show", 36), Width = 34, Height = 32, Margin = Padding.Empty };
            connectButton = ActionButton("连接", null, 124, false, "connectButton"); ((RoundedButton)connectButton).Kind = RoundedButtonKind.Secondary; connectButton.Font = uiResources.Font(9.5F, FontStyle.Bold);
            inputs.Controls.Add(Input("路由器地址", hostInput, 12), 0, 0); inputs.Controls.Add(Input("用户名", usernameInput, 12), 1, 0); inputs.Controls.Add(Input("密码", passwordInput, 8), 2, 0); inputs.Controls.Add(Input("", passwordToggleButton, 10), 3, 0); inputs.Controls.Add(Input("", connectButton, 0), 4, 0); layout.Controls.Add(inputs, 0, 2);
            connectionLabel = Value("等待连接", "connectionLabel"); connectionLabel.ForeColor = UiTheme.WaitingText;
            var status = Columns(2); status.ColumnStyles.Clear(); status.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 18)); status.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            connectionDot = new StatusDot { Name = "connectionDot", Anchor = AnchorStyles.None }; status.Controls.Add(connectionDot, 0, 0); status.Controls.Add(connectionLabel, 1, 0);
            connectionSummaryLabel = Value("", "connectionSummaryLabel"); connectionSummaryLabel.Visible = false;
            layout.Controls.Add(status, 0, 4); var card = Card(layout, "connectionCard"); card.Padding = new Padding(16, 10, 16, 4); return card;
        }
        private Control BuildPluginCard()
        {
            var layout = Stack(28, 8, 72, 24, 16); layout.Controls.Add(Heading("选择插件", "plugin"), 0, 0);
            var drop = new RoundedDropZone { Name = "dropArea", Dock = DockStyle.Fill, Padding = new Padding(18, 0, 18, 0), Margin = Padding.Empty };
            var dropLayout = Columns(2); dropLayout.ColumnStyles.Clear(); dropLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); dropLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 102));
            var caption = Columns(4); caption.ColumnStyles.Clear(); caption.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); caption.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36)); caption.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); caption.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var upload = AccentedPicture("upload", 26, UiTheme.UploadAccent); upload.Image = uiResources.Image("upload", 52, UiTheme.UploadIcon); upload.Name = "dropUploadIcon"; caption.Controls.Add(upload, 1, 0);
            dropLabel = new DropCaption { Text = "拖入 Are-u-ok .run 插件\n或点击选择文件", Name = "dropLabel", Dock = DockStyle.Fill, AllowDrop = true, Font = uiResources.Font(10F, FontStyle.Regular), Margin = Padding.Empty };
            caption.Controls.Add(dropLabel, 2, 0);
            chooseButton = ActionButton("选择文件", null, 102, false, "chooseButton"); chooseButton.Height = 36; chooseButton.Anchor = AnchorStyles.None; chooseButton.Margin = Padding.Empty;
            dropLayout.Controls.Add(caption, 0, 0); dropLayout.Controls.Add(chooseButton, 1, 0); drop.Controls.Add(dropLayout);
            drop.Click += ChooseButton_Click; dropLabel.Click += ChooseButton_Click; drop.DragEnter += DropLabel_DragEnter; drop.DragDrop += DropLabel_DragDrop;
            dropLayout.Click += ChooseButton_Click; caption.Click += ChooseButton_Click; upload.Click += ChooseButton_Click;
            foreach (var control in new Control[] { dropLayout, caption, upload }) { control.AllowDrop = true; control.DragEnter += DropLabel_DragEnter; control.DragDrop += DropLabel_DragDrop; drop.TrackHover(control); }
            drop.TrackHover(chooseButton);
            layout.Controls.Add(drop, 0, 2);
            pluginNameLabel = Value("尚未选择插件", "pluginNameLabel"); pluginNameLabel.Font = uiResources.Font(9.5F, FontStyle.Regular); pluginNameLabel.ForeColor = UiTheme.PluginEmptyText;
            pluginNameLabel.Paint += (sender, args) =>
            {
                float scale = pluginNameLabel.Height / 24F; int textHeight = (int)(22 * scale), iconSize = Math.Max(1, (int)(16 * scale)), inset = (int)(24 * scale);
                using (var brush = new SolidBrush(Color.White)) args.Graphics.FillRectangle(brush, pluginNameLabel.ClientRectangle);
                if (plugin != null)
                {
                    // Add breathing space without changing the fixed summary row or empty-state position.
                    TextRenderer.DrawText(args.Graphics, pluginNameLabel.Text, pluginNameLabel.Font, new Rectangle(0, (int)(4 * scale), pluginNameLabel.Width, textHeight), pluginNameLabel.ForeColor,
                        TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    return;
                }
                var icon = uiResources.Image("file", 32, UiTheme.FileIcon);
                if (icon != null) { args.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; args.Graphics.DrawImage(icon, new Rectangle((int)(2 * scale), (textHeight - iconSize) / 2, iconSize, iconSize)); }
                TextRenderer.DrawText(args.Graphics, pluginNameLabel.Text, pluginNameLabel.Font, new Rectangle(inset, 0, Math.Max(1, pluginNameLabel.Width - inset), textHeight), pluginNameLabel.ForeColor,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            };
            pluginNameLabel.TextChanged += (sender, args) => { pluginNameLabel.Font = uiResources.Font(plugin == null ? 9.5F : 10.5F, plugin == null ? FontStyle.Regular : FontStyle.Bold); pluginNameLabel.ForeColor = plugin == null ? UiTheme.PluginEmptyText : UiTheme.Ink; };
            pluginSummaryLabel = Value("", "pluginSummaryLabel"); pluginSummaryLabel.Visible = false;
            filePathPreviewLabel = Value("", "filePathPreviewLabel"); filePathPreviewLabel.Visible = false;
            compatibilityLabel = Value("", "compatibilityLabel"); compatibilityLabel.Font = uiResources.Font(9F, FontStyle.Regular);
            layout.Controls.Add(pluginNameLabel, 0, 3); layout.Controls.Add(compatibilityLabel, 0, 4); var card = Card(layout, "pluginCard"); card.Margin = new Padding(0, 0, 0, 16); return card;
        }
        private Control BuildInstallActions()
        {
            var row = Columns(1);
            installButton = ActionButton("安装插件", null, 200, true, "installButton"); installButton.Height = 40; installButton.Font = uiResources.Font(10.5F, FontStyle.Bold); installButton.Enabled = false; installButton.Anchor = AnchorStyles.Top; installButton.Margin = Padding.Empty;
            row.Controls.Add(installButton, 0, 0); return row;
        }
        private Control BuildInstallCard()
        {
            var layout = Stack(20, 18);
            installHeadlineLabel = Value("准备就绪后即可安装", "installHeadlineLabel"); installHeadlineLabel.Font = uiResources.Font(9.5F, FontStyle.Regular); installHeadlineLabel.ForeColor = UiTheme.StatusText;
            installStepLabel = Value("", "installStepLabel"); installStepLabel.ForeColor = UiTheme.StatusHelp; installStepLabel.Font = uiResources.Font(8.5F, FontStyle.Regular);
            layout.Controls.Add(installHeadlineLabel, 0, 0); layout.Controls.Add(installStepLabel, 0, 1);
            installCard = new Panel { Dock = DockStyle.Fill, Name = "installCard", Margin = Padding.Empty, Padding = new Padding(8, 0, 8, 0) }; installCard.Controls.Add(layout); return installCard;
        }
        private Control BuildLogActions()
        {
            toggleLogButton = new LinkLabel { Name = "toggleLogButton", Text = "高级信息 ›", Dock = DockStyle.Left, Width = 110, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft,
                Font = uiResources.Font(9F, FontStyle.Regular), LinkColor = UiTheme.LinkNormal, ActiveLinkColor = UiTheme.LinkHover, VisitedLinkColor = UiTheme.LinkNormal, LinkBehavior = LinkBehavior.HoverUnderline };
            toggleLogButton.MouseEnter += (sender, args) => toggleLogButton.LinkColor = UiTheme.LinkHover;
            toggleLogButton.MouseLeave += (sender, args) => toggleLogButton.LinkColor = UiTheme.LinkNormal;
            detailsLink = new LinkLabel { Name = "detailsLink", Text = "查看详细信息", Dock = DockStyle.Right, Width = 110, TextAlign = ContentAlignment.MiddleRight, Visible = false, LinkColor = UiTheme.Blue };
            detailsLink.LinkClicked += (sender, args) => ShowAdvancedInformation(true);
            var panel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(8, 0, 8, 12) }; panel.Controls.Add(toggleLogButton); panel.Controls.Add(detailsLink); return panel;
        }
        private void SetWorkflowStage(int stage) { }
        private void SetStatusAppearance(Label text, UiTone tone)
        {
            text.ForeColor = text == connectionLabel ? tone == UiTone.Neutral ? UiTheme.WaitingText : tone == UiTone.Success ? UiTheme.ConnectionSuccess : UiTheme.Fore(tone) :
                text == installHeadlineLabel ? tone == UiTone.Neutral ? UiTheme.StatusText : tone == UiTone.Success ? UiTheme.StatusSuccess : tone == UiTone.Warning ? UiTheme.StatusWarning : UiTheme.Error :
                text == compatibilityLabel && tone == UiTone.Success ? UiTheme.CompatibleText : UiTheme.Fore(tone);
            uiToolTip.SetToolTip(text, text.Text); if (text == connectionLabel && connectionDot != null) connectionDot.DotColor = tone == UiTone.Neutral ? UiTheme.Waiting : UiTheme.Fore(tone);
        }
        private void SetInstallSummary(string title, string detail, UiTone tone, bool expanded)
        {
            installHeadlineLabel.Text = title; SetStatusAppearance(installHeadlineLabel, tone);
            installResultBox.Text = detail; installStepLabel.Text = FirstLine(detail); uiToolTip.SetToolTip(installStepLabel, detail);
            detailsLink.Visible = tone == UiTone.Error || tone == UiTone.Warning;
        }
        private static string FirstLine(string text)
        { var lines = (text ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); return lines.Length == 0 ? "" : lines[0]; }
        private void DisplayInstallResult(PluginInstallResult result)
        { SetInstallSummary(UiResultFormatter.Title(result), UiResultFormatter.Details(result), UiResultFormatter.Tone(result.Status), true); DisplayAdvancedInstallation(result); }
        private void DisplayWorkflowResult(InstallationWorkflowResult result)
        {
            lastWorkflowResult = result;
            installerStateUnresolved = result.PendingInstaller;
            installationRequiresReview = result.State == InstallationState.Success || !result.CanRetry;
            if (result.Router != null) { router = result.Router; DisplayEnvironment(); }
            if (ssh.IsConnected) { connectionLossReported = false; DisplayEnvironment(); SetStatusAppearance(connectionLabel, UiTone.Success); }
            if (result.Software != null) DisplaySoftwareResult(result.Software);
            var installed = result.Installation;
            if (result.State == InstallationState.Success && installed != null && installed.Verification != null && installed.Verification.Verified)
            {
                string detail = "已验证 " + installed.Verification.PackageName + " · " + (installed.CleanupSucceeded ? "临时文件已清理" : "清理状态待确认，请查看高级信息");
                if (result.Warnings.Count > 0) detail += " · 有收尾提示";
                SetInstallSummary("✓ " + (installed.Plugin == null ? "插件" : installed.Plugin.PluginName) + " 安装成功", detail, result.Warnings.Count > 0 ? UiTone.Warning : UiTone.Success, true);
            }
            else
            {
                string message = result.Failure == null ? "请查看高级信息并保存日志，确认安装结果。" : result.Failure.UserMessage;
                UiTone tone = result.PendingInstaller || result.CanRetry || result.Failure == null || result.Failure.Kind == InstallationFailureKind.VerificationIncomplete ? UiTone.Warning : UiTone.Error;
                SetInstallSummary(result.PendingInstaller ? "安装状态待确认" : result.Failure != null && result.Failure.Kind == InstallationFailureKind.VerificationIncomplete ? "安装结果需要确认" : "未完成安装", message, tone, true);
            }
            DisplayAdvancedWorkflow(result);
            RefreshInstallButton(); RefreshLogButtons();
        }
        private void ToggleLogButton_Click(object sender, EventArgs e)
        { ShowAdvancedInformation(false); }
        private void PasswordToggleButton_Click(object sender, EventArgs e)
        { SetPasswordVisibility(passwordBox.PasswordChar != '\0'); passwordBox.Focus(); passwordBox.SelectionStart = passwordBox.TextLength; }
        private void SetPasswordVisibility(bool visible)
        {
            passwordBox.PasswordChar = visible ? '\0' : '●'; passwordToggleButton.Image = uiResources.Image(visible ? "eye-hide" : "eye-show", 36);
            passwordToggleButton.AccessibleName = visible ? "隐藏密码" : "显示密码"; uiToolTip.SetToolTip(passwordToggleButton, passwordToggleButton.AccessibleName);
            passwordToggleButton.Text = passwordToggleButton.Image == null ? visible ? "隐" : "显" : "";
        }
        private void CopyLogButton_Click(object sender, EventArgs e)
        {
            string text = logBox.SelectionLength > 0 ? logBox.SelectedText : GetAllLogText();
            try { Clipboard.SetText(text); uiToolTip.Show("日志已复制", copyLogButton, 1500); }
            catch (Exception) { AppendLog("剪贴板暂不可用，可使用保存日志。"); }
        }
    }
}
