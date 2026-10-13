using System.Drawing;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components;
        private TextBox hostBox, usernameBox, passwordBox, fileNameBox, filePathBox, installResultBox;
        private Button connectButton, chooseButton, softwareButton, saveSoftwareLogButton, installButton, cancelInstallButton, saveInstallLogButton;
        private Button passwordToggleButton, copyLogButton;
        private LinkLabel toggleLogButton;
        private Label connectionLabel, versionLabel, sdkLabel, architectureLabel, managerLabel, quickStartLabel;
        private Label dropLabel, fileSizeLabel, pluginNameLabel, pluginVersionLabel, pluginArchitectureLabel, pluginSdkLabel, pluginTypeLabel, compatibilityLabel, cancelPreparationHint;
        private Label currentSourceLabel, officialSourceLabel, auxiliarySourceLabel, updateSourceLabel, softwareStatusLabel, installStepLabel, installHeadlineLabel;
        private RichTextBox logBox;
        private OpenFileDialog fileDialog;
        private Timer connectionTimer;
        private ProgressBar softwareProgressBar, installProgressBar;
        private Panel scrollPanel, installCard, logPanel;
        private TableLayoutPanel pageLayout;
        private ToolTip uiToolTip;
        private readonly UiResources uiResources = new UiResources();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing.Cancel();
                if (passwordBox != null) passwordBox.Clear();
                ssh.Dispose();
            }
            if (disposing && advancedInfoForm != null) { advancedInfoForm.Dispose(); advancedInfoForm = null; }
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
            if (disposing) uiResources.Dispose();
        }
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            uiToolTip = new ToolTip(components) { AutoPopDelay = 15000 };
            SuspendLayout();
            Font = uiResources.Font(9F, FontStyle.Regular); ForeColor = UiTheme.Ink; BackColor = UiTheme.Background;
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Text = ProductInfo.WindowTitle; ClientSize = new Size(760, 500);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            var windowIcon = uiResources.WindowIcon(); if (windowIcon != null) Icon = windowIcon;
            scrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = false, Name = "scrollPanel" };
            pageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty, Padding = new Padding(20, 14, 20, 4) };
            foreach (int height in new[] { 58, 130, 180, 40, 40, 22 }) pageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            pageLayout.Controls.Add(BuildTitle(), 0, 0);
            pageLayout.Controls.Add(BuildConnectionCard(), 0, 1);
            pageLayout.Controls.Add(BuildPluginCard(), 0, 2);
            pageLayout.Controls.Add(BuildInstallActions(), 0, 3);
            pageLayout.Controls.Add(BuildInstallCard(), 0, 4);
            pageLayout.Controls.Add(BuildLogActions(), 0, 5);
            scrollPanel.Controls.Add(pageLayout); Controls.Add(scrollPanel);
            advancedInfoForm = new AdvancedInfoForm(BuildLogPanel(), windowIcon);
            fileDialog = new OpenFileDialog { Filter = "Are-u-ok 插件 (*.run)|*.run", CheckFileExists = true, Multiselect = false, Title = "选择本地 .run 插件" };
            components.Add(fileDialog); connectionTimer = new Timer(components) { Interval = 1000 };
            AcceptButton = connectButton;
            connectButton.Click += ConnectButton_Click; chooseButton.Click += ChooseButton_Click;
            dropLabel.DragEnter += DropLabel_DragEnter; dropLabel.DragDrop += DropLabel_DragDrop;
            connectionTimer.Tick += ConnectionTimer_Tick;
            softwareButton.Click += SoftwareButton_Click; saveSoftwareLogButton.Click += SaveSoftwareLogButton_Click;
            installButton.Click += InstallButton_Click; cancelInstallButton.Click += CancelInstallButton_Click; saveInstallLogButton.Click += SaveInstallLogButton_Click;
            passwordToggleButton.Click += PasswordToggleButton_Click; toggleLogButton.LinkClicked += (sender, args) => ToggleLogButton_Click(sender, args); copyLogButton.Click += CopyLogButton_Click;
            var dropZone = (Controls.RoundedDropZone)Controls.Find("dropArea", true)[0]; dropZone.TrackHover(dropLabel);
            ResumeLayout(true);
            MinimumSize = Size;
        }
    }
}
