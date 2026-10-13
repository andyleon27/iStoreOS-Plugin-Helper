using System;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Controls;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class AdvancedUiTests
    {
        public static void Run(Action<string, Action> test)
        {
            test("V0.3.2.5 flat navigation preserves click arrow and programmatic selection", () =>
            {
                using (var form = new MainForm())
                {
                    Open(form); var tabs = (FlatPageTabs)Control(form, "advancedTabs"); Equal(tabs.ClientRectangle, tabs.DisplayRectangle);
                    True(((SegmentButton)Control(form, "advancedNav0")).Selected);
                    ((Button)Control(form, "advancedNav3")).PerformClick(); Equal(3, tabs.SelectedIndex);
                    Raise(Control(form, "advancedNav3"), "OnKeyDown", new KeyEventArgs(Keys.Right)); Equal(4, tabs.SelectedIndex);
                    tabs.SelectedIndex = 1; True(((SegmentButton)Control(form, "advancedNav1")).Selected);
                    True(Control(form, "advancedNav1").Font.Bold); Equal(false, Control(form, "advancedNav0").Font.Bold);
                    UiTests.Field<AdvancedInfoForm>(form, "advancedInfoForm").SelectLogs(); Equal(4, tabs.SelectedIndex);
                    True(Control(form, "saveLogButton").Enabled); Equal(new Size(760, 500), form.ClientSize);
                }
            });
            test("V0.3.2.5 empty pages update from existing device and source values", () =>
            {
                using (var form = new MainForm())
                {
                    Open(form); True(Control(form, "systemEmptyState").Visible);
                    UiTests.Set(form, "router", new RouterEnvironment { IsConnected = true, Version = "24.10.9", SdkSeries = "24.10", Architecture = "aarch64_a53", PackageManager = PackageManagerKind.OPKG });
                    UiTests.Invoke(form, "DisplayEnvironment"); Equal(false, Control(form, "systemEmptyState").Visible); Equal("24.10.9", Control(form, "versionLabel").Text);
                    var tabs = (TabControl)Control(form, "advancedTabs"); tabs.SelectedIndex = 1;
                    var result = new SoftwareEnvironmentResult { CurrentSource = new SoftwareSourceInfo { Identity = "Fixture", Url = "https://fixture.test/source" }, OfficialHealth = new RepositoryHealthResult() };
                    result.OfficialHealth.Probes.Add(new RepositoryProbeResult { Success = true }); UiTests.Invoke(form, "DisplaySoftwareResult", result);
                    Equal(false, Control(form, "sourceEmptyState").Visible); True(Control(form, "sourceDetailsBox").Text.Contains("fixture.test/source"));
                    tabs.SelectedIndex = 2; True(Control(form, "installationEmptyState").Visible);
                    tabs.SelectedIndex = 3; True(Control(form, "diffEmptyState").Visible);
                }
            });
            test("V0.3.2.5 diff grid preserves added updated removed and attempt records", () =>
            {
                using (var form = new MainForm())
                {
                    var result = new InstallationWorkflowResult { State = InstallationState.Success, Installation = new PluginInstallResult { Diff = new PackageDiff() } };
                    var install = result.Installation;
                    install.Diff.Added.Add(new PackageChange { Name = "added-fixture", AfterVersion = "2.0" });
                    install.Diff.Updated.Add(new PackageChange { Name = "updated-fixture", BeforeVersion = "1.0", AfterVersion = "2.1" });
                    install.Diff.Removed.Add(new PackageChange { Name = "removed-fixture", BeforeVersion = "0.9" });
                    result.Attempts.Add(new InstallationAttempt { SourceKey = "fixture-source", Result = install });
                    UiTests.Invoke(form, "DisplayWorkflowResult", result); Open(form); ((TabControl)Control(form, "advancedTabs")).SelectedIndex = 3;
                    var grid = (DataGridView)Control(form, "packageDiffGrid"); Equal(3, grid.Rows.Count); True(grid.Visible); Equal(false, Control(form, "diffEmptyState").Visible);
                    Equal("1", grid.Rows[0].Cells[0].Value); Equal("added-fixture", grid.Rows[0].Cells[2].Value);
                    Equal("1.0", grid.Rows[1].Cells[3].Value); Equal("2.1", grid.Rows[1].Cells[4].Value); Equal("0.9", grid.Rows[2].Cells[3].Value);
                    True(Control(form, "packageDiffBox").Text.Contains("Attempt 1，Source=fixture-source")); Equal(1, install.Diff.Added.Count); Equal(1, install.Diff.Updated.Count); Equal(1, install.Diff.Removed.Count);
                    Equal(false, grid.EnableHeadersVisualStyles); True(grid.ReadOnly);
                }
            });
            test("V0.3.2.5 stage presentation does not invent completed stages", () =>
            {
                using (var resources = new UiResources()) using (var stages = new InstallationStageView(resources))
                {
                    stages.UpdateResult(null, true, "正在上传插件… 20%"); Equal("进行中", stages.Controls.Find("advancedStageStatus2", true)[0].Text);
                    Equal("等待", stages.Controls.Find("advancedStageStatus0", true)[0].Text);
                    var result = new InstallationWorkflowResult { State = InstallationState.ActionRequiredFailure, SourceChanged = true, RestoreAttempted = true,
                        Installation = new PluginInstallResult { Status = InstallStatus.FailedIntegrity, CleanupSucceeded = true } };
                    stages.UpdateResult(result, false, ""); Equal("未通过", stages.Controls.Find("advancedStageStatus3", true)[0].Text);
                    Equal("等待", stages.Controls.Find("advancedStageStatus4", true)[0].Text); Equal("待确认 · 查看结果", stages.Controls.Find("advancedStageStatus6", true)[0].Text);
                    Equal(false, result.SourceRestored); Equal(InstallStatus.FailedIntegrity, result.Installation.Status);
                }
            });
            test("V0.3.2.5 wrapped detail scrolling preserves text and selection", () =>
            {
                using (var host = new Form()) using (var detail = new AdaptiveDetailTextBox { Size = new Size(240, 80) })
                {
                    host.Controls.Add(detail); host.Show(); detail.Text = "short-fixture"; Equal(ScrollBars.None, detail.ScrollBars);
                    detail.Text = new string('X', 4000); Equal(ScrollBars.Vertical, detail.ScrollBars); detail.Select(5, 12);
                    detail.Size = new Size(480, 160); Equal(5, detail.SelectionStart); Equal(12, detail.SelectionLength); Equal(new string('X', 4000), detail.Text);
                    detail.Text = "short-fixture"; Equal(ScrollBars.None, detail.ScrollBars); True(detail.WordWrap); host.Close();
                }
            });
            test("V0.3.2.5 dark log retains original readonly text selection and export", () =>
            {
                using (var form = new MainForm())
                {
                    Open(form); var tabs = (TabControl)Control(form, "advancedTabs"); tabs.SelectedIndex = 4;
                    var log = (RichTextBox)Control(form, "logBox"); Equal(UiTheme.LogBackground, log.BackColor); Equal(UiTheme.LogForeground, log.ForeColor); True(log.ReadOnly);
                    UiTests.Invoke(form, "AppendInstallLog", "dark-log-fixture"); int start = log.Text.IndexOf("dark-log-fixture"); log.Select(start, 16); string selected = log.SelectedText;
                    tabs.SelectedIndex = 0; tabs.SelectedIndex = 4; Equal(selected, log.SelectedText);
                    string export = Snapshot(form); True(export.Contains("dark-log-fixture")); True(Control(form, "saveLogButton").Enabled);
                    string longLine = "https://fixture.test/" + new string('x', 1600) + "?hash=" + new string('a', 64);
                    UiTests.Invoke(form, "AppendInstallLog", longLine); start = log.Text.IndexOf(longLine); log.Select(start, longLine.Length);
                    True(log.GetLineFromCharIndex(start + longLine.Length - 1) > log.GetLineFromCharIndex(start)); Equal(longLine, log.SelectedText);
                    string raw = log.Text; UiTests.Field<AdvancedInfoForm>(form, "advancedInfoForm").ClientSize = new Size(640, 480); Application.DoEvents();
                    Equal(raw, log.Text); Equal(longLine, log.SelectedText); True(Snapshot(form).Contains(longLine));
                    log.Clear(); True(Control(form, "logEmptyState").Visible); True(Snapshot(form).Contains("dark-log-fixture"));
                }
            });
            foreach (float factor in new[] { 1F, 1.25F })
            {
                float scale = factor;
                test("V0.3.2.5 advanced resize and scaled geometry " + scale, () =>
                {
                    using (var form = new MainForm())
                    {
                        Open(form); var view = UiTests.Field<AdvancedInfoForm>(form, "advancedInfoForm");
                        foreach (var size in new[] { new Size(1024, 720), new Size(624, 441), new Size(800, 600) })
                        {
                            view.ClientSize = size; Application.DoEvents(); CheckBounds(view);
                        }
                        view.WindowState = FormWindowState.Maximized; Application.DoEvents(); CheckBounds(view); view.WindowState = FormWindowState.Normal; view.ClientSize = new Size(800, 600);
                        if (scale != 1F) view.Scale(new SizeF(scale, scale)); Application.DoEvents(); CheckBounds(view);
                        var tabs = (TabControl)Control(form, "advancedTabs");
                        for (int i = 0; i < 5; i++)
                        {
                            tabs.SelectedIndex = i; Application.DoEvents(); CheckBounds(view); True(((SegmentButton)Control(form, "advancedNav" + i)).Selected);
                            Equal(9.5F * scale, Control(form, "advancedNav" + i).Font.Size);
                        }
                    }
                });
            }
        }
        private static void CheckBounds(Form view)
        {
            foreach (string name in new[] { "advancedNavigation", "advancedTabs", "advancedToolbar", "saveLogButton", "copyLogButton", "cancelInstallButton", "advancedNav4" })
            {
                var control = view.Controls.Find(name, true)[0]; var rectangle = view.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                if (!view.ClientRectangle.Contains(rectangle) || !control.Parent.ClientRectangle.Contains(control.Bounds)) throw new Exception(name + " outside layout: " + rectangle);
            }
        }
        private static Control Control(MainForm form, string name) { return UiTests.Control(form, name); }
        private static string Snapshot(MainForm form) { return (string)typeof(MainForm).GetMethod("GetAllLogText", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null); }
        private static void Open(MainForm form) { form.Show(); UiTests.Invoke(form, "ShowAdvancedInformation", false); Application.DoEvents(); }
        private static void Raise(object control, string name, object args) { control.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new[] { args }); }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    }
}
