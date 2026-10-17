using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class CompactUiTests
    {
        public static void Run(Action<string, Action> test)
        {
            test("V0.3.2.1 main window is compact fixed and DPI aware", () =>
            {
                using (var form = new MainForm())
                {
                    Equal(new Size(760, 500), form.ClientSize); Equal(FormBorderStyle.FixedSingle, form.FormBorderStyle);
                    Equal(false, form.MaximizeBox); Equal(true, form.MinimizeBox); Equal(AutoScaleMode.Dpi, form.AutoScaleMode);
                    Equal("V1.0.1", Control(form, "appVersionLabel").Text);
                    Equal("iStoreOS 插件安装助手 V1.0.1", form.Text);
                    Equal(new Version(1, 0, 0, 0), typeof(MainForm).Assembly.GetName().Version);
                    var info = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(MainForm).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                    Equal("1.0.1", info.InformationalVersion);
                    var fileVersion = (System.Reflection.AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(typeof(MainForm).Assembly, typeof(System.Reflection.AssemblyFileVersionAttribute));
                    Equal("1.0.1.0", fileVersion.Version);
                }
            });
            test("V0.3.2.1 advanced window opens modeless with five sections", () =>
            {
                using (var form = new MainForm())
                {
                    form.Show(); Open(form); var advanced = Advanced(form);
                    Equal(true, advanced.Visible); Equal(form, advanced.Owner); Equal(false, advanced.Modal);
                    Equal(new Size(800, 600), advanced.ClientSize); Equal(true, advanced.MaximizeBox); Equal(FormBorderStyle.Sizable, advanced.FormBorderStyle);
                    Equal(5, ((TabControl)Control(form, "advancedTabs")).TabCount); Equal(true, form.Enabled);
                    form.Close();
                }
            });
            test("V0.3.2.1 repeated entry activates one advanced window", () =>
            {
                using (var form = new MainForm()) { Open(form); var view = Advanced(form); Open(form); Equal(view, Advanced(form)); Equal(true, view.Visible); }
            });
            test("V0.3.2.1 advanced close hides view without cancelling preparation", () =>
            {
                using (var token = new CancellationTokenSource())
                using (var form = new MainForm())
                {
                    UiTests.Set(form, "installCancellation", token); UiTests.Set(form, "isInstallBusy", true); Open(form); var view = Advanced(form); view.Close();
                    Equal(false, view.IsDisposed); Equal(false, view.Visible); Equal(false, token.IsCancellationRequested); Equal(true, UiTests.Field<bool>(form, "isInstallBusy"));
                    UiTests.Set(form, "isInstallBusy", false); UiTests.Set(form, "installCancellation", null);
                }
            });
            test("V0.3.2.1 advanced close during dispatch cannot affect installer gate", () =>
            {
                using (var form = new MainForm())
                {
                    UiTests.Set(form, "isInstallBusy", true); UiTests.Set(form, "installerDispatched", true); Open(form); Advanced(form).Close(); UiTests.Invoke(form, "RefreshInstallButton");
                    Equal(false, Control(form, "cancelInstallButton").Enabled); Equal(false, Control(form, "installButton").Enabled); Equal("正在安装…", Control(form, "installButton").Text);
                    UiTests.Set(form, "isInstallBusy", false);
                }
            });
            test("V0.3.2.1 main visual tree contains no advanced panel logs or save action", () =>
            {
                using (var form = new MainForm())
                {
                    foreach (string name in new[] { "logPanel", "logBox", "saveLogButton", "softwareButton", "installResultBox", "filePathBox" }) Equal(0, form.Controls.Find(name, true).Length);
                    Equal(false, ((Panel)Control(form, "scrollPanel")).AutoScroll);
                }
            });
            test("V0.3.2.1 open close and activate cannot resize main window", () =>
            {
                using (var form = new MainForm())
                {
                    Size size = form.Size; Open(form); Equal(size, form.Size); Advanced(form).Size = new Size(920, 700); Equal(size, form.Size);
                    Advanced(form).Close(); Equal(size, form.Size); Open(form); Equal(size, form.Size);
                }
            });
            test("V0.3.2.1 connect select busy success and failure retain client size", () =>
            {
                using (var form = new MainForm())
                {
                    Size size = form.ClientSize; UiTests.Set(form, "router", Router()); UiTests.Invoke(form, "DisplayEnvironment"); Equal(size, form.ClientSize);
                    UiTests.Set(form, "plugin", Plugin()); UiTests.Invoke(form, "RefreshCompatibility"); Equal(size, form.ClientSize);
                    UiTests.Set(form, "isInstallBusy", true); UiTests.Invoke(form, "RefreshInstallButton"); Equal(size, form.ClientSize); UiTests.Set(form, "isInstallBusy", false);
                    UiTests.Invoke(form, "DisplayWorkflowResult", Success()); Equal(size, form.ClientSize);
                    UiTests.Invoke(form, "DisplayWorkflowResult", new InstallationWorkflowResult { State = InstallationState.RetryableFailure, Failure = InstallFailureClassifier.Failure(InstallationFailureKind.MirrorsExhausted) }); Equal(size, form.ClientSize);
                }
            });
            test("V0.3.2.1 hidden advanced view continues receiving all log streams", () =>
            {
                using (var form = new MainForm())
                {
                    UiTests.Invoke(form, "AppendLog", "connection sentinel"); UiTests.Invoke(form, "AppendSoftwareLog", "source sentinel"); UiTests.Invoke(form, "AppendInstallLog", "installer sentinel");
                    Equal(false, Advanced(form).Visible); Open(form); var text = Control(form, "logBox").Text;
                    foreach (string line in new[] { "connection sentinel", "source sentinel", "installer sentinel" }) True(text.Contains(line));
                    Advanced(form).Close(); UiTests.Invoke(form, "AppendInstallLog", "after close sentinel"); Open(form); True(Control(form, "logBox").Text.Contains("after close sentinel"));
                }
            });
            test("V0.3.2.1 full export survives advanced reopen and visual buffer trimming", () =>
            {
                using (var form = new MainForm())
                {
                    UiTests.Invoke(form, "AppendLog", "first-session-sentinel"); UiTests.Invoke(form, "AppendInstallLog", new string('Z', 200010));
                    Open(form); Advanced(form).Close(); UiTests.Invoke(form, "AppendInstallLog", "last-session-sentinel"); Open(form);
                    string export = Snapshot(form); True(export.Contains("first-session-sentinel")); True(export.Contains("last-session-sentinel")); True(export.Contains(new string('Z', 200010)));
                    Equal(false, Control(form, "logBox").Text.Contains("first-session-sentinel")); True(Control(form, "saveLogButton").Enabled);
                }
            });
            test("V0.3.2.1 advanced save exports complete UTF8 BOM session", () =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    using (var form = new MainForm())
                    {
                        Open(form); UiTests.Invoke(form, "AppendSoftwareLog", "HTTP 206 中文输出"); UiTests.Invoke(form, "AppendInstallLog", "SHA256 Makeself 验证输出"); string text = Snapshot(form);
                        typeof(MainForm).GetMethod("WriteLogFile", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, new object[] { path, text });
                        Equal(text, File.ReadAllText(path, Encoding.UTF8)); Equal(0xEF, (int)File.ReadAllBytes(path)[0]); True(text.Contains("V1.0.1") && text.Contains("1.0.1")); True(!text.Contains("RC1") && !text.Contains("1.0.0-rc1"));
                    }
                }
                finally { File.Delete(path); }
            });
            test("V0.3.2.1 sole visible save action stays available across tabs", () =>
            {
                using (var form = new MainForm())
                {
                    form.Show(); Open(form); var tabs = (TabControl)Control(form, "advancedTabs");
                    for (int i = 0; i < tabs.TabCount; i++)
                    { tabs.SelectedIndex = i; Application.DoEvents(); True(Control(form, "saveLogButton").Visible); True(Control(form, "saveLogButton").Enabled); Equal(false, Control(form, "saveSoftwareLogButton").Visible); Equal(false, Control(form, "saveInstallLogButton").Visible); }
                    form.Close();
                }
            });
            test("V0.3.2.1 failed result exposes details link opening logs", () =>
            {
                using (var form = new MainForm())
                {
                    form.Show(); UiTests.Invoke(form, "DisplayWorkflowResult", new InstallationWorkflowResult { State = InstallationState.ActionRequiredFailure, Failure = InstallFailureClassifier.Failure(InstallationFailureKind.Integrity) });
                    True(Control(form, "detailsLink").Visible); UiTests.Invoke(form, "ShowAdvancedInformation", true); Equal(4, ((TabControl)Control(form, "advancedTabs")).SelectedIndex); form.Close();
                }
            });
            test("V0.3.2.1 connected summary uses actual device and leaves QuickStart advanced", () =>
            {
                using (var form = new MainForm())
                {
                    var router = Router(); router.Version = "24.10.9"; router.SdkSeries = "24.10"; router.Architecture = "aarch64_a53"; router.PackageManager = PackageManagerKind.OPKG;
                    UiTests.Set(form, "router", router); UiTests.Invoke(form, "DisplayEnvironment"); string text = Control(form, "connectionLabel").Text;
                    foreach (string value in new[] { "24.10.9", "24.10", "aarch64_a53", "OPKG" }) True(text.Contains(value)); Equal(false, text.Contains("QuickStart")); Equal(0, form.Controls.Find("quickStartLabel", true).Length);
                }
            });
            test("V0.3.2.1 empty plugin summary has no duplicated prompt", () =>
            {
                using (var form = new MainForm()) { UiTests.Invoke(form, "RefreshCompatibility"); Equal("尚未选择插件", Control(form, "pluginNameLabel").Text); Equal("", Control(form, "compatibilityLabel").Text); }
            });
            test("V0.3.2.1 selected file path stays advanced and click area accepts drops", () =>
            {
                string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); string path = Path.Combine(directory, "OpenWrt-nikki_1.2_x86_64_all_sdk_25.12.run");
                try
                {
                    File.WriteAllText(path, "metadata fixture only");
                    using (var form = new MainForm())
                    {
                        UiTests.Set(form, "router", Router()); UiTests.Invoke(form, "SelectPlugin", path); True(Control(form, "pluginNameLabel").Text.Contains("OpenWrt-nikki")); True(Control(form, "compatibilityLabel").Text.Contains("当前设备兼容"));
                        Equal(path, Control(form, "filePathBox").Text); Equal(0, form.Controls.Find("filePathPreviewLabel", true).Length); True(Control(form, "dropArea").AllowDrop); True(Control(form, "dropLabel").AllowDrop);
                    }
                }
                finally { File.Delete(path); Directory.Delete(directory); }
            });
            test("V0.3.2.1 incompatible package keeps install disabled and red summary", () =>
            {
                using (var form = new MainForm())
                {
                    UiTests.Set(form, "router", Router()); UiTests.Set(form, "plugin", new PluginPackageAnalyzer().AnalyzeFileName("Nikki_1_aarch64_a53_all_sdk_25.12.run")); UiTests.Invoke(form, "RefreshCompatibility");
                    Equal(false, Control(form, "installButton").Enabled); True(Control(form, "compatibilityLabel").Text.Contains("不兼容")); True(Control(form, "compatibilityLabel").ForeColor.R > Control(form, "compatibilityLabel").ForeColor.G);
                }
            });
            test("V0.3.2.1 verified success is concise with engineering results advanced", () =>
            {
                using (var form = new MainForm())
                {
                    var result = Success(); UiTests.Invoke(form, "DisplayWorkflowResult", result); True(Control(form, "installHeadlineLabel").Text.Contains("OpenWrt-nikki 安装成功")); True(Control(form, "installStepLabel").Text.Contains("luci-app-nikki"));
                    Equal(false, Control(form, "installStepLabel").Text.Contains("\n")); True(Control(form, "installResultBox").Text.Contains(new string('a', 64))); True(Control(form, "installResultBox").Text.Contains("Makeself：2.5"));
                    True(Control(form, "installResultBox").Text.Contains("原源已恢复：True")); True(Control(form, "packageDiffBox").Text.Contains("dependency-fixture")); Equal(InstallationState.Success, result.State); True(result.Installation.Verification.Verified);
                }
            });
            test("V0.3.2.1 source detail displays dynamic URLs HTTP and update output", () =>
            {
                using (var form = new MainForm())
                {
                    var result = new SoftwareEnvironmentResult { CurrentSource = new SoftwareSourceInfo { Identity = "Fixture", Url = "https://fixture.test/router-source" }, OfficialHealth = new RepositoryHealthResult(), AdditionalHealth = new RepositoryHealthResult() };
                    result.AdditionalHealth.Probes.Add(new RepositoryProbeResult { HttpStatus = 503, ExitCode = 1, Feed = new RepositoryFeed { Name = "custom-fixture", Kind = RepositoryKind.Custom, IndexUrl = "https://custom.test/index" } });
                    result.Updates.Add(new PackageUpdateResult { ExitCode = 42, Stdout = "stdout-fixture", Stderr = "stderr-fixture" }); UiTests.Invoke(form, "DisplaySoftwareResult", result);
                    string text = Control(form, "sourceDetailsBox").Text; foreach (string value in new[] { "fixture.test", "custom.test", "HTTP=503", "exit=42", "stdout-fixture", "stderr-fixture" }) True(text.Contains(value));
                    UiTests.Invoke(form, "ResetSoftwareState"); Equal(false, Control(form, "sourceDetailsBox").Text.Contains("fixture.test"));
                }
            });
            test("V0.3.2.1 main dispose also disposes independent view", () =>
            {
                var form = new MainForm(); var view = Advanced(form); Open(form); form.Dispose(); Equal(true, view.IsDisposed);
            });
            foreach (float factor in new[] { 1F, 1.5F, 2F })
            {
                float scale = factor;
                test("V0.3.2.1 scaled compact geometry " + scale, () =>
                {
                    using (var form = new MainForm())
                    using (var layoutSurface = new Panel())
                    {
                        Control surface = form;
                        if (scale == 2F)
                        {
                            // A 1520x1000 layout exceeds this virtual desktop's native window
                            // limit. Test the same visual tree on an unconstrained layout surface.
                            layoutSurface.Size = form.ClientSize;
                            layoutSurface.Controls.Add(form.Controls[0]);
                            layoutSurface.Scale(new SizeF(scale, scale));
                            layoutSurface.PerformLayout(); surface = layoutSurface;
                        }
                        else { form.Show(); if (scale != 1F) form.Scale(new SizeF(scale, scale)); }
                        Application.DoEvents();
                        foreach (string name in new[] { "hostBox", "usernameBox", "passwordBox", "passwordToggleButton", "connectButton", "chooseButton", "installButton", "toggleLogButton", "installStepLabel" })
                        {
                            var control = surface.Controls.Find(name, true)[0];
                            if (!control.Parent.ClientRectangle.Contains(control.Bounds)) throw new Exception(name + " outside parent: " + control.Bounds + " parent=" + control.Parent.ClientRectangle);
                            var rectangle = surface.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                            if (!surface.ClientRectangle.Contains(rectangle)) throw new Exception(name + " outside layout: " + rectangle + " surface=" + surface.ClientRectangle);
                        }
                        var button = surface.Controls.Find("installButton", true)[0]; Equal((int)(200 * scale), button.Width); Equal((int)(40 * scale), button.Height);
                        True(surface.Controls.Find("installCard", true)[0].Height <= 65 * scale); True(surface.Controls.Find("dropArea", true)[0].Height <= 80 * scale); if (scale != 2F) form.Close();
                    }
                });
            }
        }
        private static Control Control(MainForm form, string name) { return UiTests.Control(form, name); }
        private static AdvancedInfoForm Advanced(MainForm form) { return UiTests.Field<AdvancedInfoForm>(form, "advancedInfoForm"); }
        private static void Open(MainForm form) { UiTests.Invoke(form, "ToggleLogButton_Click", null, EventArgs.Empty); Application.DoEvents(); }
        private static string Snapshot(MainForm form) { return (string)typeof(MainForm).GetMethod("GetAllLogText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(form, null); }
        private static RouterEnvironment Router() { return new RouterEnvironment { IsConnected = true, Version = "25.12.5", SdkSeries = "25.12", Architecture = "x86_64", PackageManager = PackageManagerKind.APK }; }
        private static PluginPackageInfo Plugin() { return new PluginPackageAnalyzer().AnalyzeFileName("OpenWrt-nikki_1.2_x86_64_all_sdk_25.12.run"); }
        private static InstallationWorkflowResult Success()
        {
            var plugin = Plugin(); var install = new PluginInstallResult { Status = InstallStatus.SuccessVerified, Plugin = plugin, CleanupSucceeded = true, InstallerStarted = true, InstallerEnded = true, InstallerExitCode = 0,
                Sha256 = new string('a', 64), Archive = new RemotePackageInfo { MakeselfVersion = "2.5", Architecture = "x86_64", SdkSeries = "25.12" }, Verification = new PluginVerificationResult { Verified = true, PackageName = "luci-app-nikki", Version = "1.2" }, Diff = new PackageDiff() };
            install.Diff.Added.Add(new PackageChange { Name = "dependency-fixture", AfterVersion = "2.0" });
            return new InstallationWorkflowResult { State = InstallationState.Success, Installation = install, Router = Router(), SourceChanged = true, RestoreAttempted = true, SourceRestored = true };
        }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    }
}
