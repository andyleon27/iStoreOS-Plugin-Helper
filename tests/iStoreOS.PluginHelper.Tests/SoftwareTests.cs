using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class SoftwareTests
    {
        public static void Run(Action<string, Action> test)
        {
            foreach (int good in new[] { 8, 7, 0 })
            {
                int count = good;
                test("V0.2 health " + count + "/8", () =>
                {
                    var fake = new FakeSession(); fake.GoodByHost["old.test"] = count;
                    var health = new RepositoryProbeService().Probe(fake, Feeds(fake.Original), Ignore, CancellationToken.None, "fixture");
                    Equal(count, health.Successful); Equal(8, health.Total);
                    Equal(count == 8 ? RepositoryHealth.FULL : count == 0 ? RepositoryHealth.DOWN : RepositoryHealth.PARTIAL, health.Status);
                });
            }
            test("V0.2 healthy current source never switches or backs up", () =>
            {
                var fake = new FakeSession(); var result = RunWorkflow(fake);
                Equal(SoftwareEnvironmentStatus.Ready, result.Status); Equal(0, fake.Switches.Count); Equal(0, fake.Backups); Equal(false, result.SourceSwitchOccurred); Equal(1, fake.UpdateCalls);
                Equal(0, fake.CandidateReads); Equal(false, result.RecoveryAttempted);
            });
            test("V0.2 current DOWN, candidate A DOWN, B FULL chooses B", () =>
            {
                var fake = RepairFixture(); fake.GoodByHost["a.test"] = 0;
                fake.UpdateResults.Enqueue(Success()); var result = RunWorkflow(fake);
                Equal(SoftwareEnvironmentStatus.Ready, result.Status); Equal("B", fake.Switches.Single()); Equal(true, result.SourceSwitchOccurred);
                Equal(1, fake.Backups); Equal(false, result.RecoveryAttempted); True(fake.Current.Contains("https://b.test/openwrt/"));
            });
            test("V0.2 PARTIAL candidates never selected", () =>
            {
                var fake = RepairFixture(); fake.GoodByHost["a.test"] = 7; fake.GoodByHost["b.test"] = 0;
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Failed, result.Status); Equal(0, fake.Switches.Count); Equal(0, fake.Backups);
            });
            test("V0.2 FULL candidate update failure tries next FULL", () =>
            {
                var fake = RepairFixture(); fake.UpdateResults.Enqueue(Failure("https://a.test/openwrt/")); fake.UpdateResults.Enqueue(Success());
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Ready, result.Status); Equal("A,B", string.Join(",", fake.Switches));
                Equal(3, result.Updates.Count); Equal(false, result.RecoveryAttempted); True(fake.Current.Contains("b.test"));
            });
            test("V0.2 all candidates fail restores exact original text", () =>
            {
                var fake = RepairFixture(); fake.UpdateResults.Enqueue(Failure("https://a.test/openwrt/")); fake.UpdateResults.Enqueue(Failure("https://b.test/openwrt/"));
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Failed, result.Status); True(result.RecoveryAttempted); True(result.RecoverySucceeded);
                Equal(fake.Original, fake.Current); Equal(1, fake.Restores); True(fake.Switches.Contains("Original"));
            });
            test("V0.2 recovery failure never claims restored or Ready", () =>
            {
                var fake = RepairFixture(); fake.RestoreFails = true; fake.UpdateResults.Enqueue(Failure("https://a.test/openwrt/")); fake.UpdateResults.Enqueue(Failure("https://b.test/openwrt/"));
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.RecoveryFailed, result.Status); Equal(false, result.RecoverySucceeded); True(result.BackupPath != null);
            });
            test("V0.2 compat failure warns, actual update can still succeed", () =>
            {
                var fake = new FakeSession(); fake.GoodByHost["compat.test"] = 0;
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Ready, result.Status); Equal(0, fake.Switches.Count); Equal(1, fake.UpdateCalls);
                Equal(0, result.AdditionalHealth.Successful);
            });
            test("V0.2 compat update failure does not rotate official mirrors", () =>
            {
                var fake = new FakeSession(); fake.GoodByHost["compat.test"] = 0;
                fake.UpdateResults.Enqueue(new PackageUpdateResult { ExitCode = 1, Stderr = "WARNING: fetching " + FakeSession.Compat + ": temporary error" });
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Failed, result.Status); Equal(0, fake.Switches.Count); Equal(0, fake.CandidateReads);
                True(result.Updates[0].AdditionalRepositoryFailure); Equal(false, result.Updates[0].OfficialRepositoryFailure);
            });
            test("V0.2 stdout fetch cannot misattribute stderr compat failure", () =>
            {
                var fake = new FakeSession();
                fake.UpdateResults.Enqueue(new PackageUpdateResult { ExitCode = 1, Stdout = "fetch https://old.test/openwrt/releases/25.12.0/packages/x86_64/base/packages.adb\n", Stderr = "WARNING: fetching " + FakeSession.Compat + ": temporary error\n" });
                var result = RunWorkflow(fake); Equal(0, fake.Switches.Count); Equal(false, result.Updates[0].OfficialRepositoryFailure); True(result.Updates[0].AdditionalRepositoryFailure);
            });
            test("V0.2 current FULL with explicit official update error can repair", () =>
            {
                var fake = new FakeSession(); fake.UpdateResults.Enqueue(Failure("https://old.test/openwrt/")); fake.UpdateResults.Enqueue(Success());
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Ready, result.Status); Equal("A", fake.Switches.Single());
            });
            test("V0.2 QuickStart unavailable still checks healthy current config", () =>
            {
                var fake = new FakeSession(); fake.ApiUnavailable = true;
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Ready, result.Status); Equal(null, result.CurrentSource); Equal(0, fake.Switches.Count);
            });
            test("V0.2 QuickStart unavailable with DOWN source fails safely", () =>
            {
                var fake = RepairFixture(); fake.ApiUnavailable = true;
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Failed, result.Status); Equal(0, fake.Switches.Count); Equal(0, fake.Backups);
            });
            test("V0.2 OPKG parses only enabled src/gz", () =>
            {
                var feeds = SoftwareSourceService.ParseFeeds("# src/gz disabled https://disabled.test/\noption check_signature\nsrc/gz openwrt_base https://host.test/releases/24.10.4/packages/aarch64_generic/base\noption lists_dir /tmp/opkg-lists\n", PackageManagerKind.OPKG, "/etc/opkg/customfeeds.conf", RepositoryKind.Custom);
                Equal(1, feeds.Count); Equal("openwrt_base", feeds[0].Name); Equal("https://host.test/releases/24.10.4/packages/aarch64_generic/base/Packages.gz", feeds[0].IndexUrl);
                Equal(false, SoftwareSourceCommands.IsAdditionalPath(PackageManagerKind.OPKG, "/etc/opkg/opkg.conf"));
            });
            test("V0.2 APK commented custom config has no enabled feeds", () => Equal(0, SoftwareSourceService.ParseFeeds("# defaults\n\n# https://custom.test/packages.adb\n", PackageManagerKind.APK, "customfeeds.list", RepositoryKind.Custom).Count));
            test("V0.2 malformed active feed cannot count as FULL", () =>
            {
                var feeds = SoftwareSourceService.ParseFeeds("https://valid.test/packages.adb\nnot-a-repository\n", PackageManagerKind.APK, "distfeeds.list", RepositoryKind.Official);
                Equal(2, feeds.Count); True(feeds[1].ParseError != null);
            });
            test("V0.2 HTTP 206 and 3xx accepted; nonzero curl never passes", () =>
            {
                foreach (int status in new[] { 200, 206, 302 }) True(RepositoryProbeService.Parse(new ProbeResult { ExitStatus = 0, Output = "ISTORE_PROBE|" + status + "|0.123" }, new RepositoryFeed()).Success);
                Equal(false, RepositoryProbeService.Parse(new ProbeResult { ExitStatus = 0, Output = "ISTORE_PROBE|404|0.123" }, new RepositoryFeed()).Success);
                Equal(false, RepositoryProbeService.Parse(new ProbeResult { ExitStatus = 28, Output = "ISTORE_PROBE|206|15.000" }, new RepositoryFeed()).Success);
            });
            test("V0.2 real QuickStart envelopes parse dynamically", () =>
            {
                var current = QuickStartService.ParseCurrent("{\"success\":true,\"result\":{\"softSource\":{\"identity\":\"NewMirror\",\"name\":\"未来镜像\",\"url\":\"https://future.test/openwrt/\"}}}");
                Equal("NewMirror", current.Identity);
                var list = QuickStartService.ParseCandidates("{\"success\":true,\"result\":{\"softSourceList\":[{\"identity\":\"NewMirror\",\"name\":\"未来镜像\",\"url\":\"https://future.test/openwrt/\"}]}}");
                Equal(1, list.Count); Equal("NewMirror", list[0].Identity); Equal(false, QuickStartService.IsSuccessfulPost("{\"success\":false,\"error\":\"denied\"}"));
            });
            test("V0.2 unnamed custom source shows a readable fallback", () =>
            {
                var source = QuickStartService.ParseCurrent("{\"success\":true,\"result\":{\"softSource\":{\"url\":\"https://custom.test/openwrt/\"}}}");
                Equal("自定义 / 未知来源", source.ToString());
            });
            test("V0.2 lost post-write reads do not claim zero source switch", () =>
            {
                var fake = RepairFixture(); fake.FailReadAfterPost = true; var progress = new CapturedProgress();
                var result = new SoftwareEnvironmentService().RunAsync(fake, new RouterEnvironment { IsConnected = true, PackageManager = PackageManagerKind.APK }, progress,
                    plan => Task.FromResult(true), CancellationToken.None).GetAwaiter().GetResult();
                Equal(SoftwareEnvironmentStatus.RecoveryFailed, result.Status); True(result.SwitchAttempted);
                True(progress.Logs.Last().Contains("source switch=Unknown"));
            });
            test("V0.2 ROM rebase preserves full release and kernel ABI suffixes", () =>
            {
                string original = "https://downloads.test/releases/25.12.0/targets/x86/64/kmods/6.12.44-1-abcdef/packages.adb\nhttps://downloads.test/openwrt/releases/25.12.0/packages/x86_64/base/packages.adb\n";
                string rebased = SoftwareSourceService.RebaseRom(original, "https://new.test/openwrt/");
                Equal("https://new.test/openwrt/releases/25.12.0/targets/x86/64/kmods/6.12.44-1-abcdef/packages.adb\nhttps://new.test/openwrt/releases/25.12.0/packages/x86_64/base/packages.adb\n", rebased);
            });
            test("V0.2 HTTPS FULL wins over faster HTTP FULL", () =>
            {
                var fake = RepairFixture(); fake.Candidates[1].Url = "http://a.test/openwrt/"; fake.UpdateResults.Enqueue(Success());
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Ready, result.Status); Equal("B", fake.Switches.Single());
            });
            test("V0.2 denied confirmation makes no backup or POST", () =>
            {
                var fake = RepairFixture(); var result = RunWorkflow(fake, false);
                Equal(SoftwareEnvironmentStatus.Cancelled, result.Status); Equal(0, fake.Switches.Count); Equal(0, fake.Backups); Equal(fake.Original, fake.Current);
            });
            test("V0.2 configuration race stops before POST", () =>
            {
                var fake = RepairFixture(); fake.ChangeBeforeBackupRead = true;
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Failed, result.Status); Equal(0, fake.Switches.Count); Equal(0, fake.Backups);
            });
            test("V0.2 backup mismatch stops before POST", () =>
            {
                var fake = RepairFixture(); fake.BadBackup = true;
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Failed, result.Status); Equal(0, fake.Switches.Count);
            });
            test("V0.2 POST mutates then throws still recovers", () =>
            {
                var fake = RepairFixture(); fake.ThrowAfterPost = true;
                var result = RunWorkflow(fake); True(result.RecoveryAttempted); True(result.RecoverySucceeded); Equal(fake.Original, fake.Current);
            });
            test("V0.2 cancellation after POST still recovers", () =>
            {
                var token = new CancellationTokenSource(); var fake = RepairFixture(); fake.AfterPost = () => token.Cancel();
                var result = RunWorkflow(fake, true, token.Token); Equal(SoftwareEnvironmentStatus.Cancelled, result.Status); True(result.RecoverySucceeded); Equal(fake.Original, fake.Current);
            });
            test("V0.2 exit 0 with repository WARNING fails gate", () =>
            {
                var update = new PackageUpdateResult { ExitCode = 0, Stderr = "WARNING: fetching https://host.test/packages.adb: temporary error" };
                PackageManagerService.Analyze(update, new[] { new RepositoryFeed { IndexUrl = "https://host.test/packages.adb" } }, new RepositoryFeed[0]);
                Equal(false, update.Success); True(update.OfficialRepositoryFailure);
            });
            test("V0.2 update timeout and non-source errors classified", () =>
            {
                foreach (var pair in new Dictionary<string, SoftwareErrorCategory> { { "architecture mismatch", SoftwareErrorCategory.Compatibility }, { "SDK mismatch", SoftwareErrorCategory.Compatibility }, { "kernel ABI mismatch", SoftwareErrorCategory.Compatibility }, { "version conflict", SoftwareErrorCategory.Conflict }, { "package not found", SoftwareErrorCategory.PackageAvailability }, { "invalid index signature", SoftwareErrorCategory.RepoIndex }, { "DNS failure", SoftwareErrorCategory.Transport } })
                    Equal(pair.Value, PackageManagerService.Classify(pair.Key, false));
                var update = new PackageUpdateResult { ExitCode = 124 }; PackageManagerService.Analyze(update, new RepositoryFeed[0], new RepositoryFeed[0]); True(update.TimedOut); Equal(SoftwareErrorCategory.Timeout, update.ErrorCategory);
            });
            test("V0.2 exit 0 compatibility and conflict diagnostics fail gate", () =>
            {
                foreach (string diagnostic in new[] { "architecture mismatch", "SDK mismatch", "kernel ABI mismatch", "version conflict", "package not found" })
                {
                    var update = new PackageUpdateResult { ExitCode = 0, Stderr = diagnostic };
                    PackageManagerService.Analyze(update, new RepositoryFeed[0], new RepositoryFeed[0]); Equal(false, update.Success); True(PackageManagerService.IsNonSourceFailure(update));
                }
            });
            test("V0.2 compatibility error never treated as mirror repair", () =>
            {
                var fake = RepairFixture(); fake.UpdateResults.Clear(); fake.UpdateResults.Enqueue(new PackageUpdateResult { ExitCode = 1, Stderr = "ERROR: kernel ABI mismatch" });
                var result = RunWorkflow(fake); Equal(SoftwareEnvironmentStatus.Failed, result.Status); Equal(0, fake.Switches.Count); Equal(0, fake.CandidateReads);
            });
            test("V0.2 full stdout stderr retained and password redacted", () =>
            {
                var fake = new FakeSession(); fake.Secret = "test-secret";
                fake.UpdateResults.Enqueue(new PackageUpdateResult { ExitCode = 0, Stdout = "line1\nline2 test-secret\n", Stderr = "" });
                var result = RunWorkflow(fake); True(result.Updates[0].Stdout.Contains("line1\nline2")); Equal(false, result.Updates[0].Stdout.Contains(fake.Secret));
            });
            test("V0.2 POSIX quoting and POST JSON are safe", () =>
            {
                Equal("'a'\"'\"'b'", ShellArgument.Quote("a'b"));
                string identity = "Mirror'$(touch /tmp/nope);`cmd`";
                string json = new JavaScriptSerializer().Serialize(new { softSourceIdentity = identity });
                True(SoftwareSourceCommands.QuickStartSwitch(identity).Contains("--data-binary " + ShellArgument.Quote(json)));
                bool rejected = false; try { SoftwareSourceCommands.ProbeIndex("file:///etc/passwd"); } catch (ArgumentException) { rejected = true; } True(rejected);
                rejected = false; try { SoftwareSourceCommands.RestoreBackup(PackageManagerKind.APK, "/etc/passwd"); } catch (ArgumentException) { rejected = true; } True(rejected);
            });
            test("V0.2 command boundary has no install or plugin operations", () =>
            {
                foreach (PackageManagerKind kind in new[] { PackageManagerKind.APK, PackageManagerKind.OPKG })
                {
                    string[] commands = { SoftwareSourceCommands.Update(kind), SoftwareSourceCommands.BackupOfficial(kind), SoftwareSourceCommands.RestoreBackup(kind, "/tmp/istore-plugin-helper.ABCDEFGH/distfeeds.backup"), SoftwareSourceCommands.QuickStartSwitch("Source") };
                    foreach (string command in commands) Equal(false, Regex.IsMatch(command, @"\b(apk (add|del|upgrade)|opkg (install|remove|upgrade)|uci|sed -i|--force|\.run|/etc/init.d/)"));
                }
            });
        }
        private static SoftwareEnvironmentResult RunWorkflow(FakeSession fake, bool approve = true, CancellationToken token = default(CancellationToken))
        { return new SoftwareEnvironmentService().RunAsync(fake, new RouterEnvironment { IsConnected = true, PackageManager = PackageManagerKind.APK }, null, plan => Task.FromResult(approve), token).GetAwaiter().GetResult(); }
        private static FakeSession RepairFixture()
        { var fake = new FakeSession(); fake.GoodByHost["old.test"] = 0; fake.UpdateResults.Enqueue(Failure("https://old.test/openwrt/")); return fake; }
        private static PackageUpdateResult Success() { return new PackageUpdateResult { ExitCode = 0, Stdout = "OK: 12043 distinct packages available\n", Stderr = "" }; }
        private static PackageUpdateResult Failure(string mirror) { return new PackageUpdateResult { ExitCode = 1, Stderr = "ERROR: fetching " + mirror + "releases/25.12.0/packages/x86_64/base/packages.adb: temporary error" }; }
        private static List<RepositoryFeed> Feeds(string text) { return SoftwareSourceService.ParseFeeds(text, PackageManagerKind.APK, "/etc/apk/repositories.d/distfeeds.list", RepositoryKind.Official); }
        private static void Ignore(string step, string log) { }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }

        private sealed class FakeSession : ISoftwareSourceSession
        {
            public const string Compat = "https://compat.test/repo-apk/all/compat/packages.adb";
            public string Original;
            public string Current;
            public string Rom;
            private string backup;
            public string Identity = "Original";
            public bool ApiUnavailable, RestoreFails, ChangeBeforeBackupRead, BadBackup, ThrowAfterPost, FailReadAfterPost;
            public Action AfterPost;
            public string Secret;
            public int Backups, Restores, UpdateCalls, CandidateReads;
            private int currentReads;
            public readonly List<string> Switches = new List<string>();
            public readonly Dictionary<string, int> GoodByHost = new Dictionary<string, int>();
            private readonly Dictionary<string, int> counts = new Dictionary<string, int>();
            public readonly Queue<PackageUpdateResult> UpdateResults = new Queue<PackageUpdateResult>();
            public readonly List<SoftwareSourceInfo> Candidates = new List<SoftwareSourceInfo>();
            public FakeSession()
            {
                Rom = "# ROM baseline\n" + string.Join("\n", new[] {
                    "https://downloads.test/releases/25.12.0/targets/x86/64/packages/packages.adb",
                    "https://downloads.test/releases/25.12.0/packages/x86_64/base/packages.adb",
                    "https://downloads.test/releases/25.12.0/targets/x86/64/kmods/6.12.44-1-abcdef/packages.adb",
                    "https://downloads.test/releases/25.12.0/packages/x86_64/luci/packages.adb",
                    "https://downloads.test/releases/25.12.0/packages/x86_64/packages/packages.adb",
                    "https://downloads.test/releases/25.12.0/packages/x86_64/routing/packages.adb",
                    "https://downloads.test/releases/25.12.0/packages/x86_64/telephony/packages.adb",
                    "https://downloads.test/releases/25.12.0/packages/x86_64/video/packages.adb" }) + "\n";
                Original = "# preserve user comment\n" + SoftwareSourceService.RebaseRom(Rom, "https://old.test/openwrt/"); Current = Original;
                Candidates.Add(new SoftwareSourceInfo { Identity = "Original", Name = "Original", Url = "https://old.test/openwrt/" });
                Candidates.Add(new SoftwareSourceInfo { Identity = "A", Name = "A", Url = "https://a.test/openwrt/" });
                Candidates.Add(new SoftwareSourceInfo { Identity = "B", Name = "B", Url = "https://b.test/openwrt/" });
            }
            public ProbeResult ReadOfficial(PackageManagerKind manager, bool rom)
            { if (!rom && FailReadAfterPost && Switches.Count > 0) throw new Exception("SSH read lost after POST"); if (!rom && ++currentReads == 2 && ChangeBeforeBackupRead) Current += "# externally changed\n"; return Ok(rom ? Rom : Current); }
            public IDictionary<string, string> ReadAdditional(PackageManagerKind manager)
            { return new Dictionary<string, string> { { "/etc/apk/repositories.d/compat.list", Compat + "\n" }, { "/etc/apk/repositories.d/customfeeds.list", "# no enabled URLs\n" } }; }
            public ProbeResult QuickStartGet(bool list)
            {
                if (list) CandidateReads++;
                if (ApiUnavailable) return new ProbeResult { ExitStatus = 7, Error = "connection refused" };
                object payload = list ? (object)new { softSourceList = Candidates.Select(c => new { identity = c.Identity, name = c.Name, url = c.Url }).ToArray() } :
                    new { softSource = new { identity = Identity, name = Identity, url = Candidates.First(c => c.Identity == Identity).Url } };
                return Ok(new JavaScriptSerializer().Serialize(new { success = true, result = payload }));
            }
            public ProbeResult QuickStartSwitch(string identity)
            {
                Switches.Add(identity); Identity = identity;
                Current = SoftwareSourceService.RebaseRom(Rom, Candidates.First(c => c.Identity == identity).Url);
                if (AfterPost != null) AfterPost();
                if (ThrowAfterPost) throw new Exception("lost response after write");
                return Ok("{\"success\":true}");
            }
            public ProbeResult ProbeIndex(string url)
            {
                string host = new Uri(url).Host; int count; counts.TryGetValue(host, out count); counts[host] = count + 1;
                int good; if (!GoodByHost.TryGetValue(host, out good)) good = 8;
                bool pass = count % 8 < good;
                return new ProbeResult { ExitStatus = 0, Output = "ISTORE_PROBE|" + (pass ? 206 : 404) + "|" + (host == "a.test" ? "0.1" : "0.2") };
            }
            public PackageUpdateResult Update(PackageManagerKind manager) { UpdateCalls++; return UpdateResults.Count > 0 ? UpdateResults.Dequeue() : Success(); }
            public string BackupOfficial(PackageManagerKind manager) { Backups++; backup = Current; return "/tmp/istore-plugin-helper.ABCDEFGH/distfeeds.backup"; }
            public ProbeResult ReadBackup(string path) { return Ok(BadBackup ? "corrupt" : backup); }
            public ProbeResult RestoreBackup(PackageManagerKind manager, string path) { Restores++; if (RestoreFails) return new ProbeResult { ExitStatus = 1 }; Current = backup; return Ok(""); }
            public string Sanitize(string text) { return LogHelper.RedactPreservingLines(text, Secret); }
            private static ProbeResult Ok(string text) { return new ProbeResult { ExitStatus = 0, Output = text, Error = "" }; }
        }
        private sealed class CapturedProgress : IProgress<SoftwareProgress>
        {
            public readonly List<string> Logs = new List<string>();
            public void Report(SoftwareProgress item) { if (item.Log != null) Logs.Add(item.Log); }
        }
    }
}
