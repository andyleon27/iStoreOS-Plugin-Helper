using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Controls;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper
{
    public delegate Task<MaintenanceMenuRunResult> MaintenanceMenuRunner(Action<MaintenanceMenuTerminal> ready,
        IProgress<MaintenanceOperationLog> progress, CancellationToken token, uint columns, uint rows);

    public sealed class PluginMaintenanceTerminalForm : Form
    {
        private readonly UiResources resources = new UiResources();
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 20000 };
        private readonly System.Windows.Forms.Timer statusTimer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly MaintenanceMenuRunner run;
        private readonly Func<MaintenanceMenuAvailability> availability;
        private readonly Func<bool> connected;
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationTokenRegistration ownerRegistration;
        private readonly TerminalView terminalView;
        private readonly Label connectionStatus, operationStatus, detail;
        private readonly RoundedButton returnHome;
        private MaintenanceMenuTerminal terminal;
        private Task operation;
        private bool started, busy, closeRequested, allowClose, resourcesDisposed;
        private int ownerClosed;
        private bool computerProxyPath;
        public MaintenanceMenuRunResult Result { get; private set; }
        public Task Completion { get { return operation ?? Task.FromResult(0); } }

        public PluginMaintenanceTerminalForm(MaintenanceMenuRunner run, Func<MaintenanceMenuAvailability> availability,
            Func<bool> connected, Icon icon, CancellationToken ownerToken = default(CancellationToken))
        {
            if (run == null || availability == null || connected == null) throw new ArgumentNullException("run");
            this.run = run; this.availability = availability; this.connected = connected;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
            Name = "PluginMaintenanceTerminalForm"; Text = "插件维护 · 菜单终端";
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            Font = resources.Font(9F, FontStyle.Regular); ForeColor = UiTheme.Ink; BackColor = UiTheme.Background;
            ClientSize = new Size(960, 660); MinimumSize = new Size(800, 540);
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; Padding = new Padding(16);
            if (icon != null) Icon = icon;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 56, 26, 28, 48 }) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.Controls.Add(Label("插件维护终端", "maintenanceTerminalTitle", 13F, FontStyle.Bold), 0, 0);
            connectionStatus = Label("正在检查 SSH 连接…", "maintenanceTerminalConnection"); connectionStatus.TextAlign = ContentAlignment.MiddleRight;
            header.Controls.Add(connectionStatus, 1, 0);
            var source = Label("维护引擎：slobys/openclash-auto-installer · MIT License · " + ThirdPartyScriptCatalog.Copyright, "maintenanceTerminalSource", 8.5F);
            source.ForeColor = UiTheme.SecondaryText; header.Controls.Add(source, 0, 1); header.SetColumnSpan(source, 2); root.Controls.Add(header, 0, 0);
            root.Controls.Add(Label("优先使用软路由直连；不可用时使用电脑 HTTP 代理。启动前会再次复核 GitHub Raw。", "maintenanceTerminalNotice", 9F), 0, 1);
            operationStatus = Label("等待启动菜单…", "maintenanceTerminalState", 10F, FontStyle.Bold); root.Controls.Add(operationStatus, 0, 2);
            detail = Label("正在准备菜单会话。", "maintenanceTerminalDetail"); detail.ForeColor = UiTheme.SecondaryText; root.Controls.Add(detail, 0, 3);
            terminalView = new TerminalView { Name = "maintenanceTerminalView", Margin = new Padding(0, 4, 0, 4), TabIndex = 0 };
            terminalView.InputRejected += message => { if (busy && !closeRequested) SetDetail(message); };
            root.Controls.Add(terminalView, 0, 4);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            var hint = Label("键盘输入 · Ctrl+C 中断 · Ctrl+V 粘贴 · 选中文字后 Ctrl+C 复制", "maintenanceTerminalHelp", 8.5F); hint.ForeColor = UiTheme.SecondaryText;
            footer.Controls.Add(hint, 0, 0);
            returnHome = new RoundedButton { Name = "maintenanceTerminalReturn", Text = "返回首页", Kind = RoundedButtonKind.Secondary,
                Dock = DockStyle.Fill, Margin = new Padding(6, 4, 0, 0), TabIndex = 1 };
            returnHome.Click += (sender, args) => RequestReturn(); footer.Controls.Add(returnHome, 1, 0);
            root.Controls.Add(footer, 0, 5); Controls.Add(root);
            statusTimer.Tick += (sender, args) => RefreshStatus();
            ownerRegistration = ownerToken.Register(() => {
                Interlocked.Exchange(ref ownerClosed, 1);
                if (IsHandleCreated && !IsDisposed && !Disposing) {
                    try { BeginInvoke(new Action(RequestReturn)); } catch (InvalidOperationException) { }
                }
            });
        }
        private Label Label(string text, string name, float size = 9F, FontStyle style = FontStyle.Regular)
        { return new Label { Name = name, Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty, Font = resources.Font(size, style) }; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (started || IsDisposed || Disposing || closeRequested) return;
            started = true;
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            if (Volatile.Read(ref ownerClosed) != 0 || lifetime.IsCancellationRequested) { RequestReturn(); return; }
            statusTimer.Start(); RefreshStatus();
            operation = RunSessionAsync();
        }
        private async Task RunSessionAsync()
        {
            busy = true;
            try
            {
                // This can differ from the homepage's click-time snapshot.
                var current = availability();
                if (current == null || !current.CanOpen) {
                    SetState("菜单暂不可用", UiTheme.Warning); SetDetail(current == null ? "当前连接状态无法确认。" : current.Message); return;
                }
                if (closeRequested || lifetime.IsCancellationRequested) return;
                computerProxyPath = current.Status == MaintenanceMenuAvailabilityStatus.ReadyViaComputerProxy;
                SetState("正在准备菜单…", UiTheme.SecondaryBlue);
                var progress = new Progress<MaintenanceOperationLog>(UpdatePreparation);
                var size = GetTerminalSize();
                Result = await run(BindTerminal, progress, lifetime.Token, size.Item1, size.Item2);
                // The runner has joined the network reader. Keep the UI alive to draw every queued chunk.
                while (!IsDisposed && !Disposing && terminalView.PendingChunkCount > 0) {
                    terminalView.RenderPending(); if (terminalView.PendingChunkCount > 0) await Task.Delay(1);
                }
                if (!IsDisposed && !Disposing) DisplayResult();
            }
            catch (OperationCanceledException) { if (!IsDisposed && !Disposing) { SetState("菜单准备已取消", UiTheme.Warning); SetDetail("已结束本次菜单准备。"); } }
            catch (Exception) { if (!IsDisposed && !Disposing) { SetState("菜单会话未能完成", UiTheme.Error); SetDetail("请返回首页检查连接后再打开。未自动重试菜单。"); } }
            finally
            {
                busy = false;
                if (!IsDisposed && !Disposing) {
                    if (closeRequested) FinishReturn(); else { returnHome.Enabled = true; RefreshStatus(); }
                }
            }
        }
        private void BindTerminal(MaintenanceMenuTerminal value)
        {
            if (IsDisposed || Disposing || closeRequested || lifetime.IsCancellationRequested) throw new OperationCanceledException();
            if (InvokeRequired) { Invoke(new Action(() => BindTerminal(value))); return; }
            terminal = value; terminalView.Bind(value); terminalView.Focus();
            SetState("正在启动菜单…", UiTheme.SecondaryBlue); SetDetail("请在终端中按照原项目菜单提示操作。");
        }
        public Tuple<uint, uint> GetTerminalSize()
        {
            if (IsDisposed || Disposing || closeRequested || lifetime.IsCancellationRequested) throw new OperationCanceledException();
            if (InvokeRequired) return (Tuple<uint, uint>)Invoke(new Func<Tuple<uint, uint>>(GetTerminalSize));
            PerformLayout(); terminalView.Parent.PerformLayout();
            return terminalView.GetViewportSize();
        }
        private void UpdatePreparation(MaintenanceOperationLog entry)
        {
            if (!IsDisposed && !Disposing && busy && !closeRequested && entry.Stage == MaintenancePreparationStage.Network) {
                if (entry.Message.StartsWith("网络路径：")) computerProxyPath = entry.Message.StartsWith("网络路径：电脑代理");
                SetState("正在复核维护网络…", UiTheme.SecondaryBlue); SetDetail(entry.Message); return;
            }
            if (IsDisposed || Disposing || !busy || closeRequested || Result != null || terminal != null) return;
            string text = entry.Stage == MaintenancePreparationStage.Upload || entry.Stage == MaintenancePreparationStage.RemoteHash ? "正在上传并校验菜单…" :
                entry.Stage == MaintenancePreparationStage.Commit || entry.Stage == MaintenancePreparationStage.Download || entry.Stage == MaintenancePreparationStage.Validate ? "正在下载并校验菜单…" : "正在准备菜单会话…";
            SetState(text, UiTheme.SecondaryBlue);
        }
        private void RefreshStatus()
        {
            if (IsDisposed || Disposing) return;
            bool online; try { online = connected(); } catch (Exception) { online = false; }
            connectionStatus.Text = online ? computerProxyPath ? "SSH 已连接 · 电脑代理" : "SSH 已连接" : "SSH 已断开";
            connectionStatus.ForeColor = online ? UiTheme.ConnectionSuccess : UiTheme.Error;
            if (!busy || closeRequested || Result != null || terminal == null) return;
            var state = terminal.Snapshot;
            switch (state.Status)
            {
                case MaintenanceMenuRunStatus.Running: SetState("菜单运行中" + (state.ProcessId.HasValue ? " · PID " + state.ProcessId : ""), UiTheme.ConnectionSuccess); break;
                case MaintenanceMenuRunStatus.AwaitingClose: SetState("菜单已返回，正在接收剩余输出…", UiTheme.SecondaryBlue); break;
                case MaintenanceMenuRunStatus.ExitedNormally:
                case MaintenanceMenuRunStatus.ExitedWithError: SetState("正在确认退出状态与清理…", UiTheme.SecondaryBlue); break;
                case MaintenanceMenuRunStatus.Unknown: SetState("菜单状态待确认…", UiTheme.Warning); break;
            }
        }
        private void DisplayResult()
        {
            if (Result != null && Result.NetworkPath != MaintenanceNetworkPath.None) computerProxyPath = Result.NetworkPath == MaintenanceNetworkPath.ComputerProxy;
            var preparation = Result == null ? null : Result.Preparation;
            if (preparation == null) { SetState("菜单状态无法确认", UiTheme.Warning); SetDetail("未收到完整运行结果，请返回首页检查连接。"); return; }
            string message = preparation.Error;
            switch (preparation.Status)
            {
                case MaintenancePreparationStatus.Completed: SetState("菜单已正常退出", UiTheme.ConnectionSuccess); message = "退出码 0。可返回首页查看结果。"; break;
                case MaintenancePreparationStatus.ScriptFailed: SetState("菜单异常退出 · 退出码 " + preparation.ExitCode, UiTheme.Error); break;
                case MaintenancePreparationStatus.FinalStateUnknown: SetState("菜单最终状态未知", UiTheme.Warning); break;
                case MaintenancePreparationStatus.Cancelled: SetState("菜单准备已取消", UiTheme.Warning); break;
                default: SetState("菜单未启动", UiTheme.Error); break;
            }
            if (!string.IsNullOrEmpty(preparation.CleanupWarning)) message = (message ?? "") + "\n" + preparation.CleanupWarning;
            SetDetail(message ?? "可返回首页检查连接后再打开。");
        }
        private void SetState(string text, Color color) { operationStatus.Text = text; operationStatus.ForeColor = color; }
        private void SetDetail(string text) { detail.Text = text; tips.SetToolTip(detail, text); }
        private void RequestReturn()
        {
            if (IsDisposed || Disposing || closeRequested) return;
            closeRequested = true; returnHome.Enabled = false; returnHome.Text = "正在返回…";
            SetState("正在关闭菜单会话…", UiTheme.SecondaryBlue); SetDetail("正在结束本次菜单会话并确认状态…");
            lifetime.Cancel();
            if (terminal != null) terminal.CloseAsync().ContinueWith(task => { var observed = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            if (!busy) FinishReturn();
        }
        private void FinishReturn()
        {
            allowClose = true;
            // Avoid recursively closing inside OnShown or the original FormClosing event.
            if (IsHandleCreated) { try { BeginInvoke(new Action(Close)); } catch (InvalidOperationException) { } }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel || allowClose) return;
            if (!busy) { closeRequested = true; lifetime.Cancel(); return; }
            e.Cancel = true; RequestReturn();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && !resourcesDisposed) {
                resourcesDisposed = true;
                lifetime.Cancel(); ownerRegistration.Dispose(); statusTimer.Dispose(); tips.Dispose();
                // Disposing the view also unblocks any output producer waiting on its bounded queue.
                terminalView.Dispose(); lifetime.Dispose();
            }
            base.Dispose(disposing);
            if (disposing) resources.Dispose();
        }
    }
}
