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
    internal static class RuntimeCompatibilityHotfixTests
    {
        public static void Run(Action<string, Action> test)
        {
            test("V1.0.2 temp creation uses only basic shell tools", () =>
                Equal(false, Regex.IsMatch(RemoteTempSession.Create().CreateCommand(), @"\b(stat|python|perl|realpath|readlink|find)\b")));
            test("V1.0.2 temp creation starts with restrictive umask", () =>
                Equal(true, RemoteTempSession.Create().CreateCommand().StartsWith("umask 077 && ", StringComparison.Ordinal)));
            test("V1.0.2 temp root symlink rejected before and after mkdir", () =>
            {
                string command = RemoteTempSession.Create().CreateCommand(), root = ShellArgument.Quote(RemoteTempSession.Root);
                Equal(2, Regex.Matches(command, Regex.Escape("test ! -L " + root)).Count);
                Before(command, "test ! -L " + root, "mkdir -- " + root);
                Before(command, "mkdir -- " + root, "; } && test ! -L " + root);
            });
            test("V1.0.2 temp root created only when not a directory", () =>
                Contains(RemoteTempSession.Create().CreateCommand(), "{ test -d " + ShellArgument.Quote(RemoteTempSession.Root) + " || mkdir -- " + ShellArgument.Quote(RemoteTempSession.Root) + "; }"));
            test("V1.0.2 temp root chmod failure gates session creation", () =>
            {
                var session = RemoteTempSession.Create(); string command = session.CreateCommand();
                Contains(command, "&& chmod 700 " + ShellArgument.Quote(RemoteTempSession.Root) + " && test ! -e ");
                Before(command, "chmod 700 " + ShellArgument.Quote(RemoteTempSession.Root), "mkdir -- " + ShellArgument.Quote(session.Directory));
            });
            test("V1.0.2 temp session rejects existing entries and dangling symlinks", () =>
            {
                var session = RemoteTempSession.Create(); string path = ShellArgument.Quote(session.Directory);
                Contains(session.CreateCommand(), "test ! -e " + path + " && test ! -L " + path + " && mkdir -- " + path);
            });
            test("V1.0.2 temp session is a fresh 32 hex GUID", () =>
            {
                var first = RemoteTempSession.Create(); var second = RemoteTempSession.Create();
                Equal(true, Regex.IsMatch(first.Id, @"\A[a-f0-9]{32}\z")); Equal(false, first.Id == second.Id);
                Equal(RemoteTempSession.Root + "/" + first.Id, first.Directory);
            });
            test("V1.0.2 temp session chmod follows exclusive mkdir", () =>
            {
                var session = RemoteTempSession.Create(); string path = ShellArgument.Quote(session.Directory);
                Contains(session.CreateCommand(), "mkdir -- " + path + " && chmod 700 " + path);
            });
            test("V1.0.2 cleanup remains scoped to one validated GUID", () =>
            {
                var session = RemoteTempSession.Create();
                Equal("test ! -L " + ShellArgument.Quote(RemoteTempSession.Root) + " && test ! -L " + ShellArgument.Quote(session.Directory) +
                    " && rm -rf -- " + ShellArgument.Quote(session.Directory), session.CleanupCommand());
            });
            foreach (string path in new[] { null, "", "/tmp", RemoteTempSession.Root, RemoteTempSession.Root + "/", RemoteTempSession.Root + "/../etc",
                RemoteTempSession.Root + "/" + new string('a', 31), RemoteTempSession.Root + "/" + new string('a', 33),
                RemoteTempSession.Root + "/" + new string('A', 32), RemoteTempSession.Root + "/" + new string('g', 32),
                RemoteTempSession.Root + "/" + new string('a', 32) + "/package.run", RemoteTempSession.Root + "/" + new string('a', 32) + "\n" })
            {
                string value = path;
                test("V1.0.2 temp invalid path rejected " + (value ?? "null").Replace("\n", "\\n"), () =>
                {
                    bool rejected = false; try { RemoteTempSession.ValidateDirectory(value); } catch (ArgumentException) { rejected = true; }
                    Equal(true, rejected);
                });
            }
            test("V1.0.2 remote file size retains wc byte validation", () =>
            {
                var session = RemoteTempSession.Create(); Equal("wc -c < " + ShellArgument.Quote(session.RunPath), PluginInstallCommands.FileSize(session));
            });
            test("V1.0.2 busy check retains inode locks without stat", () =>
            {
                Equal(false, Regex.IsMatch(InstallationReadiness.Busy, @"\bstat\b"));
                Contains(InstallationReadiness.Busy, "ls -di"); Contains(InstallationReadiness.Busy, "/proc/locks");
                Contains(InstallationReadiness.Busy, "case \"$i\" in ''|*[!0-9]*) exit 2");
                Contains(InstallationReadiness.Busy, "case \"$locked\" in 0)"); Contains(InstallationReadiness.Busy, "*) exit 2");
            });
            test("V1.0.2 busy check retains process and pending installer guards", () =>
            {
                Contains(InstallationReadiness.Busy, InstallationReadiness.PendingRunners); Contains(InstallationReadiness.Busy, "/proc/[0-9]*/comm");
                Contains(InstallationReadiness.Busy, "apk|opkg)"); Contains(InstallationReadiness.Busy, "BUSY_INSTALLER:");
            });

            foreach (PackageManagerKind kind in new[] { PackageManagerKind.OPKG, PackageManagerKind.APK })
            {
                PackageManagerKind manager = kind;
                SourceCase(test, manager, "false acknowledgement with verified state becomes Ready", f => { }, (r, f) =>
                {
                    Equal(SoftwareEnvironmentStatus.Ready, r.Status); Equal("Tsinghua", r.CurrentSource.Identity); Equal(2, f.UpdateCalls);
                    Equal(0, f.ActualProbes); Equal(RepositoryHealth.FULL, r.OfficialHealth.Status); Equal(true, r.SourceSwitchOccurred);
                    Equal(false, r.RecoveryAttempted); Equal(false, QuickStartService.IsSuccessfulPost(f.PostBody));
                    Equal("Tsinghua", string.Join(",", f.Switches)); Contains(string.Join("\n", f.Logs), "POST acknowledgement=False");
                    Contains(string.Join("\n", f.Logs), "实际 identity / distfeeds 已确认切换成功");
                });
                SourceCase(test, manager, "false acknowledgement with unchanged Tencent cannot pass", f => f.LeaveUnchanged = true, RejectedBeforeProbe);
                SourceCase(test, manager, "correct identity with old feeds cannot pass", f => f.KeepOldFeeds = true, RejectedBeforeProbe);
                SourceCase(test, manager, "correct feeds with wrong identity cannot pass", f => f.WrongIdentity = true, RejectedBeforeProbe);
                SourceCase(test, manager, "unreadable current identity cannot pass", f => f.CurrentReadFails = true, RejectedBeforeProbe);
                SourceCase(test, manager, "unreadable distfeeds cannot pass", f => f.ConfigReadFails = true, (r, f) =>
                { Equal(false, r.Status == SoftwareEnvironmentStatus.Ready); Equal(1, f.UpdateCalls); Equal(0, f.ActualProbes); Equal(true, r.RecoveryAttempted); });
                SourceCase(test, manager, "incomplete candidate feed list cannot pass", f => f.MissingFeed = true, RejectedBeforeProbe);
                SourceCase(test, manager, "candidate feed architecture mismatch cannot pass", f => f.WrongFeedArchitecture = true, RejectedBeforeProbe);
                SourceCase(test, manager, "cached FULL is not reprobed; real update failure still blocks readiness", f => { f.ActualProbePartial = true; f.UpdateFails = true; }, (r, f) =>
                { Equal(SoftwareEnvironmentStatus.Failed, r.Status); Equal(0, f.ActualProbes); Equal(2, f.UpdateCalls); Restored(r, f); });
                SourceCase(test, manager, "actual update failure still restores exact Tencent feeds", f => f.UpdateFails = true, (r, f) =>
                { Equal(SoftwareEnvironmentStatus.Failed, r.Status); Equal(0, f.ActualProbes); Equal(2, f.UpdateCalls); Equal(false, r.Updates.Last().Success); Restored(r, f); });
                SourceCase(test, manager, "non source update conflict keeps existing failure policy", f => f.UpdateConflict = true, (r, f) =>
                { Equal(SoftwareEnvironmentStatus.Failed, r.Status); Equal(SoftwareErrorCategory.Conflict, r.Updates.Last().ErrorCategory); Equal(2, f.UpdateCalls); Restored(r, f); });
                SourceCase(test, manager, "lost POST response can pass only through actual verification", f => f.ThrowAfterMutation = true, (r, f) =>
                { Equal(SoftwareEnvironmentStatus.Ready, r.Status); Equal(0, f.ActualProbes); Equal(2, f.UpdateCalls); Equal(false, r.RecoveryAttempted); });
                SourceCase(test, manager, "true acknowledgement with unchanged state cannot pass", f => { f.PostBody = "{\"success\":true}"; f.LeaveUnchanged = true; }, RejectedBeforeProbe);
            }
        }

        private static void SourceCase(Action<string, Action> test, PackageManagerKind manager, string name, Action<SourceSession> setup, Action<SoftwareEnvironmentResult, SourceSession> check)
        {
            test("V1.0.2 " + manager + " " + name, () =>
            {
                var fake = new SourceSession(manager); setup(fake);
                var result = new SoftwareEnvironmentService().RunAsync(fake, new RouterEnvironment { IsConnected = true, PackageManager = manager,
                    Version = "24.10.8", SdkSeries = "24.10", Architecture = "aarch64_cortex-a53" }, new ProgressCapture(fake.Logs),
                    plan => Task.FromResult(true), CancellationToken.None).GetAwaiter().GetResult();
                check(result, fake);
            });
        }
        private static void RejectedBeforeProbe(SoftwareEnvironmentResult result, SourceSession fake)
        { Equal(SoftwareEnvironmentStatus.Failed, result.Status); Equal(0, fake.ActualProbes); Equal(1, fake.UpdateCalls); Restored(result, fake); }
        private static void Restored(SoftwareEnvironmentResult result, SourceSession fake)
        { Equal(true, result.RecoveryAttempted); Equal(true, result.RecoverySucceeded); Equal(fake.Original, fake.Current); }
        private static void Contains(string text, string value) { Equal(true, text.Contains(value)); }
        private static void Before(string text, string first, string second)
        { int a = text.IndexOf(first, StringComparison.Ordinal), b = text.IndexOf(second, StringComparison.Ordinal); Equal(true, a >= 0 && b > a); }
        private static void Equal<T>(T expected, T actual)
        { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
        private sealed class ProgressCapture : IProgress<SoftwareProgress>
        {
            private readonly List<string> logs;
            public ProgressCapture(List<string> logs) { this.logs = logs; }
            public void Report(SoftwareProgress value) { if (value.Log != null) logs.Add(value.Log); }
        }
        private sealed class SourceSession : ISoftwareSourceSession
        {
            private readonly PackageManagerKind manager;
            private readonly SoftwareSourceInfo[] candidates = {
                new SoftwareSourceInfo { Identity = "Tencent", Name = "Tencent", Url = "https://tencent.test/openwrt/" },
                new SoftwareSourceInfo { Identity = "Tsinghua", Name = "Tsinghua", Url = "https://tsinghua.test/openwrt/" } };
            public readonly string Rom, Original;
            public string Current, Identity = "Tencent", PostBody = "{\"success\":false}";
            public bool LeaveUnchanged, KeepOldFeeds, WrongIdentity, CurrentReadFails, ConfigReadFails, MissingFeed, WrongFeedArchitecture;
            public bool ActualProbePartial, UpdateFails, UpdateConflict, ThrowAfterMutation;
            public int ActualProbes, UpdateCalls;
            public readonly List<string> Switches = new List<string>(), Logs = new List<string>();
            private bool candidatePosted;
            private string backup;
            public SourceSession(PackageManagerKind manager)
            {
                this.manager = manager;
                Rom = string.Join("\n", new[] { "base", "luci", "packages", "routing", "telephony" }.Select(name =>
                    (manager == PackageManagerKind.OPKG ? "src/gz openwrt_" + name + " " : "") +
                    "https://downloads.test/releases/24.10.8/packages/aarch64_cortex-a53/" + name +
                    (manager == PackageManagerKind.APK ? "/packages.adb" : ""))) + "\n";
                Original = "# preserve original Tencent comment\n" + SoftwareSourceService.RebaseRom(Rom, candidates[0].Url);
                Current = Original;
            }
            public ProbeResult ReadOfficial(PackageManagerKind kind, bool rom)
            { Equal(manager, kind); return !rom && candidatePosted && ConfigReadFails ? new ProbeResult { ExitStatus = 1 } : Ok(rom ? Rom : Current); }
            public IDictionary<string, string> ReadAdditional(PackageManagerKind kind) { Equal(manager, kind); return new Dictionary<string, string>(); }
            public ProbeResult QuickStartGet(bool list)
            {
                if (!list && candidatePosted && CurrentReadFails) return new ProbeResult { ExitStatus = 7 };
                object payload = list ? (object)new { softSourceList = candidates.Select(c => new { identity = c.Identity, name = c.Name, url = c.Url }).ToArray() } :
                    new { softSource = new { identity = Identity, name = Identity, url = candidates.First(c => c.Identity == Identity).Url } };
                return Ok(new JavaScriptSerializer().Serialize(new { success = true, result = payload }));
            }
            public ProbeResult QuickStartSwitch(string identity)
            {
                Switches.Add(identity); candidatePosted = identity == "Tsinghua";
                if (!candidatePosted) { Identity = identity; Current = SoftwareSourceService.RebaseRom(Rom, candidates[0].Url); return Ok(PostBody); }
                if (LeaveUnchanged) return Ok(PostBody);
                Identity = WrongIdentity ? "Tencent" : identity;
                Current = KeepOldFeeds ? Original : SoftwareSourceService.RebaseRom(Rom, candidates[1].Url);
                if (MissingFeed) Current = string.Join("\n", Current.TrimEnd().Split('\n').Skip(1)) + "\n";
                if (WrongFeedArchitecture) Current = Current.Replace("/aarch64_cortex-a53/", "/x86_64/");
                if (ThrowAfterMutation) throw new InvalidOperationException("lost POST response after actual mutation");
                return Ok(PostBody);
            }
            public ProbeResult ProbeIndex(string url)
            {
                var uri = new Uri(url); bool actual = candidatePosted && uri.Host == "tsinghua.test";
                if (actual) ActualProbes++;
                bool pass = uri.Host != "tencent.test" && !(actual && ActualProbePartial && uri.AbsolutePath.Contains("/base/"));
                return Ok("ISTORE_PROBE|" + (pass ? "206" : "404") + "|0.100");
            }
            public PackageUpdateResult Update(PackageManagerKind kind)
            {
                Equal(manager, kind); UpdateCalls++;
                if (candidatePosted && UpdateConflict) return new PackageUpdateResult { ExitCode = 1, Stderr = "ERROR: version conflict" };
                if (!candidatePosted || UpdateFails)
                {
                    string url = SoftwareSourceService.ParseFeeds(Current, kind, SoftwareSourceCommands.DistfeedsPath(kind), RepositoryKind.Official)[0].IndexUrl;
                    return new PackageUpdateResult { ExitCode = 1, Stderr = "Failed to download " + url + ": connection refused" };
                }
                return new PackageUpdateResult { ExitCode = 0, Stdout = "Package indexes downloaded. Signature check passed." };
            }
            public string BackupOfficial(PackageManagerKind kind) { Equal(manager, kind); backup = Current; return "/tmp/istore-plugin-helper.ABCDEFGH/distfeeds.backup"; }
            public ProbeResult ReadBackup(string path) { return Ok(backup); }
            public ProbeResult RestoreBackup(PackageManagerKind kind, string path) { Equal(manager, kind); Current = backup; Identity = "Tencent"; candidatePosted = false; return Ok(""); }
            public string Sanitize(string text) { return text ?? ""; }
            private static ProbeResult Ok(string text) { return new ProbeResult { ExitStatus = 0, Output = text, Error = "" }; }
        }
    }
}
