using System;
using System.Drawing;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper
{
    public sealed partial class MainForm
    {
        private LinkLabel maintenanceEntry;
        private bool maintenanceWindowOpen;
        private bool maintenanceNetworkChecking;
        private void AttachMaintenanceEntry(Panel panel, LinkLabel advanced)
        {
            var entry = new LinkLabel { Name = "pluginMaintenanceLink", Text = "插件维护 ›", Width = 105, TabStop = true,
                Font = uiResources.Font(9F, FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft,
                LinkColor = UiTheme.SecondaryText, ActiveLinkColor = UiTheme.LinkHover, VisitedLinkColor = UiTheme.SecondaryText,
                LinkBehavior = LinkBehavior.HoverUnderline, Enabled = false, DisabledLinkColor = UiTheme.DisabledText };
            maintenanceEntry = entry;
            panel.Controls.Add(entry);
            panel.Layout += (sender, args) => entry.SetBounds(advanced.Right + 10, advanced.Top, entry.Width, advanced.Height);
            entry.LinkClicked += (sender, args) => OpenMaintenanceTerminal();
            Shown += (sender, args) => {
                connectionTimer.Tick += (s, e) => RefreshMaintenanceEntry();
                RefreshMaintenanceEntry();
            };
            RefreshMaintenanceEntry();
        }
        private MaintenanceMenuAvailability MaintenanceEntryAvailability()
        {
            if (IsDisposed || Disposing || closing.IsCancellationRequested)
                return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.Closed, "窗口正在关闭。");
            if (maintenanceWindowOpen || isConnecting || isDisconnecting || isSoftwareBusy || isInstallBusy)
                return new MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus.Busy, "请等待当前安装、检测或插件维护任务完成。");
            return ssh.GetMaintenanceLaunchAvailability(router);
        }
        private void RefreshMaintenanceEntry()
        {
            if (maintenanceEntry == null || maintenanceEntry.IsDisposed) return;
            if (!maintenanceWindowOpen && !isConnecting && !isDisconnecting && !isSoftwareBusy && !isInstallBusy &&
                !closing.IsCancellationRequested && ssh.GetMaintenanceMenuAvailability(router).CanOpen &&
                !maintenanceNetworkChecking && ssh.GetMaintenanceNetworkSnapshot().NeedsRefresh) DetectMaintenanceNetwork();
            var availability = MaintenanceEntryAvailability();
            bool proxy = ssh.GetMaintenanceNetworkSnapshot().Path == MaintenanceNetworkPath.ComputerProxy;
            maintenanceEntry.Text = proxy ? "插件维护 · 电脑代理 ›" : "插件维护 ›";
            maintenanceEntry.Width = proxy ? 165 : 105;
            maintenanceEntry.Enabled = availability.CanOpen;
            uiToolTip.SetToolTip(maintenanceEntry, availability.Message);
            RefreshConnectionButton();
        }
        private async void DetectMaintenanceNetwork()
        {
            maintenanceNetworkChecking = true;
            RefreshInstallButton(); softwareButton.Enabled = false;
            try {
                var result = await ssh.CheckMaintenanceNetworkAsync(router, closing.Token);
                if (!closing.IsCancellationRequested) AppendLog("插件维护网络：" + result.Code + "；Raw HTTP=" +
                    (result.RouterRaw == null ? "unknown" : result.RouterRaw.HttpStatus.ToString()) + "；API HTTP=" +
                    (result.RouterApi == null ? "unknown" : result.RouterApi.HttpStatus.ToString()) + "（仅诊断）");
            } catch (OperationCanceledException) { }
            catch (Exception) { }
            finally {
                maintenanceNetworkChecking = false;
                if (!IsDisposed && !Disposing && !closing.IsCancellationRequested) {
                    RefreshInstallButton(); RefreshConnectionButton();
                    if (!isSoftwareBusy && !isInstallBusy && !isConnecting && !isDisconnecting)
                        softwareButton.Enabled = ssh.CanChangeConnection && router != null && router.IsConnected && router.PackageManager != PackageManagerKind.Unknown;
                }
            }
        }
        private void OpenMaintenanceTerminal()
        {
            // Enabled is just a display snapshot. Recheck the live connection and task gates on click.
            var availability = MaintenanceEntryAvailability();
            if (!availability.CanOpen) { RefreshMaintenanceEntry(); return; }
            var expected = router;
            maintenanceWindowOpen = true;
            RefreshConnectionButton();
            RefreshMaintenanceEntry();
            try
            {
                PluginMaintenanceTerminalForm form = null;
                using (form = new PluginMaintenanceTerminalForm(
                    (ready, progress, token, columns, rows) => ssh.RunMaintenanceMenuWithNetworkAsync(expected, ready, progress, token, columns, rows, form.GetTerminalSize),
                    () => ssh.GetMaintenanceLaunchAvailability(expected), () => ssh.IsConnected, Icon, closing.Token))
                {
                    form.ShowDialog(this);
                    var result = form.Result == null ? null : form.Result.Preparation;
                    if (result != null) {
                        AppendLog("插件维护：" + result.Status + (result.ExitCode.HasValue ? "；退出码 " + result.ExitCode : ""));
                        if (!string.IsNullOrEmpty(result.Error)) AppendLog(result.Error);
                        if (!string.IsNullOrEmpty(result.CleanupWarning)) AppendLog(result.CleanupWarning);
                    }
                }
            }
            finally { maintenanceWindowOpen = false; RefreshMaintenanceEntry(); RefreshConnectionButton(); }
        }
    }
}
