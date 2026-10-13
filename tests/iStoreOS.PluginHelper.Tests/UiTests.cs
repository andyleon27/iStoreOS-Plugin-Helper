using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class UiTests
    {
        public static void Run(Action<string, Action> test)
        {
            test("V0.3.1 generated detection log can be saved before workflow finally", () =>
            {
                using (var form = new MainForm())
                {
                    Set(form, "isSoftwareBusy", true);
                    Invoke(form, "AppendSoftwareLog", "fixture package update exit=0");
                    Equal(true, UiTests.Control(form, "saveSoftwareLogButton").Enabled);
                }
            });
            test("V0.3.1 diagnostic export available without pretending to have installed", () =>
            {
                using (var form = new MainForm())
                {
                    Invoke(form, "AppendLog", "connection and selection fixture");
                    Equal(true, UiTests.Control(form, "saveSoftwareLogButton").Enabled);
                    Equal(true, UiTests.Control(form, "saveInstallLogButton").Enabled);
                    Equal("保存当前日志", UiTests.Control(form, "saveInstallLogButton").Text);
                    string text = Text(form, "GetInstallLogText");
                    Equal(true, text.Contains("尚未执行安装流程")); Equal(true, text.Contains("connection and selection fixture"));
                    Equal(0, Field<StringBuilder>(form, "installTranscript").Length);
                    Equal(false, UiTests.Control(form, "cancelInstallButton").Enabled);
                    Equal(false, UiTests.Control(form, "installButton").Enabled);
                }
            });
            test("V0.3.1 scoped export retains full detection records in progress", () =>
            {
                using (var form = new MainForm())
                {
                    Invoke(form, "AppendLog", "general-only fixture"); Set(form, "isSoftwareBusy", true);
                    Invoke(form, "AppendSoftwareLog", new string('A', 200010)); Invoke(form, "AppendSoftwareLog", "最后一行\n第二行");
                    string text = Text(form, "GetSoftwareLogText");
                    Equal(true, text.Contains(new string('A', 200010))); Equal(true, text.Contains("最后一行\n第二行"));
                    Equal(true, text.Contains("软件环境检测中")); Equal(false, text.Contains("general-only fixture"));
                    Equal("保存检测日志", UiTests.Control(form, "saveSoftwareLogButton").Text);
                }
            });
            test("V0.3.1 exported snapshot writes complete UTF8 log", () =>
            {
                string path = Path.GetTempFileName();
                try
                {
                    using (var form = new MainForm())
                    {
                        Invoke(form, "AppendSoftwareLog", "HTTP 206\n退出码 0\n软件环境已就绪");
                        string text = Text(form, "GetSoftwareLogText");
                        form.GetType().GetMethod("WriteLogFile", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { path, text });
                        Equal(text, File.ReadAllText(path, Encoding.UTF8)); Equal(0xEF, (int)File.ReadAllBytes(path)[0]);
                    }
                }
                finally { File.Delete(path); }
            });
            test("V0.3.1 install preparation export cannot enable cancel after dispatch", () =>
            {
                using (var token = new CancellationTokenSource())
                using (var form = new MainForm())
                {
                    Set(form, "isInstallBusy", true); Set(form, "installCancellation", token);
                    Invoke(form, "RefreshInstallButton"); Equal(true, UiTests.Control(form, "cancelInstallButton").Enabled);
                    Invoke(form, "AppendInstallLog", "preparation fixture"); Equal(true, UiTests.Control(form, "saveInstallLogButton").Enabled);
                    Equal("保存安装日志", UiTests.Control(form, "saveInstallLogButton").Text);
                    Set(form, "installerDispatched", true); Invoke(form, "RefreshInstallButton");
                    Equal(false, UiTests.Control(form, "cancelInstallButton").Enabled); Equal(false, token.IsCancellationRequested);
                    Equal(true, UiTests.Control(form, "cancelPreparationHint").Text.Contains("不可取消"));
                    Set(form, "isInstallBusy", false); Invoke(form, "RefreshInstallButton"); Equal(false, UiTests.Control(form, "cancelInstallButton").Enabled);
                    Set(form, "installCancellation", null);
                }
            });
            test("V0.3.1 password reveal hides again without changing or logging text", () =>
            {
                using (var form = new MainForm())
                {
                    var password = (TextBox)UiTests.Control(form, "passwordBox"); password.Text = "only-ui-test-secret";
                    Invoke(form, "PasswordToggleButton_Click", null, EventArgs.Empty); Equal('\0', password.PasswordChar); Equal("only-ui-test-secret", password.Text);
                    Invoke(form, "PasswordToggleButton_Click", null, EventArgs.Empty); Equal('●', password.PasswordChar); Equal("only-ui-test-secret", password.Text);
                    Equal(false, UiTests.Control(form, "logBox").Text.Contains("only-ui-test-secret"));
                }
            });
            test("V0.3.1 password toggle follows connection busy state", () =>
            {
                using (var form = new MainForm())
                {
                    Invoke(form, "SetConnectionInputs", false); Equal(false, UiTests.Control(form, "passwordToggleButton").Enabled);
                    Invoke(form, "SetConnectionInputs", true); Equal(true, UiTests.Control(form, "passwordToggleButton").Enabled);
                    Invoke(form, "SetPasswordVisibility", false); Equal('●', ((TextBox)UiTests.Control(form, "passwordBox")).PasswordChar);
                }
            });
            test("V0.3.1 collapsed logs retain full transcript after visible buffer trims", () =>
            {
                using (var form = new MainForm())
                {
                    Equal(false, Field<AdvancedInfoForm>(form, "advancedInfoForm").Visible);
                    Invoke(form, "AppendSoftwareLog", new string('A', 200010)); Invoke(form, "AppendSoftwareLog", "last line");
                    var transcript = Field<StringBuilder>(form, "softwareTranscript"); int before = transcript.Length;
                    Equal(true, before > UiTests.Control(form, "logBox").Text.Length);
                    Invoke(form, "ToggleLogButton_Click", null, EventArgs.Empty); Equal(true, Field<AdvancedInfoForm>(form, "advancedInfoForm").Visible);
                    Field<AdvancedInfoForm>(form, "advancedInfoForm").Close(); Equal(false, Field<AdvancedInfoForm>(form, "advancedInfoForm").Visible); Equal(before, transcript.Length);
                }
            });
            test("V0.3.1 embedded icons transparent and missing resource degrades", () =>
            {
                using (var resources = new UiResources())
                {
                    var image = resources.Image("app-logo", 32); Equal(true, image != null); Equal(0, ((Bitmap)image).GetPixel(0, 0).A);
                    Equal(null, resources.Image("nonexistent-fixture-icon", 24)); Equal(null, resources.Image("nonexistent-fixture-icon", 24)); Equal(1, resources.Warnings.Count);
                }
            });
            test("V0.3.1 failure summary is Chinese and keeps engine status", () =>
            {
                var result = new PluginInstallResult { Status = InstallStatus.FailedUpload, Message = "SFTP subsystem unavailable" };
                Equal(true, UiResultFormatter.Title(result).Contains("上传失败")); Equal(InstallStatus.FailedUpload, result.Status); Equal(UiTone.Error, UiResultFormatter.Tone(result.Status));
                result.Status = InstallStatus.FailedIntegrity; result.Message = "SHA256 mismatch"; Equal(true, UiResultFormatter.Title(result).Contains("SHA256"));
            });
            test("V0.3.1 unknown install cannot be styled as verified success", () =>
            {
                var result = new PluginInstallResult { Status = InstallStatus.InstallerStateUnknown, InstallerStarted = true };
                Equal(UiTone.Warning, UiResultFormatter.Tone(result.Status)); Equal(true, UiResultFormatter.Details(result).Contains("请勿重复安装"));
            });
            test("V0.3.1 success summary reflects cleanup warning and source attempt", () =>
            {
                var result = new PluginInstallResult { Status = InstallStatus.SuccessVerified, RemoteDirectory = "/tmp/test-fixture", CleanupSucceeded = false,
                    SoftwareEnvironment = new SoftwareEnvironmentResult { SwitchAttempted = true, SourceSwitchOccurred = false } };
                result.Warnings.Add("cleanup warning"); string text = UiResultFormatter.Details(result);
                Equal(true, text.Contains("未确认清理")); Equal(true, text.Contains("曾尝试切换")); Equal(true, text.Contains("1 条提示")); Equal(InstallStatus.SuccessVerified, result.Status);
            });
            test("V0.3.1 result display does not expand technical logs", () =>
            {
                using (var form = new MainForm())
                {
                    var result = new PluginInstallResult { Status = InstallStatus.SuccessWithWarning, InstallerStarted = true, InstallerEnded = true, InstallerExitCode = 0 };
                    Invoke(form, "DisplayInstallResult", result);
                    Equal(true, UiTests.Control(form, "installHeadlineLabel").Text.Contains("自动验证不完整")); Equal(false, Field<AdvancedInfoForm>(form, "advancedInfoForm").Visible);
                }
            });
        }
        public static void RunReleaseAudit(Action<string, Action> test)
        {
            test("RC1 direct form disposal clears password and session credential references", () =>
            {
                var form = new MainForm(); var password = (TextBox)Control(form, "passwordBox");
                var service = Field<Services.SshService>(form, "ssh"); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                password.Text = "fixture-password-for-dispose";
                service.GetType().GetField("connectionFactory", flags).SetValue(service, new Func<Renci.SshNet.PasswordConnectionInfo>(() => new Renci.SshNet.PasswordConnectionInfo("fixture.test", 22, "fixture", "fixture-password-for-dispose")));
                service.GetType().GetField("sanitizeLog", flags).SetValue(service, new Func<string, string>(text => text.Replace("fixture-password-for-dispose", "[已隐藏]")));
                Equal(true, service.CanReconnect); form.Dispose();
                Equal("", password.Text); Equal(false, service.CanReconnect);
                Equal<object>(null, service.GetType().GetField("connectionFactory", flags).GetValue(service));
                Equal(true, service.GetType().GetField("sanitizeLog", flags).GetValue(service) is Func<string, string>);
                Equal(false, ((Func<string, string>)service.GetType().GetField("sanitizeLog", flags).GetValue(service)).Target != null);
            });
            test("RC1 host key rejection and actionable connection errors remain visible", () =>
            {
                using (var form = new MainForm())
                foreach (ConnectionError error in Enum.GetValues(typeof(ConnectionError)))
                {
                    if (error == ConnectionError.None) continue;
                    string message = (string)typeof(Services.SshService).GetMethod("GetUserMessage", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { error });
                    Invoke(form, "DisplayConnectionOutcome", new ConnectionResult { Error = error, UserMessage = message });
                    Equal("✗ " + message, Control(form, "connectionLabel").Text);
                    if (error == ConnectionError.HostKeyChanged) Equal(true, Control(form, "connectionLabel").Text.Contains("已拒绝连接"));
                }
            });
        }
        public static void Invoke(MainForm form, string name, params object[] values)
        { form.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, values); }
        public static Control Control(MainForm form, string name)
        {
            var controls = form.Controls.Find(name, true);
            if (controls.Length > 0) return controls[0];
            var advanced = Field<AdvancedInfoForm>(form, "advancedInfoForm");
            controls = advanced.Controls.Find(name, true);
            if (controls.Length == 0) throw new Exception("Missing UI control: " + name);
            return controls[0];
        }
        private static string Text(MainForm form, string name)
        { return (string)form.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null); }
        public static T Field<T>(MainForm form, string name)
        { return (T)form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form); }
        public static void Set(MainForm form, string name, object value)
        { form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    }
}

