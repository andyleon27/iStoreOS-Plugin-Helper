using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static partial class WorkflowTests
    {
        private const string Name = "PassWall_26.9.16_x86_64_all_sdk_25.12.run";
        private const string Compat = "https://compat.test/repo/packages.adb";
        private static string FailureUrl(string host) { return "ERROR: fetching https://" + host + "/openwrt/releases/25.12/packages/x86_64/base/packages.adb: temporary error (try again later)"; }
        public static void Run(Action<string, Action> test)
        {
            SourceTests(test);
            PerformanceTests(test);
            foreach (bool arm in new[] { false, true })
            {
                bool useArm = arm;
                string file = useArm ? "PassWall_26.9.16_aarch64_all_sdk_24.10.run" : Name;
                test("r2 full shared SFTP polling reconnect without redispatch " + (useArm ? "aarch64/OPKG" : "x86_64/APK"), () => Fixture(file, path =>
                {
                    var fake = new Fake { UseSftpPolling = true, DisconnectAfterFirstSftpPoll = true };
                    if (useArm)
                    {
                        fake.MakeOpkg(); fake.RouterArchitecture = "aarch64_cortex-a53"; fake.ArchiveArchitecture = "aarch64"; fake.RouterSdk = "24.10"; fake.RouterVersion = "24.10.8";
                        fake.Rom = "src/gz openwrt_base https://downloads.test/releases/24.10.8/packages/aarch64_cortex-a53/base\n";
                        fake.Original = "# preserve ARM OPKG source\n" + SoftwareSourceService.RebaseRom(fake.Rom, "https://old.test/openwrt/"); fake.Current = fake.Original;
                    }
                    fake.Runs.Enqueue(new Outcome { Log = new string('x', 70000) });
                    var result = Run(Service(), fake, path); Equal(InstallationState.Success, result.State); Equal(1, fake.Starts); Equal(1, fake.Reconnects);
                    True(fake.SftpFiles.Creates >= 2); Equal(fake.SftpFiles.Creates, fake.SftpFiles.Disposals); True(fake.SftpFiles.Seeks["stdout.log"] >= 65536);
                    True(fake.LastLaunch.Contains("launch.once")); True(fake.LastCleanup.Contains(result.Installation.RemoteDirectory)); True(result.Installation.CleanupSucceeded);
                    Equal(false, System.Text.RegularExpressions.Regex.IsMatch(fake.LastLaunch, @"\b(stat|nohup|base64|dd)\b"));
                }));
            }
            foreach (PackageManagerKind kind in new[] { PackageManagerKind.OPKG, PackageManagerKind.APK })
            {
                PackageManagerKind manager = kind;
                test("V1.0.2 r1 " + manager + " runner capability failure preserves source restoration and no dispatch", () => Fixture(Name, path =>
                {
                    var fake = new Fake { RunnerFailure = true }; if (manager == PackageManagerKind.OPKG) fake.MakeOpkg(); fake.BadHosts.Add("old.test");
                    var result = Run(Service(), fake, path); Equal(InstallationFailureKind.Tool, result.Failure.Kind);
                    Equal(0, fake.Starts); True(result.SourceRestored); Equal(fake.Original, fake.Current); True(result.Installation.CleanupSucceeded);
                    True(fake.Events.IndexOf("restore") < fake.Events.IndexOf("cleanup"));
                }));
                test("V1.0.2 installation " + manager + " false POST acknowledgement verified state still installs and restores", () => Fixture(Name, path =>
                {
                    var fake = new Fake { PostNotAcknowledged = true }; if (manager == PackageManagerKind.OPKG) fake.MakeOpkg(); fake.BadHosts.Add("old.test");
                    var result = Run(Service(), fake, path); Equal(InstallationState.Success, result.State); Equal(1, fake.Starts);
                    Equal(1, fake.UpdateCalls); Equal("A", string.Join(",", fake.Switches)); True(result.SourceRestored); Equal(fake.Original, fake.Current);
                }));
                test("V1.0.2 installation " + manager + " false acknowledgement wrong actual identity still blocks", () => Fixture(Name, path =>
                {
                    var fake = new Fake { PostNotAcknowledged = true, SwitchKeepsOldIdentity = true }; if (manager == PackageManagerKind.OPKG) fake.MakeOpkg(); fake.BadHosts.Add("old.test");
                    var result = Run(Service(), fake, path); Equal(InstallationFailureKind.Tool, result.Failure.Kind); Equal(0, fake.Starts);
                    Equal(0, fake.UpdateCalls); True(result.SourceRestored); Equal(fake.Original, fake.Current);
                }));
                test("V1.0.2 installation " + manager + " false acknowledgement wrong actual feeds still blocks", () => Fixture(Name, path =>
                {
                    var fake = new Fake { PostNotAcknowledged = true, SwitchKeepsOldFeeds = true }; if (manager == PackageManagerKind.OPKG) fake.MakeOpkg(); fake.BadHosts.Add("old.test");
                    var result = Run(Service(), fake, path); Equal(InstallationFailureKind.Tool, result.Failure.Kind); Equal(0, fake.Starts);
                    Equal(0, fake.UpdateCalls); True(result.SourceRestored); Equal(fake.Original, fake.Current);
                }));
            }
            Case(test, "healthy current source no switch", f => { }, (r, f) => { Equal(InstallationState.Success, r.State); Equal(0, f.Switches.Count); Equal(0, f.Backups); Equal(1, f.Starts); True(r.Installation.CleanupSucceeded); });
            Case(test, "transient current canary failure is cached and fallback restores exact text", f => f.TransientFailures = 1, (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, f.Switches.Count); Equal(1, f.Backups); Equal(f.Original, f.Current); Equal(1, f.Starts); True(r.SourceRestored); Equal(1, f.ProbeUrls.Count(u => new Uri(u).Host == "old.test")); });
            Case(test, "failed current probe uses FULL fallback and restores exact original", f => f.BadHosts.Add("old.test"), (r, f) => { Equal(InstallationState.Success, r.State); Equal("A", f.Switches.Single()); True(r.SourceRestored); Equal(f.Original, f.Current); True(f.Events.IndexOf("restore") < f.Events.IndexOf("cleanup")); });
            Case(test, "first package update failure proceeds to second source", f => f.Updates.Enqueue(new PackageUpdateResult { ExitCode = 1, Stderr = FailureUrl("old.test") }), (r, f) => { Equal(InstallationState.Success, r.State); Equal(2, f.UpdateCalls); Equal(1, f.Starts); Equal("A", f.Switches.Single()); });
            Case(test, "explicit official download failure permits next run", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl("old.test") }), (r, f) => { Equal(InstallationState.Success, r.State); Equal(2, f.Starts); Equal(2, r.Attempts.Count); True(r.SourceRestored); });
            Case(test, "script error never rotates", f => f.Runs.Enqueue(new Outcome { Exit = 2, Log = "install.sh: syntax error: unexpected token" }), (r, f) => { Equal(InstallationFailureKind.InstallerScript, r.Failure.Kind); Equal(1, f.Starts); Equal(0, f.Switches.Count); });
            Case(test, "unknown nonzero never rotates", f => f.Runs.Enqueue(new Outcome { Exit = 2, Log = "some unknown failure" }), (r, f) => { Equal(InstallationFailureKind.UnknownInstaller, r.Failure.Kind); Equal(1, f.Starts); Equal(0, f.Switches.Count); });
            Case(test, "partial diff with official failure resumes without uninstall", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl("old.test"), Partial = true }), (r, f) => { Equal(2, f.Starts); True(r.Attempts[0].Result.Diff.Added.Any(p => p.Name == "dependency")); True(r.Attempts[1].Result.BeforePackages.Packages.ContainsKey("dependency")); Equal(InstallationState.Success, r.State); });
            Case(test, "partial diff with script failure stops", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = "syntax error", Partial = true }), (r, f) => { Equal(1, f.Starts); True(r.Installation.Diff.Added.Any(p => p.Name == "dependency")); Equal(InstallationFailureKind.InstallerScript, r.Failure.Kind); });
            Case(test, "all FULL mirrors exhausted has hard ceiling", f => { foreach (string host in new[] { "old.test", "a.test", "b.test" }) f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl(host) }); }, (r, f) => { Equal(InstallationState.RetryableFailure, r.State); Equal(InstallationFailureKind.MirrorsExhausted, r.Failure.Kind); Equal(3, f.Starts); Equal(2, f.Switches.Count); True(r.Failure.UserMessage.Contains("30～60")); True(r.SourceRestored); });
            test("V0.3.2 deliberate retry refreshes source and readiness", () => Fixture(Name, path => {
                var f = new Fake(); var service = Service(); f.BadHosts.UnionWith(new[] { "old.test", "a.test", "b.test" });
                var first = Run(service, f, path); Equal(InstallationFailureKind.MirrorsExhausted, first.Failure.Kind); int probes = f.Probes;
                f.BadHosts.Clear(); var second = Run(service, f, path); Equal(InstallationState.Success, second.State); Equal(1, f.CandidateReads); Equal(2, f.ReadinessReads); True(f.Probes > probes);
            }));
            Case(test, "SSH disconnected before click automatically reconnects", f => f.Connected = false, (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, f.Reconnects); });
            Case(test, "SSH lost during poll reconnects to same runner", f => f.DisconnectPoll = true, (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, f.Reconnects); Equal(1, f.Starts); True(r.States.Contains(InstallationState.ReconnectChecking)); });
            Case(test, "lost launch acknowledgement resumes same launch marker", f => f.LostAcknowledgement = true, (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, f.Starts); });
            test("V0.3.2 retry verifies completed main without rerunning", () => Fixture(Name, path => {
                var f = new Fake(); var service = Service(); f.Readiness.PersistentBytes = 1; Equal(InstallationFailureKind.NoSpace, Run(service, f, path).Failure.Kind);
                f.Readiness.PersistentBytes = 1024 * 1024 * 100; f.Packages.Packages["luci-app-passwall"] = "26.9.16-r1";
                var result = Run(service, f, path); Equal(InstallationState.Success, result.State); Equal(0, f.Starts); Equal(0, f.CandidateReads); // Failed preflight and healthy-current retry both avoid the lazy candidate endpoint.
            }));
            Case(test, "remote run disappears reupload then rehash", f => f.MissingBeforeDispatch = true, (r, f) => { Equal(InstallationState.Success, r.State); Equal(2, f.Uploads); True(f.HashReads >= 2); Equal(1, f.Starts); });
            Case(test, "success restores exact original comments and hash", f => f.BadHosts.Add("old.test"), (r, f) => { True(r.SourceRestored); Equal(f.Original, f.Current); Equal(1, f.Restores); True(r.OriginalHash.Length == 64); });
            Case(test, "failure also restores official source", f => { f.BadHosts.Add("old.test"); f.Runs.Enqueue(new Outcome { Exit = 2, Log = "syntax error" }); }, (r, f) => { Equal(InstallationFailureKind.InstallerScript, r.Failure.Kind); True(r.SourceRestored); Equal(f.Original, f.Current); });
            Case(test, "restore warning preserves verified installed success", f => { f.BadHosts.Add("old.test"); f.RestoreFailure = true; }, (r, f) => { Equal(InstallationState.Success, r.State); True(r.Installation.Verification.Verified); Equal(false, r.SourceRestored); True(r.Warnings.Any(w => w.Contains("原软件源恢复失败"))); Equal(0, f.BackupDeletes); });
            Case(test, "compat failure never rotates official mirrors", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = "ERROR: fetching " + Compat + ": temporary error" }), (r, f) => { Equal(InstallationFailureKind.AdditionalRepository, r.Failure.Kind); Equal(0, f.Switches.Count); Equal(1, f.Starts); });
            Case(test, "custom update failure never rotates", f => f.Updates.Enqueue(new PackageUpdateResult { ExitCode = 1, Stderr = "ERROR: fetching https://custom.test/repo/packages.adb: temporary error" }), (r, f) => { Equal(0, f.Starts); Equal(0, f.Switches.Count); Equal(InstallationFailureKind.AdditionalRepository, r.Failure.Kind); });
            Case(test, "external plugin repository never rotates", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = "failed to download package https://plugin.test/file.apk: connection timeout" }), (r, f) => { Equal(InstallationFailureKind.ExternalRepository, r.Failure.Kind); Equal(0, f.Switches.Count); });
            Case(test, "tmp shortage no run no upload", f => f.Readiness.TmpBytes = 1, (r, f) => { Equal(InstallationFailureKind.NoSpace, r.Failure.Kind); Equal(0, f.Starts); Equal(0, f.Uploads); });
            Case(test, "persistent shortage no run", f => f.Readiness.PersistentBytes = 1, (r, f) => { Equal(InstallationFailureKind.NoSpace, r.Failure.Kind); Equal(0, f.Starts); });
            Case(test, "busy manager finite wait and no dispatch", f => f.Busy = true, (r, f) => { Equal(InstallationFailureKind.PackageManagerBusy, r.Failure.Kind); Equal(3, f.BusyReads); Equal(0, f.Starts); });
            Case(test, "busy manager clears during finite wait", f => f.BusyRemaining = 2, (r, f) => { Equal(InstallationState.Success, r.State); True(f.BusyReads >= 3); });
            Case(test, "integrity mismatch no run", f => f.HashMismatch = true, (r, f) => { Equal(InstallationFailureKind.Integrity, r.Failure.Kind); Equal(0, f.Starts); Equal(0, f.Switches.Count); });
            Case(test, "Makeself failure separately classified", f => f.MakeselfBad = true, (r, f) => { Equal(InstallationFailureKind.Makeself, r.Failure.Kind); Equal(0, f.Starts); });
            test("V0.3.2 incompatible SDK arch blocks before probing", () => Fixture("PassWall_26.9.16_aarch64_all_sdk_24.10.run", path => { var f = new Fake(); var r = Run(Service(), f, path); Equal(InstallationFailureKind.Compatibility, r.Failure.Kind); Equal(0, f.Starts); Equal(0, f.Probes); }));
            Case(test, "offline does not rotate mirrors", f => f.Readiness.DefaultRoute = false, (r, f) => { Equal(InstallationFailureKind.Offline, r.Failure.Kind); Equal(0, f.Probes); Equal(0, f.Switches.Count); });
            Case(test, "widespread DNS failure stops before probing", f => f.Readiness.DnsResolved = 0, (r, f) => { Equal(InstallationFailureKind.DnsFailure, r.Failure.Kind); Equal(0, f.Starts); Equal(0, f.Probes); });
            Case(test, "gross clock anomaly stops without setting time", f => f.Readiness.UnixTime = 1, (r, f) => { Equal(InstallationFailureKind.TimeInvalid, r.Failure.Kind); Equal(0, f.Starts); });
            Case(test, "minutes of clock drift allowed", f => f.Readiness.UnixTime -= 300, (r, f) => Equal(InstallationState.Success, r.State));
            Case(test, "readonly filesystem stops", f => f.Readiness.PersistentReadOnly = true, (r, f) => { Equal(InstallationFailureKind.ReadOnlyFileSystem, r.Failure.Kind); Equal(0, f.Starts); });
            Case(test, "identity change refuses reconnect", f => { f.Connected = false; f.ChangedIdentity = true; }, (r, f) => { Equal(InstallationFailureKind.IdentityChanged, r.Failure.Kind); Equal(0, f.Starts); Equal(1, f.Reconnects); });
            Case(test, "SSH unavailable respects finite reconnect ceiling", f => { f.Connected = false; f.ConnectFailure = true; }, (r, f) => { Equal(InstallationFailureKind.ConnectionLost, r.Failure.Kind); Equal(3, f.Reconnects); Equal(0, f.Starts); });
            Case(test, "installer no space beats simultaneous download error", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl("old.test") + "\nNo space left on device" }), (r, f) => { Equal(InstallationFailureKind.NoSpace, r.Failure.Kind); Equal(0, f.Switches.Count); });
            Case(test, "conflict never retries", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = "package conflicts with installed package" }), (r, f) => { Equal(InstallationFailureKind.PackageConflict, r.Failure.Kind); Equal(1, f.Starts); });
            Case(test, "kernel ABI never retries", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = "kernel ABI mismatch" }), (r, f) => { Equal(InstallationFailureKind.KernelAbi, r.Failure.Kind); Equal(1, f.Starts); });
            Case(test, "post install error does not claim corrupt package", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = "post-install script returned 2" }), (r, f) => { Equal(InstallationFailureKind.InstallerScript, r.Failure.Kind); Equal(false, r.Failure.UserMessage.Contains("损坏")); });
            Case(test, "network unreachable stops mirror churn", f => f.Runs.Enqueue(new Outcome { Exit = 1, Log = "failed to download https://old.test/openwrt/releases/25.12/packages/x86_64/base/a.apk: Network is unreachable" }), (r, f) => { Equal(InstallationFailureKind.Offline, r.Failure.Kind); Equal(0, f.Switches.Count); Equal(1, f.Starts); });
            Case(test, "unknown update is retryable but cannot rotate", f => f.Updates.Enqueue(new PackageUpdateResult { ExitCode = 1, Stderr = "unknown database error" }), (r, f) => { Equal(InstallationFailureKind.UpdateFailure, r.Failure.Kind); Equal(0, f.Starts); Equal(0, f.Switches.Count); });
            Case(test, "hash changes after initial check still blocks dispatch", f => f.CorruptBeforeDispatch = true, (r, f) => { Equal(InstallationFailureKind.Integrity, r.Failure.Kind); Equal(0, f.Starts); Equal(false, r.PendingInstaller); True(r.Installation.CleanupSucceeded); });
            Case(test, "busy immediately before dispatch blocks legacy premarked launch", f => f.BusyAfterCheck = true, (r, f) => { Equal(InstallationFailureKind.PackageManagerBusy, r.Failure.Kind); Equal(0, f.Starts); Equal(false, r.PendingInstaller); True(r.Installation.CleanupSucceeded); });
            Case(test, "missing router hash allows initial fallback but never automatic rerun", f => { f.HashUnavailable = true; f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl("old.test") }); }, (r, f) => { Equal(1, f.Starts); Equal(0, f.Switches.Count); });
            Case(test, "duplicate mirrors cannot increase attempt ceiling", f => { f.DuplicateCandidates = true; foreach (string host in new[] { "old.test", "a.test", "b.test" }) f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl(host) }); }, (r, f) => { Equal(3, f.Starts); Equal(2, f.Switches.Count); Equal(InstallationFailureKind.MirrorsExhausted, r.Failure.Kind); });
            test("V0.3.2 failed restoration cannot become next original baseline", () => Fixture(Name, path => {
                var f = new Fake { RestoreFailure = true }; f.BadHosts.Add("old.test"); var service = Service(); Equal(InstallationState.Success, Run(service, f, path).State); int starts = f.Starts;
                var second = Run(service, f, path); Equal(InstallationFailureKind.Tool, second.Failure.Kind); Equal(starts, f.Starts); True(second.Warnings.Any(w => w.Contains("恢复失败")));
                True(service.HasPendingWork); var third = Run(service, f, path); Equal(InstallationFailureKind.Tool, third.Failure.Kind); Equal(starts, f.Starts); True(service.HasPendingWork);
            }));
            test("V0.3.2 pending official failure ends before deliberate retry resumes", () => Fixture(Name, path => {
                var f = new Fake { NeverEnds = true }; f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl("old.test"), Partial = true }); var service = Service(); service.Engine.OverallLimit = TimeSpan.Zero;
                True(Run(service, f, path).PendingInstaller); f.NeverEnds = false; service.Engine.OverallLimit = TimeSpan.FromSeconds(1);
                var second = Run(service, f, path); Equal(InstallationState.Success, second.State); Equal(2, f.Starts); True(f.Packages.Packages.ContainsKey("dependency"));
            }));
            test("V0.3.2 cancel before dispatch restores independently", () => Fixture(Name, path => {
                var f = new Fake(); f.BadHosts.Add("old.test"); using (var token = new CancellationTokenSource()) { f.AfterSwitch = () => token.Cancel(); var r = Run(Service(), f, path, token.Token); Equal(InstallationFailureKind.Cancelled, r.Failure.Kind); True(r.SourceRestored); Equal(0, f.Starts); }
            }));
            test("V0.3.2 cancellation after real dispatch cannot stop installer", () => Fixture(Name, path => {
                var f = new Fake(); using (var token = new CancellationTokenSource()) { f.AfterStart = () => token.Cancel(); var r = Run(Service(), f, path, token.Token); Equal(InstallationState.Success, r.State); Equal(1, f.Starts); }
            }));
            test("V0.3.2 pending runner blocks overlapping run and retries reconcile", () => Fixture(Name, path => {
                var f = new Fake { NeverEnds = true }; var service = Service(); service.Engine.OverallLimit = TimeSpan.Zero;
                var first = Run(service, f, path); True(first.PendingInstaller); Equal(0, f.Cleanups);
                var second = Run(service, f, path); True(second.PendingInstaller); Equal(1, f.Starts); Equal(0, f.Cleanups);
                f.NeverEnds = false; service.Engine.OverallLimit = TimeSpan.FromSeconds(1);
                var third = Run(service, f, path); Equal(InstallationState.Success, third.State); Equal(1, f.Starts); True(third.Installation.CleanupSucceeded);
            }));
            test("V0.3.2 all errors give user a next action", () => {
                foreach (InstallationFailureKind kind in Enum.GetValues(typeof(InstallationFailureKind))) { if (kind == InstallationFailureKind.None) continue; var message = InstallFailureClassifier.Failure(kind).UserMessage; True(message.Contains("请") || message.Contains("建议") || message.Contains("可以")); }
            });
            test("V0.3.2 URL boundary rejects lookalike host and unrelated timeout", () => {
                var f = new Fake(); var official = Feeds(f.Original); Equal(false, InstallFailureClassifier.Classify("failed to download https://old.test.attacker/repo/a.apk", official, null).CanRotateOfficial);
                Equal(false, InstallFailureClassifier.Classify("fetch https://old.test/openwrt/releases/25.12/packages/x86_64/base/packages.adb\ninstallation finished\nservice timeout", official, null).CanRotateOfficial);
            });
            test("V0.3.2 commands never rollback kill or proxy-control", () => {
                var text = InstallationReadiness.Busy + InstallationReadiness.RestoreCommand(PackageManagerKind.APK, "# exact original\n") + InstallationReadiness.Routes + InstallationReadiness.DnsCommand("old.test");
                foreach (var forbidden in new[] { "kill ", "apk del", "opkg remove", "uci ", "/etc/init.d/", "--force" }) Equal(false, text.Contains(forbidden)); True(InstallationReadiness.HasRoute("default via 192.168.1.1 dev eth0")); Equal(false, InstallationReadiness.HasRoute("default dev lo"));
            });
            test("V0.3.2 advanced details collapsed with one log action", () => {
                using (var form = new MainForm()) { form.Show(); Application.DoEvents(); Equal(false, UiTests.Field<AdvancedInfoForm>(form, "advancedInfoForm").Visible); Equal(false, UiTests.Control(form, "softwareButton").Visible); Equal(false, UiTests.Control(form, "saveLogButton").Visible); Equal(false, UiTests.Control(form, "cancelInstallButton").Visible);
                    UiTests.Invoke(form, "ToggleLogButton_Click", null, EventArgs.Empty); True(UiTests.Control(form, "saveLogButton").Visible); Equal(false, UiTests.Control(form, "saveSoftwareLogButton").Visible); Equal(false, UiTests.Control(form, "saveInstallLogButton").Visible); form.Close(); }
            });
            test("V0.3.2 retry label running gate and force cancellation", () => {
                using (var form = new MainForm()) { var plugin = new PluginPackageAnalyzer().AnalyzeFileName(Name); plugin.CompatibilityStatus = CompatibilityStatus.Compatible; UiTests.Set(form, "plugin", plugin); UiTests.Set(form, "router", Router());
                    UiTests.Invoke(form, "DisplayWorkflowResult", new InstallationWorkflowResult { State = InstallationState.RetryableFailure, Failure = InstallFailureClassifier.Failure(InstallationFailureKind.MirrorsExhausted) }); Equal("再次尝试", UiTests.Control(form, "installButton").Text); True(UiTests.Control(form, "installButton").Enabled);
                    UiTests.Set(form, "isInstallBusy", true); UiTests.Set(form, "installerDispatched", true); UiTests.Invoke(form, "RefreshInstallButton"); Equal("正在安装…", UiTests.Control(form, "installButton").Text); Equal(false, UiTests.Control(form, "installButton").Enabled); Equal(false, UiTests.Control(form, "cancelInstallButton").Enabled); }
            });
            test("V0.3.2 minimum window keeps password eye inside input and advanced action visible", () => {
                using (var form = new MainForm()) { form.Size = form.MinimumSize; form.Show(); Application.DoEvents();
                    var eye = UiTests.Control(form, "passwordToggleButton"); True(eye.Parent.ClientRectangle.Contains(eye.Bounds)); True(eye.Parent.Parent.ClientRectangle.Contains(eye.Parent.Bounds));
                    var toggle = UiTests.Control(form, "toggleLogButton"); True(toggle.Parent.ClientRectangle.Contains(toggle.Bounds)); form.Close(); }
            });
            Case(test, "success follows restoration and cleanup in state history", f => f.BadHosts.Add("old.test"), (r, f) => { Equal(InstallationState.Success, r.States.Last()); True(r.States.IndexOf(InstallationState.RestoringSource) < r.States.IndexOf(InstallationState.Cleaning)); True(r.States.IndexOf(InstallationState.Cleaning) < r.States.IndexOf(InstallationState.Success)); });
            Case(test, "OPKG current source path runs same orchestration", f => f.MakeOpkg(), (r, f) => { Equal(InstallationState.Success, r.State); Equal(PackageManagerKind.OPKG, r.Router.PackageManager); Equal(1, f.Starts); Equal(0, f.Switches.Count); });
            Case(test, "OPKG download retry restores distfeeds conf", f => { f.MakeOpkg(); f.Runs.Enqueue(new Outcome { Exit = 1, Log = "opkg_download: Failed to download https://old.test/openwrt/releases/24.10/packages/x86_64/base/dependency.ipk, wget returned 4." }); }, (r, f) => { Equal(InstallationState.Success, r.State); Equal(2, f.Starts); True(r.SourceRestored); Equal(f.Original, f.Current); });
            Case(test, "current custom official text cannot repeat same mirror alias", f => { f.Original += "https://old.test/openwrt/releases/25.12/packages/x86_64/extra/packages.adb\n"; f.Current = f.Original; f.DuplicateCandidates = true; foreach (string host in new[] { "old.test", "a.test", "b.test" }) f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl(host) }); }, (r, f) => { Equal(3, f.Starts); Equal(2, f.Switches.Count); Equal(f.Original, f.Current); });
            test("V0.3.2 progress shows backup action without mirror identity or attempt counters", () => Fixture(Name, path => {
                var f = new Fake(); f.BadHosts.Add("old.test"); var messages = new List<string>(); var service = Service();
                var result = Task.Run(() => service.RunAsync(f, Router(), path, new InlineProgress<InstallationWorkflowProgress>(p => messages.Add(p.Message)), CancellationToken.None)).GetAwaiter().GetResult();
                Equal(InstallationState.Success, result.State); True(messages.Any(m => m.Contains("备用镜像"))); Equal(false, messages.Any(m => m.Contains("old.test") || m.Contains("a.test") || m.Contains("Attempt")));
            }));
            test("V0.3.2 stale QuickStart identity after raw restoration cannot skip a usable mirror", () => Fixture(Name, path => {
                var f = new Fake(); var service = Service(); f.BadHosts.Add("old.test"); Equal(InstallationState.Success, Run(service, f, path).State);
                Equal(f.Original, f.Current); Equal("A", f.Identity); f.BadHosts.Clear(); f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl("old.test") });
                var next = Run(service, f, path); Equal(InstallationState.Success, next.State); Equal("A", f.Switches.Last()); Equal(3, f.Starts);
            }));
            test("V0.3.2 unresolved installer keeps window close blocked after monitoring returns", () => {
                using (var form = new MainForm()) { UiTests.Set(form, "installerStateUnresolved", true); var args = new FormClosingEventArgs(CloseReason.UserClosing, false); UiTests.Invoke(form, "OnFormClosing", args); True(args.Cancel); }
            });
            Case(test, "foreign unresolved runner prevents any new dispatch", f => f.ForeignRunner = true, (r, f) => { Equal(InstallationFailureKind.PackageManagerBusy, r.Failure.Kind); Equal(0, f.Starts); Equal(0, f.Cleanups); True(r.Failure.Evidence.Contains("旧安装")); });
            test("V0.3.2 deliberate retry also requires remote SHA and does not blame missing capability on corrupt file", () => Fixture(Name, path => {
                var f = new Fake { HashUnavailable = true }; f.Runs.Enqueue(new Outcome { Exit = 1, Log = FailureUrl("old.test") }); var service = Service(); Equal(1, Run(service, f, path).Attempts.Count);
                var second = Run(service, f, path); Equal(InstallationFailureKind.HashUnavailable, second.Failure.Kind); Equal(1, f.Starts); Equal(false, second.Failure.UserMessage.Contains("损坏"));
            }));
        }
        private static void Case(Action<string, Action> test, string name, Action<Fake> setup, Action<InstallationWorkflowResult, Fake> check)
        { test("V0.3.2 " + name, () => Fixture(Name, path => { var f = new Fake(); setup(f); var result = Run(Service(), f, path); check(result, f); })); }
        private static InstallationWorkflowService Service()
        { return new InstallationWorkflowService { BusyChecks = 3, BusyInterval = TimeSpan.FromMilliseconds(1), ReconnectInterval = TimeSpan.FromMilliseconds(1), Engine = new PluginInstallationService { PollInterval = TimeSpan.FromMilliseconds(1) } }; }
        private static InstallationWorkflowResult Run(InstallationWorkflowService service, Fake fake, string path, CancellationToken token = default(CancellationToken), IProgress<InstallationWorkflowProgress> progress = null)
        { var expected = Router(); expected.PackageManager = fake.Manager; expected.Architecture = fake.RouterArchitecture; expected.SdkSeries = fake.RouterSdk; expected.Version = fake.RouterVersion; return Task.Run(() => service.RunAsync(fake, expected, path, progress, token)).GetAwaiter().GetResult(); }
        private static void Fixture(string name, Action<string> body)
        { string folder = Path.Combine(Path.GetTempPath(), "istore-workflow-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder); string path = Path.Combine(folder, name); try { File.WriteAllText(path, "abc", new UTF8Encoding(false)); body(path); } finally { File.Delete(path); Directory.Delete(folder); } }
        private static RouterEnvironment Router() { return new RouterEnvironment { IsConnected = true, Version = "25.12.5", SdkSeries = "25.12", Architecture = "x86_64", PackageManager = PackageManagerKind.APK, HostKeyFingerprint = "SHA256:fixture" }; }
        private static List<RepositoryFeed> Feeds(string text) { return SoftwareSourceService.ParseFeeds(text, PackageManagerKind.APK, SoftwareSourceCommands.DistfeedsPath(PackageManagerKind.APK), RepositoryKind.Official); }
        private static ProbeResult Ok(string text) { return new ProbeResult { ExitStatus = 0, Output = text, Error = "" }; }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
        private sealed class Outcome { public int Exit; public string Log = ""; public bool Partial; }
        private sealed class Fake : IInstallationWorkflowSession, IPluginInstallSession, ISoftwareSourceSession, IFastRepositoryProbeSession, IInstallerMonitoringSession
        {
            public bool Connected = true, ConnectFailure, ChangedIdentity, Busy, HashMismatch, MakeselfBad, RestoreFailure, MissingBeforeDispatch, DisconnectPoll, LostAcknowledgement, NeverEnds;
            public bool CorruptBeforeDispatch, BusyAfterCheck, HashUnavailable, DuplicateCandidates;
            public bool ForeignRunner;
            public bool RunnerFailure;
            public bool UseSftpPolling, DisconnectAfterFirstSftpPoll;
            public bool UsePersistentPolling;
            public bool FreshInstallerWrappers;
            public int PollsBeforeExit;
            private PersistentInstallerMonitor persistentMonitor;
            public readonly PersistentPollTests.Files PersistentFiles = new PersistentPollTests.Files();
            public readonly Dictionary<string, double> FastLatency = new Dictionary<string, double>();
            public readonly HashSet<string> FastTimeoutHosts = new HashSet<string>();
            public readonly List<string> FastProbeUrls = new List<string>();
            public int FastProbeDelayMilliseconds;
            public readonly SftpPollTests.Files SftpFiles = new SftpPollTests.Files();
            public string RouterArchitecture = "x86_64", ArchiveArchitecture = "x86_64", RouterSdk = "25.12", RouterVersion = "25.12.5", LastLaunch, LastCleanup;
            public bool PostNotAcknowledged, SwitchKeepsOldIdentity, SwitchKeepsOldFeeds;
            public int Starts, Uploads, HashReads, Probes, CandidateReads, ReadinessReads, UpdateCalls, Reconnects, BusyReads, BusyRemaining, Backups, Restores, Cleanups, BackupDeletes;
            public string Rom, Original, Current, Identity = "Original", UploadedHash;
            public long Size;
            public int TransientFailures;
            public PackageManagerKind Manager = PackageManagerKind.APK;
            public Action AfterSwitch, AfterStart;
            public readonly Queue<Outcome> Runs = new Queue<Outcome>();
            public readonly Queue<PackageUpdateResult> Updates = new Queue<PackageUpdateResult>();
            public readonly HashSet<string> BadHosts = new HashSet<string>();
            public readonly HashSet<string> BadUrls = new HashSet<string>();
            public readonly List<string> ProbeUrls = new List<string>();
            public readonly List<string> Switches = new List<string>(), Events = new List<string>();
            public readonly PackageSnapshot Packages = PackageSnapshotService.Parse("base-1.0\nluci-app-openclash-0.47.156-r1", PackageManagerKind.APK);
            private Outcome activeOutcome;
            private bool missingUsed;
            public readonly RouterReadiness Readiness = new RouterReadiness { DefaultRoute = true, DnsChecked = 3, DnsResolved = 3, UnixTime = InstallationReadiness.UnixNow, TmpBytes = 1024L * 1024 * 1024, PersistentBytes = 1024 * 1024 * 100 };
            public SoftwareSourceInfo[] candidates = {
                new SoftwareSourceInfo { Identity = "Original", Url = "https://old.test/openwrt/", Name = "Original" }, new SoftwareSourceInfo { Identity = "A", Url = "https://a.test/openwrt/", Name = "A" }, new SoftwareSourceInfo { Identity = "B", Url = "https://b.test/openwrt/", Name = "B" } };
            public Fake() { Rom = "https://downloads.test/releases/25.12/packages/x86_64/base/packages.adb\n"; Original = "# preserve exact comment\n" + SoftwareSourceService.RebaseRom(Rom, "https://old.test/openwrt/"); Current = Original; }
            public void MakeOpkg() { Manager = PackageManagerKind.OPKG; Rom = "src/gz openwrt_base https://downloads.test/releases/24.10/packages/x86_64/base\n"; Original = "# preserve original OPKG comment\n" + SoftwareSourceService.RebaseRom(Rom, "https://old.test/openwrt/"); Current = Original; }
            public bool IsConnected { get { return Connected; } }
            public IPluginInstallSession Installer { get { return FreshInstallerWrappers ? (IPluginInstallSession)new InstallerFacade(this) : this; } }
            public ISoftwareSourceSession Sources { get { return this; } }
            public string Sanitize(string text) { return text; }
            public RouterEnvironment ConnectAndDetect(RouterEnvironment expected, bool force, Action<string> log, CancellationToken token)
            { if (!Connected || force) { Reconnects++; if (ConnectFailure) throw new IOException("router unreachable"); Connected = true; } var value = Router(); value.PackageManager = Manager; value.Architecture = RouterArchitecture; value.SdkSeries = RouterSdk; value.Version = RouterVersion; if (ChangedIdentity) value.HostKeyFingerprint = "changed"; return value; }
            public RouterReadiness ReadReadiness(IList<RepositoryFeed> feeds) { ReadinessReads++; return Readiness; }
            public bool PackageManagerBusy(PackageManagerKind manager) { BusyReads++; if (ForeignRunner) throw new InstallationPolicyException(InstallationFailureKind.PackageManagerBusy, "发现旧安装 session；不启动新安装器。"); return Busy || BusyRemaining-- > 0; }
            public bool WasLaunched(RemoteTempSession remote) { return Starts > 0; }
            public ProbeResult RestoreOriginal(PackageManagerKind manager, string text, string path) { Restores++; Events.Add("restore"); if (RestoreFailure) return new ProbeResult { ExitStatus = 1 }; Current = text; return Ok(""); }
            public bool DeleteBackup(string path) { BackupDeletes++; return true; }
            public ProbeResult ReadOfficial(PackageManagerKind manager, bool rom) { return Ok(rom ? Rom : Current); }
            public IDictionary<string, string> ReadAdditional(PackageManagerKind manager)
            { return manager == PackageManagerKind.OPKG ? new Dictionary<string, string> { { "/etc/opkg/compat.conf", "src/gz compat https://compat.test/repo" } } : new Dictionary<string, string> { { "/etc/apk/repositories.d/compat.list", Compat }, { "/etc/apk/repositories.d/custom.list", "https://custom.test/repo/packages.adb" } }; }
            public ProbeResult QuickStartGet(bool list)
            { if (list) CandidateReads++; object value = list ? (object)new { softSourceList = DuplicateCandidates ? candidates.Concat(candidates).Concat(new[] { new SoftwareSourceInfo { Identity = "AAlias", Name = "alias A", Url = "https://a.test/openwrt/" } }).ToArray() : candidates } : new { softSource = candidates.First(c => c.Identity == Identity) }; return Ok(new JavaScriptSerializer().Serialize(new { success = true, result = value })); }
            public ProbeResult QuickStartSwitch(string identity) { if (!SwitchKeepsOldIdentity) Identity = identity; Switches.Add(identity); if (!SwitchKeepsOldFeeds) Current = SoftwareSourceService.RebaseRom(Rom, candidates.First(c => c.Identity == identity).Url); Events.Add("switch"); if (AfterSwitch != null) AfterSwitch(); return Ok(PostNotAcknowledged ? "{\"success\":false}" : "{\"success\":true}"); }
            public ProbeResult ProbeIndex(string url) { Probes++; ProbeUrls.Add(url); bool failed = BadUrls.Contains(url) || BadHosts.Contains(new Uri(url).Host) || TransientFailures-- > 0; return Ok("ISTORE_PROBE|" + (failed ? "404" : "206") + "|0.1"); }
            public ProbeResult ProbeIndexFast(string url)
            {
                FastProbeUrls.Add(url); if (FastProbeDelayMilliseconds > 0) Thread.Sleep(FastProbeDelayMilliseconds);
                var result = ProbeIndex(url); string host = new Uri(url).Host; double latency;
                if (FastTimeoutHosts.Contains(host)) return new ProbeResult { ExitStatus = 28, Output = "ISTORE_PROBE|000|3.0", Error = "fast timeout" };
                if (FastLatency.TryGetValue(host, out latency)) result.Output = result.Output.Substring(0, result.Output.LastIndexOf('|') + 1) + (latency / 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return result;
            }
            public IDisposable OpenInstallerMonitor(RemoteTempSession s)
            {
                if (!UsePersistentPolling) return new PerformanceScope(() => { });
                persistentMonitor = new PersistentInstallerMonitor(PersistentFiles.Create, s);
                return new PerformanceScope(() => { persistentMonitor.Dispose(); persistentMonitor = null; });
            }
            public PackageUpdateResult Update(PackageManagerKind manager) { UpdateCalls++; return Updates.Count > 0 ? Updates.Dequeue() : new PackageUpdateResult { ExitCode = 0, Stdout = "OK: 12048 distinct packages available" }; }
            public string BackupOfficial(PackageManagerKind manager) { Backups++; return "/tmp/istore-plugin-helper.ABCDEFGH/distfeeds.backup"; }
            public ProbeResult ReadBackup(string path) { return Ok(Original); }
            public ProbeResult RestoreBackup(PackageManagerKind manager, string path) { return RestoreOriginal(manager, Original, path); }
            public long TmpAvailable() { return Readiness.TmpBytes; }
            public void CreateDirectory(RemoteTempSession remote) { }
            public void Upload(Stream source, RemoteTempSession remote, Action<long> progress, CancellationToken token) { Uploads++; Size = source.Length; UploadedHash = PluginPackageInspection.ComputeSha256(source); progress(source.Length); }
            public long RemoteFileSize(RemoteTempSession remote) { if (MissingBeforeDispatch && !missingUsed && HashReads > 0) { missingUsed = true; throw new FileNotFoundException(); } return Size; }
            public ProbeResult RemoteHash(RemoteTempSession remote) { HashReads++; if (HashUnavailable) return new ProbeResult { ExitStatus = 127 }; return Ok(HashMismatch || (CorruptBeforeDispatch && HashReads >= 2) ? new string('0', 64) : UploadedHash); }
            public ProbeResult Inspect(RemoteTempSession remote, bool check)
            { if (check && BusyAfterCheck) Busy = true; return check ? MakeselfBad ? new ProbeResult { ExitStatus = 2, Error = "checksum failed" } : Ok("MD5 checksums are OK.\nAll good.") : Ok("Identification: PassWall_26.9.16_with_sdk_" + RouterSdk + "\nTarget directory: " + ArchiveArchitecture + "\nUncompressed size: 80 KB\n"); }
            public PackageSnapshot Snapshot(PackageManagerKind manager) { return PackageSnapshotService.Parse(string.Join("\n", Packages.Packages.Select(p => p.Key + (manager == PackageManagerKind.OPKG ? " - " : "-") + p.Value)), manager); }
            public bool PackageExists(PackageManagerKind manager, string name) { return Packages.Packages.ContainsKey(name); }
            public void CheckRunner() { if (RunnerFailure) throw new IOException("后台安装能力检查失败（exit 127）：Missing required runner command(s): base64"); }
            public void StartInstaller(RemoteTempSession remote)
            { Starts++; LastLaunch = PluginInstallCommands.Launch(remote); activeOutcome = Runs.Count > 0 ? Runs.Dequeue() : new Outcome(); if (AfterStart != null) AfterStart(); if (LostAcknowledgement) { LostAcknowledgement = false; Connected = false; throw new IOException("lost acknowledgement"); } }
            public InstallerPoll PollInstaller(RemoteTempSession remote, long stdoutOffset, long stderrOffset)
            {
                if (DisconnectPoll) { DisconnectPoll = false; Connected = false; throw new IOException("SSH lost"); }
                if (UseSftpPolling && DisconnectAfterFirstSftpPoll && SftpFiles.Creates > 0) { DisconnectAfterFirstSftpPoll = false; Connected = false; throw new IOException("SSH lost between SFTP polls"); }
                if (activeOutcome.Partial) Packages.Packages["dependency"] = "1.0";
                if (activeOutcome.Exit == 0) Packages.Packages["luci-app-passwall"] = "26.9.16-r1";
                if (UsePersistentPolling)
                {
                    PersistentFiles.Data[remote.Directory + "/stdout.log"] = Encoding.UTF8.GetBytes(activeOutcome.Log + "\n");
                    PersistentFiles.Data[remote.Directory + "/stderr.log"] = new byte[0];
                    if (!NeverEnds && PollsBeforeExit-- <= 0) PersistentFiles.Data[remote.Directory + "/exit.code"] = Encoding.ASCII.GetBytes(activeOutcome.Exit.ToString());
                    return persistentMonitor.Poll(stdoutOffset, stderrOffset);
                }
                if (UseSftpPolling)
                {
                    SftpFiles.Data[remote.Directory + "/stdout.log"] = Encoding.UTF8.GetBytes(activeOutcome.Log + "\n");
                    SftpFiles.Data[remote.Directory + "/stderr.log"] = new byte[0];
                    if (!NeverEnds) SftpFiles.Data[remote.Directory + "/exit.code"] = Encoding.ASCII.GetBytes(activeOutcome.Exit.ToString());
                    return new SftpInstallerPoller().Poll(SftpFiles.Create, remote, stdoutOffset, stderrOffset);
                }
                byte[] data = stdoutOffset == 0 ? Encoding.UTF8.GetBytes(activeOutcome.Log + "\n") : new byte[0];
                return new InstallerPoll { Stdout = data, Stderr = new byte[0], ExitCode = NeverEnds ? (int?)null : activeOutcome.Exit };
            }
            public bool Cleanup(RemoteTempSession remote) { Cleanups++; LastCleanup = remote.CleanupCommand(); Events.Add("cleanup"); return true; }
            private sealed class InstallerFacade : IPluginInstallSession, IInstallerMonitoringSession
            {
                private readonly Fake inner; private bool monitoring;
                public InstallerFacade(Fake inner) { this.inner = inner; }
                public IDisposable OpenInstallerMonitor(RemoteTempSession s) { var scope = inner.OpenInstallerMonitor(s); monitoring = true; return new PerformanceScope(() => { try { scope.Dispose(); } finally { monitoring = false; } }); }
                public InstallerPoll PollInstaller(RemoteTempSession s, long stdout, long stderr) { if (!monitoring) throw new InvalidOperationException("Monitor owner was replaced"); return inner.PollInstaller(s, stdout, stderr); }
                public string Sanitize(string text) { return inner.Sanitize(text); }
                public long TmpAvailable() { return inner.TmpAvailable(); }
                public void CreateDirectory(RemoteTempSession s) { inner.CreateDirectory(s); }
                public void Upload(Stream source, RemoteTempSession s, Action<long> progress, CancellationToken token) { inner.Upload(source, s, progress, token); }
                public long RemoteFileSize(RemoteTempSession s) { return inner.RemoteFileSize(s); }
                public ProbeResult RemoteHash(RemoteTempSession s) { return inner.RemoteHash(s); }
                public ProbeResult Inspect(RemoteTempSession s, bool check) { return inner.Inspect(s, check); }
                public PackageSnapshot Snapshot(PackageManagerKind manager) { return inner.Snapshot(manager); }
                public bool PackageExists(PackageManagerKind manager, string name) { return inner.PackageExists(manager, name); }
                public void CheckRunner() { inner.CheckRunner(); }
                public void StartInstaller(RemoteTempSession s) { inner.StartInstaller(s); }
                public bool Cleanup(RemoteTempSession s) { return inner.Cleanup(s); }
            }
        }
    }
}

