using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static partial class WorkflowTests
    {
        private sealed class PerformanceScope : IDisposable
        { private Action release; public PerformanceScope(Action release) { this.release = release; } public void Dispose() { var action = release; release = null; if (action != null) action(); } }
        private sealed class PerformanceProgress : IProgress<InstallationWorkflowProgress>
        { public readonly List<string> Logs = new List<string>(); public void Report(InstallationWorkflowProgress p) { if (p.Log != null) Logs.Add(p.Log); } }
        private static void PerformanceTests(Action<string, Action> test)
        {
            foreach (var kind in new[] { PackageManagerKind.APK, PackageManagerKind.OPKG })
            {
                var manager = kind;
                SourceCase(test, manager, "r4 slow 3200 ms first candidate loses to 500 ms second", f => {
                    f.BadHosts.Add("old.test"); f.FastLatency["a.test"] = 3200; f.FastLatency["b.test"] = 500;
                }, (r, f) => { Equal(InstallationState.Success, r.State); Equal("B", f.Switches.Single()); Equal(1, Count(f, "a.test")); Equal(5, Count(f, "b.test")); Equal(7, f.Probes); });
                SourceCase(test, manager, "r4 500 ms Good Enough FULL stops all later benchmarking", f => {
                    f.BadHosts.Add("old.test"); f.FastLatency["a.test"] = 500;
                }, (r, f) => { Equal("A", f.Switches.Single()); Equal(1, f.FastProbeUrls.Count); Equal(0, Count(f, "b.test")); Equal(6, f.Probes); });
                SourceCase(test, manager, "r4 no Good Enough chooses lowest successful latency", f => {
                    f.BadHosts.Add("old.test"); f.FastLatency["a.test"] = 2800; f.FastLatency["b.test"] = 1600;
                }, (r, f) => { Equal("B", f.Switches.Single()); Equal(2, f.FastProbeUrls.Count); Equal(1, Count(f, "a.test")); Equal(7, f.Probes); });
                SourceCase(test, manager, "r4 fastest FULL failure tries second fastest without repeating canary", f => {
                    f.BadHosts.Add("old.test"); f.FastLatency["a.test"] = 1800; f.FastLatency["b.test"] = 2200; f.BadUrls.Add(Index(f, "a.test", "luci"));
                }, (r, f) => { Equal("B", f.Switches.Single()); Equal(2, Count(f, "a.test")); Equal(5, Count(f, "b.test")); Equal(8, f.Probes); });
                SourceCase(test, manager, "r4 all fast timeouts retain normal successful fallback", f => {
                    f.BadHosts.Add("old.test"); f.FastTimeoutHosts.UnionWith(new[] { "a.test", "b.test" });
                }, (r, f) => { Equal(InstallationState.Success, r.State); Equal("A", f.Switches.Single()); Equal(2, f.FastProbeUrls.Count); Equal(6, Count(f, "a.test")); Equal(8, f.Probes); });
                SourceCase(test, manager, "r4 normalized duplicates receive one fast canary", f => {
                    f.BadHosts.Add("old.test"); f.FastLatency["a.test"] = 2500; f.FastLatency["b.test"] = 1500;
                    f.candidates = new[] { f.candidates[0], f.candidates[1], Source("Alias", "https://A.TEST:443/openwrt"), f.candidates[2] };
                }, (r, f) => { Equal("B", f.Switches.Single()); Equal(2, f.FastProbeUrls.Count); Equal(1, Count(f, "a.test")); });
                SourceCase(test, manager, "r4 current healthy preserves source without benchmark even if slow", f => f.FastLatency["a.test"] = 1,
                    (r, f) => { Equal(InstallationState.Success, r.State); Equal(0, f.FastProbeUrls.Count); Equal(0, f.CandidateReads); Equal(1, f.Probes); });
                SourceCase(test, manager, "r4 fast selection checks at most five unique HTTPS candidates", f => {
                    f.BadHosts.Add("old.test"); f.candidates = new[] { f.candidates[0] }.Concat(Enumerable.Range(1, 8).Select(i => Source("Fast" + i, "https://fast" + i + ".test/openwrt/"))).ToArray();
                    for (int i = 1; i <= 8; i++) f.FastLatency["fast" + i + ".test"] = 2900 - i * 100;
                }, (r, f) => { Equal("Fast5", f.Switches.Single()); Equal(5, f.FastProbeUrls.Count); Equal(0, Count(f, "fast6.test")); });
                SourceCase(test, manager, "r4 Good Enough partial verification continues to next candidate", f => {
                    f.BadHosts.Add("old.test"); f.FastLatency["a.test"] = 500; f.BadUrls.Add(Index(f, "a.test", "luci"));
                }, (r, f) => { Equal("B", f.Switches.Single()); Equal(2, f.FastProbeUrls.Count); Equal(2, Count(f, "a.test")); });
                SourceCase(test, manager, "r4 monitor scope reuses one connection across ten polls", f => { f.UsePersistentPolling = true; f.PollsBeforeExit = 9; },
                    (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, f.Starts); Equal(1, f.PersistentFiles.Creates); Equal(1, f.PersistentFiles.Connects); Equal(1, f.PersistentFiles.Disposals);
                        True(f.PersistentFiles.ExistsCalls >= 30); True(f.PersistentFiles.Paths.All(p => p.StartsWith(r.Installation.RemoteDirectory + "/"))); });
                SourceCase(test, manager, "r4 monitor SFTP reconnect retains same session and starts once", f => {
                    f.UsePersistentPolling = true; f.PollsBeforeExit = 3; f.PersistentFiles.FailExistsCall = 4;
                }, (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, f.Starts); Equal(2, f.PersistentFiles.Creates); Equal(2, f.PersistentFiles.Disposals);
                    True(f.PersistentFiles.Paths.All(p => p.StartsWith(r.Installation.RemoteDirectory + "/"))); });
                SourceCase(test, manager, "r4 per-access Installer wrappers retain the original monitor owner", f => { f.UsePersistentPolling = true; f.FreshInstallerWrappers = true; f.PollsBeforeExit = 9; },
                    (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, f.Starts); Equal(1, f.PersistentFiles.Creates); Equal(1, f.PersistentFiles.Disposals); });
            }
            test("V1.0.2 r4 pending monitor disposes transport and later reconciles without redispatch", () => Fixture(Name, path => {
                var f = new Fake { UsePersistentPolling = true, FreshInstallerWrappers = true, NeverEnds = true }; var service = Service(); service.Engine.OverallLimit = TimeSpan.Zero;
                var pending = Run(service, f, path); True(pending.PendingInstaller); Equal(1, f.Starts); Equal(1, f.PersistentFiles.Disposals); Equal(0, f.Cleanups);
                f.NeverEnds = false; service.Engine.OverallLimit = TimeSpan.FromSeconds(2);
                var finished = Run(service, f, path); Equal(InstallationState.Success, finished.State); Equal(1, f.Starts); Equal(2, f.PersistentFiles.Creates); Equal(2, f.PersistentFiles.Disposals); Equal(1, f.Cleanups);
            }));
            test("V1.0.2 r4 fast selection wall budget reserves next full three-second canary", () => Fixture(Name, path => {
                var f = new Fake(); f.BadHosts.Add("old.test"); f.FastProbeDelayMilliseconds = 3100;
                f.candidates = f.candidates.Concat(new[] { Source("Later", "https://later.test/openwrt/") }).ToArray();
                f.FastLatency["a.test"] = 2500; f.FastLatency["b.test"] = 1800;
                Equal(InstallationState.Success, Run(Service(), f, path).State); Equal(2, f.FastProbeUrls.Count); Equal("B", f.Switches.Single()); Equal(0, Count(f, "later.test"));
            }));
            test("V1.0.2 r4 fast curl has bounded timeout and preserves normal probe", () => {
                string fast = SoftwareSourceCommands.ProbeIndexFast("https://source.test/base/Packages.gz"), normal = SoftwareSourceCommands.ProbeIndex("https://source.test/base/Packages.gz");
                True(fast.Contains("--connect-timeout 2 --max-time 3")); True(fast.Contains("-r 0-0 --max-filesize 1")); True(normal.Contains("--connect-timeout 5 --max-time 15"));
                True(fast.Contains("--proto-redir '=http,https'"));
            });
            foreach (bool fail in new[] { false, true })
            {
                bool failure = fail;
                test("V1.0.2 r4 timing summary nonnegative on " + (failure ? "preflight failure" : "verified success"), () => Fixture(Name, path => {
                    var f = new Fake(); if (failure) f.Readiness.TmpBytes = 1;
                    var progress = new PerformanceProgress(); var result = Run(Service(), f, path, CancellationToken.None, progress);
                    Equal(failure ? InstallationState.RetryableFailure : InstallationState.Success, result.State);
                    var summary = progress.Logs.Single(l => l.StartsWith("Timing Summary:"));
                    Equal(10, Regex.Matches(summary, @"= [0-9]+ ms").Count);
                    foreach (string stage in new[] { "Preflight / SourceSelect", "PackageUpdate", "UploadIntegrity", "SnapshotBefore", "InstallerRuntime", "SnapshotAfterVerify", "RestoreCleanup", "CandidateSelection", "CandidateFullVerify", "Total" }) True(summary.Contains(stage + " = "));
                }));
            }
        }
    }
}
