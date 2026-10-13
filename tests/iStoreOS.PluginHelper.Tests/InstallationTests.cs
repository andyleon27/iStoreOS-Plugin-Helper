using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class InstallationTests
    {
        private const string PassWall = "PassWall_26.9.16_x86_64_all_sdk_25.12.run";
        private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        public static void Run(Action<string, Action> test)
        {
            test("V0.3 local SHA256 known vector", () =>
            { using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"))) { Equal(AbcHash, PluginPackageInspection.ComputeSha256(stream)); Equal(0L, stream.Position); } });
            test("V0.3 remote SHA256 accepts uppercase and rejects invalid", () =>
            { Equal(AbcHash, PluginPackageInspection.ParseSha256(AbcHash.ToUpperInvariant() + "  /tmp/a\n")); Throws(() => PluginPackageInspection.ParseSha256("not-a-hash")); });
            test("V0.3 df parses KiB and inherited root filesystem", () =>
            { Equal(2048L * 1024, PluginPackageInspection.ParseTmpAvailable("Filesystem 1024-blocks Used Available Capacity Mounted on\ntmpfs 4096 2048 2048 50% /tmp\n")); Equal(400L * 1024, PluginPackageInspection.ParseTmpAvailable("rootfs 1000 600 400 60% /\n")); Throws(() => PluginPackageInspection.ParseTmpAvailable("broken")); });
            test("V0.3 Makeself parses metadata and unpacked size", () =>
            { var info = PluginPackageInspection.ParseInfo(Info()); Equal("x86_64", info.Architecture); Equal("25.12", info.SdkSeries); Equal(81680L * 1024, info.UncompressedBytes.Value); Equal("gzip", info.Compression); Equal("2.7.2", info.MakeselfVersion); });
            test("V0.3 MD5 success without embedded SHA256 is valid", () =>
            { True(PluginPackageInspection.IntegrityPassed(Check())); Equal(false, PluginPackageInspection.IntegrityPassed(new ProbeResult { ExitStatus = 2, Output = "All good." })); Equal(false, PluginPackageInspection.IntegrityPassed(new ProbeResult { ExitStatus = 0, Output = "done" })); });
            test("V0.3 APK snapshot handles hyphens revisions and digits", () =>
            { var snapshot = PackageSnapshotService.Parse("luci-app-passwall-26.9.16-r1\nlibstdc++6-14.3.0-r2\nxray-core-26.9.9-r1\n", PackageManagerKind.APK); Equal("26.9.16-r1", snapshot.Packages["luci-app-passwall"]); Equal("14.3.0-r2", snapshot.Packages["libstdc++6"]); });
            test("V0.3 OPKG snapshot dictionary and malformed refusal", () =>
            { Equal("1.2-r3", PackageSnapshotService.Parse("luci-app-example - 1.2-r3\n", PackageManagerKind.OPKG).Packages["luci-app-example"]); Throws(() => PackageSnapshotService.Parse("WARNING: no database", PackageManagerKind.APK)); Throws(() => PackageSnapshotService.Parse("", PackageManagerKind.APK)); });
            test("V0.3 package diff Added Updated Removed", () =>
            { var diff = PackageSnapshotService.Compare(Snapshot("old-1.0\nchanged-1.0\nsame-2.0"), Snapshot("new-3.0\nchanged-2.0\nsame-2.0")); Equal("new", diff.Added.Single().Name); Equal("1.0", diff.Updated.Single().BeforeVersion); Equal("2.0", diff.Updated.Single().AfterVersion); Equal("old", diff.Removed.Single().Name); });
            test("V0.3 Known rules only confirmed PassWall and OpenClash", () =>
            { Equal("luci-app-passwall", PluginVerificationService.KnownMainPackage(Plugin(PassWall))); Equal("luci-app-openclash", PluginVerificationService.KnownMainPackage(Plugin("OpenClash_0.47.156+x86_64_core_sdk_25.12.run"))); Equal(null, PluginVerificationService.KnownMainPackage(Plugin("PassWall2_1_x86_64_all_sdk_25.12.run"))); });
            test("V0.3 unique luci-app discovery requires independent query", () =>
            { var plugin = Plugin("New_1.0_x86_64_all_sdk_25.12.run"); var before = Snapshot("base-1.0"); var after = Snapshot("base-1.0\nluci-app-new-1.0-r1"); var diff = PackageSnapshotService.Compare(before, after); var v = new PluginVerificationService(); True(v.Verify(plugin, after, diff, name => name == "luci-app-new").Verified); Equal(false, v.Verify(plugin, after, diff, name => false).Verified); });
            test("V0.3 multiple luci-app normalized hint resolves unique candidate", () =>
            { var before = Snapshot("base-1.0"); var after = Snapshot("base-1.0\nluci-app-ssr-plus-1.0\nluci-app-other-1.0"); var plugin = Plugin("SSR-Plus_1_x86_64_all_sdk_25.12.run"); Equal("luci-app-ssr-plus", new PluginVerificationService().Verify(plugin, after, PackageSnapshotService.Compare(before, after), name => true).PackageName); });
            test("V0.3 remote temp rejects traversal root sibling and newline", () =>
            {
                var s = RemoteTempSession.Create(); RemoteTempSession.ValidateDirectory(s.Directory); True(s.CleanupCommand().Contains("rm -rf -- '" + s.Directory + "'"));
                foreach (string path in new[] { "/tmp", "/tmp/istore-plugin-helper/", s.Directory + "/..", s.Directory + "\n", "/tmp/istore-plugin-helper-other/" + s.Id, "/tmp/istore-plugin-helper/../../etc", s.Directory + "/package.run" }) Throws(() => RemoteTempSession.ValidateDirectory(path));
                True(s.CreateCommand().Contains("test ! -L")); True(PluginInstallCommands.Launch(s).Contains("launch.once")); Equal(false, PluginInstallCommands.Launch(s).Contains("kill"));
            });
            test("V0.3 package query refuses shell injection", () =>
            { Throws(() => PluginInstallCommands.Query(PackageManagerKind.APK, "pkg; rm -rf /")); True(PluginInstallCommands.Query(PackageManagerKind.APK, "luci-app-passwall").Contains("apk info -e")); True(PluginInstallCommands.Query(PackageManagerKind.OPKG, "luci-app-passwall").Contains("opkg status")); });
            test("V0.3 transfer trusts only an existing matching SSH fingerprint", () =>
            { var trust = new HostKeyTrustStore(); bool first; Equal(false, trust.IsTrusted("router", 22, "SHA256:good")); True(trust.TryTrust("router", 22, "SHA256:good", out first)); True(trust.IsTrusted("ROUTER", 22, "SHA256:good")); Equal(false, trust.IsTrusted("router", 22, "SHA256:changed")); Equal(false, trust.IsTrusted("other", 22, "SHA256:good")); });
            test("V0.3 independent query parses APK and installed OPKG status", () =>
            { True(PackageSnapshotService.QueryReportsInstalled(new ProbeResult { ExitStatus = 0, Output = "luci-app-passwall\n" }, PackageManagerKind.APK, "luci-app-passwall")); Equal(false, PackageSnapshotService.QueryReportsInstalled(new ProbeResult { ExitStatus = 1, Output = "" }, PackageManagerKind.APK, "luci-app-passwall")); True(PackageSnapshotService.QueryReportsInstalled(new ProbeResult { ExitStatus = 0, Output = "Package: luci-app-passwall\nStatus: install user installed\n" }, PackageManagerKind.OPKG, "luci-app-passwall")); Equal(false, PackageSnapshotService.QueryReportsInstalled(new ProbeResult { ExitStatus = 0, Output = "Package: luci-app-passwall\nStatus: deinstall ok not-installed\n" }, PackageManagerKind.OPKG, "luci-app-passwall")); });

            WithWorkflow(test, "SHA match installed main independently verified and cleaned", fake => { }, (r, f) =>
            { Equal(InstallStatus.SuccessVerified, r.Status); True(r.CleanupSucceeded); Equal(1, f.Starts); Equal("26.9.16-r1", r.Verification.Version); Equal(1, r.Diff.Added.Count); True(f.Queries.Contains("luci-app-passwall")); True(f.Queries.Contains("luci-app-openclash")); });
            WithWorkflow(test, "SHA mismatch stops before Makeself and installer", fake => fake.HashMismatch = true, (r, f) =>
            { Equal(InstallStatus.FailedIntegrity, r.Status); Equal(0, f.Inspections); Equal(0, f.Starts); Equal(1, f.Cleanups); });
            WithWorkflow(test, "remote size mismatch stops before SHA", fake => fake.SizeMismatch = true, (r, f) =>
            { Equal(InstallStatus.FailedIntegrity, r.Status); Equal(0, f.HashReads); Equal(0, f.Starts); True(r.CleanupSucceeded); });
            WithWorkflow(test, "missing sha256sum uses size and Makeself fallback", fake => fake.HashUnavailable = true, (r, f) =>
            { Equal(InstallStatus.SuccessVerified, r.Status); True(r.Warnings.Any(w => w.Contains("sha256sum"))); Equal(2, f.Inspections); });
            WithWorkflow(test, "Makeself check nonzero blocks launch", fake => fake.CheckResult = new ProbeResult { ExitStatus = 2, Error = "checksum mismatch" }, (r, f) =>
            { Equal(InstallStatus.FailedIntegrity, r.Status); Equal(0, f.Starts); True(r.CleanupSucceeded); });
            WithWorkflow(test, "Makeself zero without integrity evidence blocks launch", fake => fake.CheckResult = new ProbeResult { ExitStatus = 0, Output = "nothing checked" }, (r, f) =>
            { Equal(InstallStatus.FailedIntegrity, r.Status); Equal(0, f.Starts); });
            WithWorkflow(test, "Makeself conflicting architecture blocks launch", fake => fake.InfoText = Info().Replace("x86_64", "aarch64"), (r, f) =>
            { Equal(InstallStatus.FailedIntegrity, r.Status); Equal(0, f.Starts); Equal(1, f.Inspections); });
            WithWorkflow(test, "Makeself conflicting SDK blocks launch", fake => fake.InfoText = Info().Replace("25.12", "24.10"), (r, f) =>
            { Equal(InstallStatus.FailedIntegrity, r.Status); Equal(0, f.Starts); });
            WithWorkflow(test, "unknown info size and target warn with conservative gate", fake => fake.InfoText = "Identification: generic\nTarget directory: payload\n", (r, f) =>
            { Equal(InstallStatus.SuccessVerified, r.Status); True(r.Warnings.Any(w => w.Contains("3 倍"))); True(r.Warnings.Any(w => w.Contains("架构"))); });
            WithWorkflow(test, "insufficient initial tmp space never uploads", fake => fake.Spaces.Enqueue(1), (r, f) =>
            { Equal(InstallStatus.FailedPrecheck, r.Status); Equal(0, f.Uploads); Equal(0, f.Creates); });
            WithWorkflow(test, "insufficient unpack space cleans upload without installation", fake => { fake.Spaces.Enqueue(1024L * 1024 * 1024); fake.Spaces.Enqueue(1); }, (r, f) =>
            { Equal(0, f.Starts); True(r.CleanupSucceeded); Equal(1, f.Uploads); });
            WithWorkflow(test, "SFTP failure cleans created session never installs", fake => fake.UploadFailure = true, (r, f) =>
            { Equal(InstallStatus.FailedUpload, r.Status); Equal(0, f.Starts); Equal(1, f.Cleanups); });
            WithWorkflow(test, "create failure never deletes unowned existing directory", fake => fake.CreateFailure = true, (r, f) =>
            { Equal(InstallStatus.FailedPrecheck, r.Status); Equal(0, f.Cleanups); Equal(0, f.Uploads); });
            WithWorkflow(test, "exit zero missing Known main fails verification", fake => fake.After = Snapshot("base-1.0\nluci-app-openclash-0.47.156-r1"), (r, f) =>
            { Equal(InstallStatus.FailedVerification, r.Status); Equal(0, r.InstallerExitCode.Value); True(r.CleanupSucceeded); });
            WithWorkflow(test, "independent query false defeats snapshot main existence", fake => fake.QueryFailures.Add("luci-app-passwall"), (r, f) =>
            { Equal(InstallStatus.FailedVerification, r.Status); Equal(false, r.Verification.Verified); });
            WithWorkflow(test, "existing old version cannot falsely verify a failed update", fake =>
            { fake.Before.Packages.Add("luci-app-passwall", "26.9.15-r1"); fake.After.Packages["luci-app-passwall"] = "26.9.15-r1"; }, (r, f) =>
            { Equal(InstallStatus.FailedVerification, r.Status); True(r.Verification.Message.Contains("版本")); Equal(0, r.Diff.Updated.Count); });
            WithWorkflow(test, "installer exit failure never retries even if main exists", fake => fake.Exit = 3, (r, f) =>
            { Equal(InstallStatus.FailedInstaller, r.Status); Equal(1, f.Starts); True(r.Verification.Verified); True(r.CleanupSucceeded); });
            WithWorkflow(test, "cleanup failure leaves verified install success with warning", fake => fake.CleanupFailure = true, (r, f) =>
            { Equal(InstallStatus.SuccessVerified, r.Status); Equal(false, r.CleanupSucceeded); True(r.Warnings.Any(w => w.Contains("未确认清理"))); });
            WithWorkflow(test, "OpenClash disappearance fails verification without control command", fake => fake.After = Snapshot("base-1.0\nluci-app-passwall-26.9.16-r1"), (r, f) =>
            { Equal(InstallStatus.FailedVerification, r.Status); True(r.Warnings.Any(w => w.Contains("openclash"))); });
            WithWorkflow(test, "after snapshot failure still cleans ended installer", fake => fake.AfterSnapshotFailure = true, (r, f) =>
            { Equal(InstallStatus.FailedVerification, r.Status); True(r.InstallerEnded); True(r.CleanupSucceeded); });

            test("V0.3 preflight not Ready prevents upload", () => Fixture(PassWall, path =>
            {
                var fake = new FakeSession(); var result = Workflow(fake, path, SoftwareEnvironmentStatus.Failed);
                Equal(InstallStatus.FailedPrecheck, result.Status); Equal(0, fake.Uploads); Equal(0, fake.Creates);
            }));
            test("V0.3 incompatible filename stops before software environment", () => Fixture("PassWall_1_aarch64_all_sdk_25.12.run", path =>
            {
                int preflight = 0; var fake = new FakeSession();
                var r = Service().RunAsync(fake, Router(), path, () => { preflight++; return Task.FromResult(Ready()); }, yes => Task.FromResult(true), null, CancellationToken.None).GetAwaiter().GetResult();
                Equal(InstallStatus.FailedPrecheck, r.Status); Equal(0, preflight); Equal(0, fake.Uploads);
            }));
            test("V0.3 Already installed explicit confirmation required", () => Fixture(PassWall, path =>
            {
                var fake = new FakeSession(); fake.Before.Packages.Add("luci-app-passwall", "26.9.16-r1");
                var r = Workflow(fake, path, SoftwareEnvironmentStatus.Ready, summary => { Equal("luci-app-passwall", summary.ExistingPackage); return Task.FromResult(false); });
                Equal(InstallStatus.AlreadyInstalled, r.Status); Equal(0, fake.Starts); True(r.CleanupSucceeded);
            }));
            test("V0.3 cancel final confirmation cleans without launch", () => Fixture(PassWall, path =>
            { var fake = new FakeSession(); var r = Workflow(fake, path, SoftwareEnvironmentStatus.Ready, summary => Task.FromResult(false)); Equal(InstallStatus.Cancelled, r.Status); Equal(0, fake.Starts); True(r.CleanupSucceeded); }));
            test("V0.3 cancellation during upload stops and cleans", () => Fixture(PassWall, path =>
            { using (var cancel = new CancellationTokenSource()) { var fake = new FakeSession { UploadHook = () => cancel.Cancel() }; var r = Workflow(fake, path, SoftwareEnvironmentStatus.Ready, null, cancel.Token); Equal(InstallStatus.Cancelled, r.Status); Equal(0, fake.Starts); True(r.CleanupSucceeded); } }));
            test("V0.3 cancellation after launch cannot kill installer", () => Fixture(PassWall, path =>
            { using (var cancel = new CancellationTokenSource()) { var fake = new FakeSession { StartHook = () => cancel.Cancel() }; var r = Workflow(fake, path, SoftwareEnvironmentStatus.Ready, null, cancel.Token); Equal(InstallStatus.SuccessVerified, r.Status); Equal(1, fake.Starts); True(r.InstallerEnded); } }));
            test("V0.3 timeout still verifies and preserves active session", () => Fixture(PassWall, path =>
            { var fake = new FakeSession { NeverEnds = true }; var service = Service(); service.OverallLimit = TimeSpan.Zero; var r = Workflow(fake, path, SoftwareEnvironmentStatus.Ready, null, CancellationToken.None, service); Equal(InstallStatus.InstallerStateUnknown, r.Status); Equal(1, fake.Starts); Equal(0, fake.Cleanups); True(fake.Snapshots >= 3); }));
            test("V0.3 lost launch acknowledgement no retry no premature cleanup", () => Fixture(PassWall, path =>
            { var fake = new FakeSession { LaunchFailure = true }; var r = Workflow(fake, path); Equal(InstallStatus.InstallerStateUnknown, r.Status); Equal(1, fake.Starts); Equal(0, fake.Cleanups); }));
            test("V0.3 unknown plugin unique package verified", () => Fixture("New_1_x86_64_all_sdk_25.12.run", path =>
            { var fake = new FakeSession { After = Snapshot("base-1.0\nluci-app-openclash-0.47.156-r1\nluci-app-new-1.0-r1") }; var r = Workflow(fake, path); Equal(InstallStatus.SuccessVerified, r.Status); Equal("luci-app-new", r.Verification.PackageName); }));
            test("RC1 generic Momo install verifies despite dependency downgrade", () => Fixture("OpenWrt-momo_1.2.1_x86_64_all_sdk_25.12.run", path =>
            {
                var fake = new FakeSession { InfoText = Info().Replace("PassWall_26.9.16", "OpenWrt-momo_1.2.1"),
                    After = Snapshot("base-1.0\nluci-app-openclash-0.47.156-r1\nsing-box-1.13.16-r1\nluci-app-momo-1.2.1-r1") };
                fake.Before.Packages.Add("sing-box", "1.14.1-r1");
                var result = Workflow(fake, path);
                Equal(InstallStatus.SuccessVerified, result.Status); Equal("luci-app-momo", result.Verification.PackageName);
                Equal(false, result.Verification.StrongRule); Equal("1.2.1-r1", result.Verification.Version);
                Equal(1, result.Diff.Updated.Count); Equal("1.14.1-r1", result.Diff.Updated[0].BeforeVersion); Equal("1.13.16-r1", result.Diff.Updated[0].AfterVersion);
                Equal(0, result.Warnings.Count); True(result.CleanupSucceeded); Equal(1, fake.Starts);
                Equal(Utilities.UiTone.Success, Utilities.UiResultFormatter.Tone(result.Status));
            }));
            test("V0.3 unknown plugin ambiguous packages shows warning", () => Fixture("Unknown_1_x86_64_all_sdk_25.12.run", path =>
            { var fake = new FakeSession { After = Snapshot("base-1.0\nluci-app-openclash-0.47.156-r1\nluci-app-one-1.0\nluci-app-two-1.0") }; var r = Workflow(fake, path); Equal(InstallStatus.SuccessWithWarning, r.Status); True(r.CleanupSucceeded); }));
            test("V0.3 stdout stderr streaming preserves split UTF8 and redacts", () => Fixture(PassWall, path =>
            {
                var fake = new FakeSession(); byte[] bytes = Encoding.UTF8.GetBytes("安装中 test-secret\n");
                fake.Polls.Enqueue(new InstallerPoll { Stdout = bytes.Take(1).ToArray(), Stderr = new byte[0] });
                fake.Polls.Enqueue(new InstallerPoll { Stdout = bytes.Skip(1).Take(14).ToArray(), Stderr = new byte[0] });
                fake.Polls.Enqueue(new InstallerPoll { Stdout = bytes.Skip(15).ToArray(), Stderr = Encoding.UTF8.GetBytes("warning\n"), ExitCode = 0 });
                var lines = new List<string>(); var p = new InlineProgress(item => { if (item.Log != null) lines.Add(item.Log); });
                var r = Workflow(fake, path, SoftwareEnvironmentStatus.Ready, null, CancellationToken.None, null, p);
                Equal(InstallStatus.SuccessVerified, r.Status); True(lines.Any(line => line.Contains("[stdout] 安装中"))); True(lines.Any(line => line.Contains("[stderr] warning"))); Equal(false, lines.Any(line => line.Contains("test-secret")));
            }));
            test("V0.3 file stays local and write locked until workflow ends", () => Fixture(PassWall, path =>
            {
                var fake = new FakeSession(); bool denied = false;
                var r = Workflow(fake, path, SoftwareEnvironmentStatus.Ready, summary => { try { File.WriteAllText(path, "mutated"); } catch (IOException) { denied = true; } return Task.FromResult(false); });
                True(denied); True(File.Exists(path)); Equal("abc", File.ReadAllText(path)); Equal(AbcHash, r.Sha256);
            }));
            test("V0.3 current file bytes rehashed instead of cached selection", () => Fixture(PassWall, path =>
            { var selected = new PluginPackageAnalyzer().AnalyzeFile(path); File.WriteAllText(path, "changed bytes"); var fake = new FakeSession(); var r = Workflow(fake, selected.FilePath); Equal((long)Encoding.UTF8.GetByteCount("changed bytes"), r.Plugin.FileSize); Equal(fake.UploadedHash, r.Sha256); Equal(false, r.Sha256 == AbcHash); }));
            test("V0.3 UI busy prevents second install and closing active installer", () =>
            {
                using (var form = new MainForm())
                {
                    Set(form, "router", Router()); var plugin = Plugin(PassWall); plugin.CompatibilityStatus = CompatibilityStatus.Compatible; Set(form, "plugin", plugin);
                    Invoke(form, "RefreshInstallButton"); True(form.Controls.Find("installButton", true)[0].Enabled);
                    Set(form, "isInstallBusy", true); Invoke(form, "RefreshInstallButton"); Equal(false, form.Controls.Find("installButton", true)[0].Enabled);
                    Set(form, "installerDispatched", true); var args = new FormClosingEventArgs(CloseReason.UserClosing, false);
                    form.GetType().GetMethod("OnFormClosing", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { args }); True(args.Cancel);
                }
            });
            test("V0.3 confirmation existing install starts disabled", () =>
            { using (var form = new InstallConfirmationForm(new InstallConfirmation { Plugin = Plugin(PassWall), Router = Router(), SoftwareEnvironment = Ready(), Sha256 = AbcHash, ExistingPackage = "luci-app-passwall", ExistingVersion = "26.9.16-r1" })) Equal(false, form.Controls.Find("confirmInstallButton", true)[0].Enabled); });
        }
        private static void WithWorkflow(Action<string, Action> test, string name, Action<FakeSession> setup, Action<PluginInstallResult, FakeSession> check)
        { test("V0.3 " + name, () => Fixture(PassWall, path => { var fake = new FakeSession(); setup(fake); var result = Workflow(fake, path); check(result, fake); })); }
        private static void Fixture(string name, Action<string> body)
        {
            string directory = Path.Combine(Path.GetTempPath(), "istore-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, name);
            try { File.WriteAllText(path, "abc", new UTF8Encoding(false)); body(path); }
            finally { File.Delete(path); Directory.Delete(directory); }
        }
        private static PluginInstallationService Service() { return new PluginInstallationService { PollInterval = TimeSpan.FromMilliseconds(1) }; }
        private static PluginInstallResult Workflow(FakeSession fake, string path, SoftwareEnvironmentStatus state = SoftwareEnvironmentStatus.Ready,
            Func<InstallConfirmation, Task<bool>> confirm = null, CancellationToken token = default(CancellationToken), PluginInstallationService service = null, IProgress<InstallProgress> progress = null)
        { return (service ?? Service()).RunAsync(fake, Router(), path, () => Task.FromResult(new SoftwareEnvironmentResult { Status = state }), confirm ?? (summary => Task.FromResult(true)), progress, token).GetAwaiter().GetResult(); }
        private static RouterEnvironment Router() { return new RouterEnvironment { IsConnected = true, Architecture = "x86_64", SdkSeries = "25.12", Version = "25.12.5", PackageManager = PackageManagerKind.APK }; }
        private static PluginPackageInfo Plugin(string name) { return new PluginPackageAnalyzer().AnalyzeFileName(name); }
        private static PackageSnapshot Snapshot(string text) { return PackageSnapshotService.Parse(text, PackageManagerKind.APK); }
        private static SoftwareEnvironmentResult Ready() { return new SoftwareEnvironmentResult { Status = SoftwareEnvironmentStatus.Ready }; }
        private static string Info() { return "Identification: PassWall_26.9.16_with_sdk_25.12\nTarget directory: x86_64\nUncompressed size: 81680 KB\nCompression: gzip\nBuilt with Makeself version 2.7.2\n"; }
        private static ProbeResult Check() { return new ProbeResult { ExitStatus = 0, Output = "Verifying archive integrity...\nMD5 checksums are OK.\nAll good.\n", Error = "does not contain an embedded SHA256 checksum" }; }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
        private static void Throws(Action body) { bool threw = false; try { body(); } catch (ArgumentException) { threw = true; } catch (InvalidDataException) { threw = true; } True(threw); }
        private static void Set(MainForm form, string name, object value) { form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, value); }
        private static void Invoke(MainForm form, string method) { form.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null); }
        private sealed class InlineProgress : IProgress<InstallProgress>
        { private readonly Action<InstallProgress> report; public InlineProgress(Action<InstallProgress> report) { this.report = report; } public void Report(InstallProgress value) { report(value); } }
        private sealed class FakeSession : IPluginInstallSession
        {
            public PackageSnapshot Before = InstallationTests.Snapshot("base-1.0\nluci-app-openclash-0.47.156-r1");
            public PackageSnapshot After = InstallationTests.Snapshot("base-1.0\nluci-app-openclash-0.47.156-r1\nluci-app-passwall-26.9.16-r1");
            public readonly Queue<long> Spaces = new Queue<long>();
            public readonly Queue<InstallerPoll> Polls = new Queue<InstallerPoll>();
            public readonly HashSet<string> QueryFailures = new HashSet<string>();
            public readonly List<string> Queries = new List<string>();
            public bool HashMismatch, HashUnavailable, SizeMismatch, UploadFailure, CreateFailure, CleanupFailure, AfterSnapshotFailure, NeverEnds, LaunchFailure;
            public int Creates, Uploads, HashReads, Inspections, Starts, Snapshots, Cleanups, Exit;
            public long UploadedSize;
            public string UploadedHash, InfoText = Info();
            public ProbeResult CheckResult = Check();
            public Action UploadHook, StartHook;
            public string Sanitize(string text) { return (text ?? "").Replace("test-secret", "[已隐藏]"); }
            public long TmpAvailable() { return Spaces.Count > 0 ? Spaces.Dequeue() : 1024L * 1024 * 1024; }
            public void CreateDirectory(RemoteTempSession s) { Creates++; if (CreateFailure) throw new IOException("Directory already exists."); }
            public void Upload(Stream source, RemoteTempSession s, Action<long> progress, CancellationToken token)
            { Uploads++; if (UploadFailure) throw new IOException("SFTP subsystem unavailable"); UploadedSize = source.Length; UploadedHash = PluginPackageInspection.ComputeSha256(source); progress(source.Length); if (UploadHook != null) UploadHook(); token.ThrowIfCancellationRequested(); }
            public long RemoteFileSize(RemoteTempSession s) { return UploadedSize + (SizeMismatch ? 1 : 0); }
            public ProbeResult RemoteHash(RemoteTempSession s) { HashReads++; return new ProbeResult { ExitStatus = HashUnavailable ? 127 : 0, Output = HashMismatch ? new string('0', 64) : UploadedHash }; }
            public ProbeResult Inspect(RemoteTempSession s, bool check) { Inspections++; return check ? CheckResult : new ProbeResult { ExitStatus = 0, Output = InfoText }; }
            public PackageSnapshot Snapshot(PackageManagerKind manager) { Snapshots++; if (Starts > 0 && AfterSnapshotFailure) throw new IOException("database unavailable"); return Starts > 0 ? After : Before; }
            public bool PackageExists(PackageManagerKind manager, string name) { Queries.Add(name); return !QueryFailures.Contains(name) && (Starts > 0 ? After : Before).Packages.ContainsKey(name); }
            public void CheckRunner() { }
            public void StartInstaller(RemoteTempSession s) { Starts++; if (StartHook != null) StartHook(); if (LaunchFailure) throw new IOException("lost acknowledgement"); }
            public InstallerPoll PollInstaller(RemoteTempSession s, long stdoutOffset, long stderrOffset)
            { return Polls.Count > 0 ? Polls.Dequeue() : new InstallerPoll { Stdout = new byte[0], Stderr = new byte[0], ExitCode = NeverEnds ? (int?)null : Exit }; }
            public bool Cleanup(RemoteTempSession s) { Cleanups++; return !CleanupFailure; }
        }
    }
}
