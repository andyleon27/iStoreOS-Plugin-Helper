using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class Program
    {
        private static int passed;
        private static int failed;
        private static readonly PluginPackageAnalyzer Analyzer = new PluginPackageAnalyzer();
        private static readonly CompatibilityService Compatibility = new CompatibilityService();

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            if (args.Length == 2 && args[0] == "--ui-smoke")
            {
                Run("WinForms startup and layout", () => UiSmoke(args[1]));
                return failed == 0 ? 0 : 1;
            }
            Run("PassWall filename", () => AssertPackage("PassWall_26.9.16_x86_64_all_sdk_25.12.run", "PassWall", "26.9.16", "x86_64", "all"));
            Run("PassWall2 name precedence", () => AssertPackage("PassWall2_26.10.1_x86_64_all_sdk_25.12.run", "PassWall2", "26.10.1", "x86_64", "all"));
            Run("Nikki filename", () => AssertPackage("OpenWrt-nikki_1.26.1_x86_64_all_sdk_25.12.run", "OpenWrt-nikki", "1.26.1", "x86_64", "all"));
            Run("Momo filename", () => AssertPackage("OpenWrt-momo_1.2.1_x86_64_all_sdk_25.12.run", "OpenWrt-momo", "1.2.1", "x86_64", "all"));
            Run("Homeproxy commit version", () => AssertPackage("homeproxy_26.206.30835-4b2617a_x86_64_all_sdk_25.12.run", "homeproxy", "26.206.30835-4b2617a", "x86_64", "all"));
            Run("OpenClash plus separator", () => AssertPackage("OpenClash_0.47.156+x86_64_core_sdk_25.12.run", "OpenClash", "0.47.156", "x86_64", "core"));
            Run("Daede filename", () => AssertPackage("daede_1.15-r6_x86_64_all_sdk_25.12.run", "daede", "1.15-r6", "x86_64", "all"));
            Run("Openwrt-daede alias", () => AssertPackage("openwrt-daede_1.15-r6_x86_64_all_sdk_25.12.run", "openwrt-daede", "1.15-r6", "x86_64", "all"));
            Run("SSR filename", () => AssertPackage("SSR-Plus_196-r7_x86_64_all_sdk_25.12.run", "SSR-Plus", "196-r7", "x86_64", "all"));
            Run("Unknown plugin remains compatible", () =>
            {
                var plugin = Analyzer.AnalyzeFileName("SomeNewPlugin_1.0_x86_64_all_sdk_25.12.run");
                Equal(false, plugin.IsRecognizedPlugin); Equal("未知插件 / 通用 Are-u-ok 包", plugin.PluginName);
                Equal("1.0", plugin.PluginVersion); Equal("25.12", plugin.SdkSeries); Equal("x86_64", plugin.Architecture);
                Compatibility.Evaluate(plugin, Router("25.12.5", "x86_64")); Equal(CompatibilityStatus.Compatible, plugin.CompatibilityStatus);
            });
            Run("Architecture negative acceptance", () =>
            {
                var plugin = Analyzer.AnalyzeFileName("PassWall_26.9.16_aarch64_generic_all_sdk_25.12.run");
                Equal("aarch64_generic", plugin.Architecture); Compatibility.Evaluate(plugin, Router("25.12.5", "x86_64"));
                Equal(CompatibilityStatus.Incompatible, plugin.CompatibilityStatus); True(plugin.CompatibilityMessage.Contains("架构"));
            });
            foreach (string arch in new[] { "aarch64", "aarch64_generic", "aarch64_a53", "aarch64_a72", "x86_64" })
            {
                string current = arch;
                Run("Strict architecture " + current, () =>
                {
                    var plugin = Analyzer.AnalyzeFileName("New_1.0_" + current + "_all_sdk_25.12.run");
                    Equal(current, plugin.Architecture); Compatibility.Evaluate(plugin, Router("25.12.5", current));
                    Equal(CompatibilityStatus.Compatible, plugin.CompatibilityStatus);
                    if (current != "x86_64") { Compatibility.Evaluate(plugin, Router("25.12.5", "x86_64")); Equal(CompatibilityStatus.Incompatible, plugin.CompatibilityStatus); }
                });
            }
            Run("SDK mismatch", () =>
            {
                var plugin = Analyzer.AnalyzeFileName("PassWall_1_x86_64_all_sdk_24.10.run");
                Compatibility.Evaluate(plugin, Router("25.12.5", "x86_64")); Equal(CompatibilityStatus.Incompatible, plugin.CompatibilityStatus);
            });
            foreach (string name in new[] { "PassWall_1_x86_64_all.run", "PassWall_1_mips_all_sdk_25.12.run", "PassWall_1_x86_64_all_sdk_bad.run", "Fake.txt", "" })
            {
                string current = name;
                Run("Incomplete metadata stays unknown: " + current, () =>
                {
                    var plugin = Analyzer.AnalyzeFileName(current); Compatibility.Evaluate(plugin, Router("25.12.5", "x86_64"));
                    Equal(CompatibilityStatus.Unknown, plugin.CompatibilityStatus);
                });
            }
            Run("Patch versions do not block compatibility", () =>
            {
                var plugin = Analyzer.AnalyzeFileName("PassWall_1_x86_64_all_sdk_25.12.run");
                Compatibility.Evaluate(plugin, Router("25.12.9", "x86_64")); Equal(CompatibilityStatus.Compatible, plugin.CompatibilityStatus);
            });
            Run("Disconnected router invalidates compatibility", () =>
            {
                var plugin = Analyzer.AnalyzeFileName("PassWall_1_x86_64_all_sdk_25.12.run"); var router = Router("25.12.5", "x86_64");
                Compatibility.Evaluate(plugin, router); router.IsConnected = false; Compatibility.Evaluate(plugin, router);
                Equal(CompatibilityStatus.Unknown, plugin.CompatibilityStatus);
            });
            Run("SDK extraction", () =>
            {
                Equal("25.12", RouterDetectionService.GetSdkSeries("25.12.5")); Equal("24.10", RouterDetectionService.GetSdkSeries("24.10.4"));
                Equal("22.03", RouterDetectionService.GetSdkSeries("22.03.7")); Equal(null, RouterDetectionService.GetSdkSeries("SNAPSHOT"));
            });
            Run("Quoted release parsing", () =>
            {
                var fields = RouterDetectionService.ParseRelease("# comment\nVERSION_ID=\"25.12.5\"\r\nOPENWRT_ARCH='x86_64'\nNAME=\"iStoreOS\"\n");
                Equal("25.12.5", fields["VERSION_ID"]); Equal("x86_64", fields["OPENWRT_ARCH"]);
            });
            Run("Environment detection and capability independence", () =>
            {
                var session = new FakeSession(); session.Set(RouterProbe.OsRelease, "VERSION_ID=22.03.7\nOPENWRT_ARCH=x86_64"); session.Set(RouterProbe.PackageManager, "APK");
                var router = new RouterDetectionService().Detect(session, Ignore, CancellationToken.None);
                Equal("22.03", router.SdkSeries); Equal(PackageManagerKind.APK, router.PackageManager); Equal(false, session.Calls.Contains(RouterProbe.OpenWrtRelease));
                session.Set(RouterProbe.OsRelease, "VERSION_ID=25.12.5\nOPENWRT_ARCH=x86_64"); session.Set(RouterProbe.PackageManager, "OPKG");
                Equal(PackageManagerKind.OPKG, new RouterDetectionService().Detect(session, Ignore, CancellationToken.None).PackageManager);
                session.Set(RouterProbe.PackageManager, "Unknown"); Equal(PackageManagerKind.Unknown, new RouterDetectionService().Detect(session, Ignore, CancellationToken.None).PackageManager);
            });
            Run("Architecture falls back to DISTRIB_ARCH", () =>
            {
                var session = new FakeSession(); session.Set(RouterProbe.OsRelease, "VERSION_ID=25.12.5"); session.Set(RouterProbe.OpenWrtRelease, "DISTRIB_ARCH='aarch64_a53'");
                var router = new RouterDetectionService().Detect(session, Ignore, CancellationToken.None);
                Equal("aarch64_a53", router.Architecture); True(session.Calls.Contains(RouterProbe.OpenWrtRelease));
            });
            Run("Missing router metadata stays unknown", () =>
            {
                var router = new RouterDetectionService().Detect(new FakeSession(), Ignore, CancellationToken.None);
                var plugin = Analyzer.AnalyzeFileName("PassWall_1_x86_64_all_sdk_25.12.run"); Compatibility.Evaluate(plugin, router);
                Equal(CompatibilityStatus.Unknown, plugin.CompatibilityStatus);
            });
            Run("QuickStart accepts useful JSON", () =>
            {
                True(QuickStartDetectionService.IsReasonableResponse("[{\"name\":\"default\",\"url\":\"https://example.org\"}]"));
                True(QuickStartDetectionService.IsReasonableResponse("{\"code\":0,\"data\":{\"source\":\"default\"}}"));
                True(QuickStartDetectionService.IsReasonableResponse("{\"current\":\"default\"}"));
            });
            Run("QuickStart rejects error, HTML, scalar and empty JSON", () =>
            {
                foreach (string text in new[] { "<html>login</html>", "", "[]", "{}", "null", "true", "\"ok\"", "{\"error\":\"unauthorized\"}", "{\"code\":403,\"data\":{\"source\":\"default\"}}", "{\"success\":false,\"data\":[\"default\"]}" })
                    Equal(false, QuickStartDetectionService.IsReasonableResponse(text));
            });
            Run("QuickStart falls back to current source", () =>
            {
                var session = new FakeSession(); session.Set(RouterProbe.QuickStartCurrentSource, "{\"source\":\"default\"}");
                True(new QuickStartDetectionService().Detect(session, Ignore, CancellationToken.None)); Equal(2, session.Calls.Count);
            });
            Run("QuickStart unavailable does not throw", () => Equal(false, new QuickStartDetectionService().Detect(new FakeSession(), Ignore, CancellationToken.None)));
            Run("TOFU first, same, changed and separate hosts", () =>
            {
                var trust = new HostKeyTrustStore(); bool first;
                True(trust.TryTrust("router", 22, "SHA256:first", out first)); True(first);
                True(trust.TryTrust("ROUTER", 22, "SHA256:first", out first)); Equal(false, first);
                Equal(false, trust.TryTrust("router", 22, "SHA256:changed", out first));
                True(trust.TryTrust("other", 22, "SHA256:changed", out first));
            });
            Run("Password redaction including nested exceptions", () =>
            {
                string secret = "test secret+$"; string text = LogHelper.ExceptionDetails(new Exception(secret, new Exception(Uri.EscapeDataString(secret))), secret);
                Equal(false, text.Contains(secret)); Equal(false, text.Contains(Uri.EscapeDataString(secret))); True(text.Contains("[已隐藏]"));
            });
            Run("SSH error classification", () =>
            {
                Equal(ConnectionError.AuthenticationFailure, SshService.ClassifyError(new SshAuthenticationException("denied")));
                Equal(ConnectionError.Timeout, SshService.ClassifyError(new SshOperationTimeoutException("timeout")));
                Equal(ConnectionError.NetworkUnreachable, SshService.ClassifyError(new SocketException((int)SocketError.ConnectionRefused)));
                Equal(ConnectionError.SshProtocolError, SshService.ClassifyError(new SshConnectionException("protocol")));
                Equal(ConnectionError.Other, SshService.ClassifyError(new Exception("other")));
            });
            Run("Fixed commands are read-only", () =>
            {
                foreach (RouterProbe probe in Enum.GetValues(typeof(RouterProbe)))
                {
                    string command = ReadOnlyRouterCommands.Get(probe);
                    Equal(false, Regex.IsMatch(command, @"\b(rm|mv|cp|sed|uci|apk|opkg|sh)\s"));
                    Equal(false, command.Contains("POST")); Equal(false, command.Contains("/etc/init.d/"));
                }
                True(ReadOnlyRouterCommands.Get(RouterProbe.QuickStartSources).Contains("127.0.0.1:3038"));
                True(ReadOnlyRouterCommands.Get(RouterProbe.QuickStartSources).Contains("X-Forwarded-Sid: ssh-root-poc"));
            });
            Run("SSH.NET runtime dependencies load", () =>
            {
                using (var ssh = new SshClient("127.0.0.1", "test", "unused-test-value"))
                { True(ssh.ConnectionInfo.HostKeyAlgorithms.Count > 0); True(ssh.ConnectionInfo.KeyExchangeAlgorithms.Count > 0); }
            });
            Run("Non-run files rejected", () =>
            {
                string path = Path.GetTempFileName();
                try { bool rejected = false; try { Analyzer.AnalyzeFile(path); } catch (ArgumentException) { rejected = true; } True(rejected); }
                finally { File.Delete(path); }
            });
            SoftwareTests.Run(Run);
            InstallationTests.Run(Run);
            UiTests.Run(Run);
            WorkflowTests.Run(Run);
            CompactUiTests.Run(Run);
            RoundedUiTests.Run(Run);
            AdvancedUiTests.Run(Run);
            UiTests.RunReleaseAudit(Run);
            if (args.Length == 2 && args[0] == "--file") Run("Real local package", () =>
            {
                var plugin = Analyzer.AnalyzeFile(args[1]); Equal("PassWall", plugin.PluginName); Equal("26.9.16", plugin.PluginVersion);
                Equal("25.12", plugin.SdkSeries); Equal("x86_64", plugin.Architecture); Equal("all", plugin.PackageType);
                True(plugin.FileSize > 0); Compatibility.Evaluate(plugin, Router("25.12.5", "x86_64")); Equal(CompatibilityStatus.Compatible, plugin.CompatibilityStatus);
                Console.WriteLine("FILE " + plugin.FileName + " | bytes=" + plugin.FileSize + " | " + plugin.PluginName + " " + plugin.PluginVersion + " | " + plugin.Architecture + " | SDK " + plugin.SdkSeries + " | " + plugin.PackageType + " | " + plugin.CompatibilityStatus + " (fixture router)");
            });
            if (args.Length == 2 && args[0] == "--file") Run("V0.3 real local package SHA256 (read only)", () =>
            {
                using (var stream = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string hash = PluginPackageInspection.ComputeSha256(stream);
                    Equal("956f12b53a970e7e36941cc000d5f441a3ef63ac5862f9b9a36a8def866bfdd1", hash);
                    Console.WriteLine("REAL LOCAL SHA256 " + hash + " | bytes=" + stream.Length + " | never executed");
                }
            });
            Console.WriteLine("RESULT: " + passed + " passed, " + failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        private static void CaptureView(Form view, string outputPath, string suffix)
        {
            Application.DoEvents();
            using (var image = new Bitmap(view.Width, view.Height))
            { view.DrawToBitmap(image, new Rectangle(Point.Empty, view.Size)); image.Save(Path.ChangeExtension(outputPath, suffix + ".png"), System.Drawing.Imaging.ImageFormat.Png); }
        }
        private static void UiSmoke(string outputPath)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new MainForm())
            {
                form.Show(); Application.DoEvents();
                Equal("iStoreOS 插件安装助手 V1.0", form.Text);
                True(((TextBox)UiTests.Control(form, "passwordBox")).PasswordChar != '\0');
                True(((RichTextBox)UiTests.Control(form, "logBox")).ReadOnly);
                True(UiTests.Control(form, "dropLabel").AllowDrop);
                Equal("192.168.100.1", UiTests.Control(form, "hostBox").Text);
                True(UiTests.Control(form, "connectButton").Enabled);
                Equal(false, UiTests.Control(form, "softwareButton").Enabled);
                Equal(false, UiTests.Control(form, "installButton").Enabled);
                using (var image = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.ChangeExtension(outputPath, ".empty.png"), System.Drawing.Imaging.ImageFormat.Png); }
                UiTests.Invoke(form, "ShowAdvancedInformation", false); var emptyView = UiTests.Field<AdvancedInfoForm>(form, "advancedInfoForm");
                var emptyTabs = (TabControl)UiTests.Control(form, "advancedTabs");
                for (int i = 0; i < 5; i++) { emptyTabs.SelectedIndex = i; CaptureView(emptyView, outputPath, ".advanced-empty-" + i); }
                string initialLog = UiTests.Control(form, "logBox").Text; UiTests.Control(form, "logBox").Text = ""; CaptureView(emptyView, outputPath, ".advanced-empty-log"); UiTests.Control(form, "logBox").Text = initialLog;
                emptyTabs.SelectedIndex = 0; emptyView.Close();
                string fixtureDirectory = Path.Combine(Path.GetTempPath(), "istore-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixtureDirectory);
                string path = Path.Combine(fixtureDirectory, "SomeNewPlugin_1.0_x86_64_all_sdk_25.12.run");
                string passwallPath = Path.Combine(fixtureDirectory, "PassWall_26.9.16_x86_64_all_sdk_25.12.run");
                try
                {
                    File.WriteAllText(path, "small filename-only UI fixture");
                    form.GetType().GetMethod("SelectPlugin", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { path });
                    True(UiTests.Control(form, "compatibilityLabel").Text.Contains("请先连接"));
                    using (var image = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png); }
                    form.Size = form.MinimumSize; Application.DoEvents();
                    foreach (string controlName in new[] { "passwordBox", "passwordToggleButton", "connectButton", "toggleLogButton" })
                    { var control = UiTests.Control(form, controlName); Console.WriteLine("BOUNDS " + controlName + " " + control.Bounds + " parent=" + control.Parent.ClientSize); }
                    using (var image = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.ChangeExtension(outputPath, ".min.png"), System.Drawing.Imaging.ImageFormat.Png); }
                    UiTests.Invoke(form, "ToggleLogButton_Click", null, EventArgs.Empty); Application.DoEvents();
                    var advanced = UiTests.Field<AdvancedInfoForm>(form, "advancedInfoForm");
                    using (var image = new Bitmap(advanced.Width, advanced.Height))
                    { advanced.DrawToBitmap(image, new Rectangle(Point.Empty, advanced.Size)); image.Save(Path.ChangeExtension(outputPath, ".advanced.png"), System.Drawing.Imaging.ImageFormat.Png); }
                    var summary = new InstallConfirmation { Plugin = Analyzer.AnalyzeFileName("PassWall_26.9.16_x86_64_all_sdk_25.12.run"), Router = Router("25.12.5", "x86_64"), SoftwareEnvironment = new SoftwareEnvironmentResult { Status = SoftwareEnvironmentStatus.Ready }, Sha256 = new string('a', 64) };
                    using (var dialog = new InstallConfirmationForm(summary))
                    {
                        dialog.Show(form); Application.DoEvents();
                        using (var image = new Bitmap(dialog.Width, dialog.Height))
                        { dialog.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.Size)); image.Save(Path.ChangeExtension(outputPath, ".confirm.png"), System.Drawing.Imaging.ImageFormat.Png); }
                        dialog.Close();
                    }
                    advanced.Close();
                    UiTests.Field<System.Windows.Forms.Timer>(form, "connectionTimer").Stop();
                    var router = Router("25.12.5", "x86_64"); router.PackageManager = PackageManagerKind.APK; router.QuickStartAvailable = true;
                    UiTests.Set(form, "router", router); UiTests.Invoke(form, "DisplayEnvironment");
                    UiTests.Invoke(form, "ShowAdvancedInformation", false); CaptureView(advanced, outputPath, ".advanced-system-ready"); advanced.Close();
                    UiTests.Invoke(form, "SetStatusAppearance", UiTests.Control(form, "connectionLabel"), UiTone.Success);
                    var software = new SoftwareEnvironmentResult { Status = SoftwareEnvironmentStatus.Ready, Message = "软件环境已就绪；当前源无需修改。",
                        CurrentSource = new SoftwareSourceInfo { Identity = "CERNET", Name = "高校联合镜像站", Url = "https://mirrors.cernet.edu.cn/openwrt/" },
                        OfficialHealth = new RepositoryHealthResult(), AdditionalHealth = new RepositoryHealthResult() };
                    for (int i = 0; i < 8; i++) software.OfficialHealth.Probes.Add(new RepositoryProbeResult { Success = true, HttpStatus = 206, ExitCode = 0 });
                    software.AdditionalHealth.Probes.Add(new RepositoryProbeResult { Success = true, HttpStatus = 206, ExitCode = 0 });
                    software.Updates.Add(new PackageUpdateResult { Success = true, ExitCode = 0 }); UiTests.Invoke(form, "DisplaySoftwareResult", software);
                    UiTests.Control(form, "softwareStatusLabel").Text = software.Message;
                    File.WriteAllText(passwallPath, "UI fixture only; never execute"); UiTests.Invoke(form, "SelectPlugin", passwallPath);
                    UiTests.Invoke(form, "RefreshCompatibility");
                    Application.DoEvents();
                    using (var image = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.ChangeExtension(outputPath, ".ready.png"), System.Drawing.Imaging.ImageFormat.Png); }
                    var success = new PluginInstallResult { Status = InstallStatus.SuccessVerified, Plugin = Analyzer.AnalyzeFileName("PassWall_26.9.16_x86_64_all_sdk_25.12.run"), InstallerStarted = true,
                        InstallerEnded = true, InstallerExitCode = 0, CleanupSucceeded = true, SoftwareEnvironment = software,
                        Verification = new PluginVerificationResult { Verified = true, PackageName = "luci-app-passwall", Version = "26.9.16-r1" }, Diff = new PackageDiff() };
                    success.Diff.Added.Add(new PackageChange { Name = "luci-app-passwall", AfterVersion = "26.9.16-r1" });
                    success.Diff.Updated.Add(new PackageChange { Name = "dependency-fixture", BeforeVersion = "1.0", AfterVersion = "1.1" });
                    success.Diff.Removed.Add(new PackageChange { Name = "removed-fixture", BeforeVersion = "0.9" });
                    UiTests.Invoke(form, "DisplayWorkflowResult", new InstallationWorkflowResult { State = InstallationState.Success, Installation = success, Router = router, Software = software }); Application.DoEvents();
                    UiTests.Set(form, "installationRequiresReview", true); UiTests.Invoke(form, "RefreshInstallButton");
                    using (var image = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.ChangeExtension(outputPath, ".success.png"), System.Drawing.Imaging.ImageFormat.Png); }
                    UiTests.Invoke(form, "ShowAdvancedInformation", false); ((TabControl)UiTests.Control(form, "advancedTabs")).SelectedIndex = 2; CaptureView(advanced, outputPath, ".advanced-install-success");
                    ((TabControl)UiTests.Control(form, "advancedTabs")).SelectedIndex = 3; CaptureView(advanced, outputPath, ".advanced-diff-populated"); advanced.Close();
                    var failure = new PluginInstallResult { Status = InstallStatus.FailedIntegrity, Message = "SHA256 不匹配", Plugin = success.Plugin, SoftwareEnvironment = software,
                        CleanupSucceeded = true, RemoteDirectory = "/tmp/istore-plugin-helper/00000000000000000000000000000000" };
                    UiTests.Set(form, "installationRequiresReview", false); UiTests.Invoke(form, "RefreshInstallButton");
                    UiTests.Invoke(form, "DisplayWorkflowResult", new InstallationWorkflowResult { State = InstallationState.ActionRequiredFailure, Installation = failure, Router = router, Software = software, Failure = InstallFailureClassifier.Failure(InstallationFailureKind.Integrity) }); Application.DoEvents();
                    using (var image = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(Path.ChangeExtension(outputPath, ".failure.png"), System.Drawing.Imaging.ImageFormat.Png); }
                    UiTests.Invoke(form, "ToggleLogButton_Click", null, EventArgs.Empty); Application.DoEvents();
                    var advancedTabs = (TabControl)UiTests.Control(form, "advancedTabs");
                    foreach (int tabIndex in new[] { 1, 2, 3, 4 })
                    {
                        advancedTabs.SelectedIndex = tabIndex; Application.DoEvents();
                        using (var image = new Bitmap(advanced.Width, advanced.Height))
                        { advanced.DrawToBitmap(image, new Rectangle(Point.Empty, advanced.Size)); image.Save(Path.ChangeExtension(outputPath, ".advanced-" + tabIndex + ".png"), System.Drawing.Imaging.ImageFormat.Png); }
                    }
                    UiTests.Invoke(form, "AppendInstallLog", "长行示例：https://fixture.test/" + new string('x', 320) + "?hash=" + new string('a', 64));
                    CaptureView(advanced, outputPath, ".advanced-long-line");
                    for (int i = 0; i < 70; i++) UiTests.Invoke(form, "AppendInstallLog", "滚动示例 " + i + "：仅用于检查原生纵向滚动及圆角边缘。");
                    CaptureView(advanced, outputPath, ".advanced-long-log");
                    advanced.ClientSize = new Size(1024, 720); CaptureView(advanced, outputPath, ".advanced-resized");
                    advanced.WindowState = FormWindowState.Maximized; CaptureView(advanced, outputPath, ".advanced-maximized");
                    advanced.WindowState = FormWindowState.Normal; advanced.ClientSize = new Size(800, 600);
                    advanced.Scale(new SizeF(1.25F, 1.25F));
                    advancedTabs.SelectedIndex = 0; CaptureView(advanced, outputPath, ".advanced-125-system");
                    advancedTabs.SelectedIndex = 4; CaptureView(advanced, outputPath, ".advanced-125-logs");
                    advanced.Close();
                    Console.WriteLine("UI snapshot: " + Path.GetFullPath(outputPath));
                }
                finally { File.Delete(path); File.Delete(passwallPath); Directory.Delete(fixtureDirectory); form.Close(); }
            }
        }

        private static RouterEnvironment Router(string version, string arch)
        { return new RouterEnvironment { Version = version, SdkSeries = RouterDetectionService.GetSdkSeries(version), Architecture = arch, IsConnected = true }; }
        private static void AssertPackage(string name, string pluginName, string version, string arch, string type)
        { var info = Analyzer.AnalyzeFileName(name); Equal(pluginName, info.PluginName); Equal(version, info.PluginVersion); Equal(arch, info.Architecture); Equal("25.12", info.SdkSeries); Equal(type, info.PackageType); }
        private static void Ignore(string text) { }
        private static void Run(string name, Action action)
        { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception exception) { failed++; Console.WriteLine("FAIL " + name + ": " + exception.Message); } }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual)
        { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }

        private sealed class FakeSession : IReadOnlyRouterSession
        {
            private readonly Dictionary<RouterProbe, ProbeResult> values = new Dictionary<RouterProbe, ProbeResult>();
            public readonly List<RouterProbe> Calls = new List<RouterProbe>();
            public void Set(RouterProbe probe, string output) { values[probe] = new ProbeResult { ExitStatus = 0, Output = output }; }
            public ProbeResult Execute(RouterProbe probe)
            { Calls.Add(probe); ProbeResult result; return values.TryGetValue(probe, out result) ? result : new ProbeResult { ExitStatus = 1, Output = string.Empty }; }
        }
    }
}

