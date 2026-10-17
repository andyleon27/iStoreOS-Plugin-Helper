using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static partial class WorkflowTests
    {
        private static void SourceTests(Action<string, Action> test)
        {
            foreach (var kind in new[] { PackageManagerKind.APK, PackageManagerKind.OPKG })
            {
                var manager = kind;
                SourceCase(test, manager, "healthy current canary then update never loads candidates", f => { }, (r, f) => {
                    Equal(InstallationState.Success, r.State); Equal(1, f.Probes); Equal(0, f.CandidateReads); Equal(1, f.UpdateCalls); Equal(1, f.Starts);
                    Equal(RepositoryHealth.PARTIAL, r.Software.OfficialHealth.Status); Equal(5, r.Software.OfficialHealth.Total);
                });
                SourceCase(test, manager, "current canary failure skips four remaining indexes", f => f.BadHosts.Add("old.test"), (r, f) => {
                    Equal(InstallationState.Success, r.State); Equal(1, Count(f, "old.test")); Equal(5, Count(f, "a.test")); Equal(6, f.Probes);
                });
                SourceCase(test, manager, "Tencent down Alibaba down CERNET full is exactly seven probes", f => {
                    f.BadHosts.UnionWith(new[] { "old.test", "a.test" });
                    f.candidates = f.candidates.Concat(Enumerable.Range(1, 5).Select(i => Source("Later" + i, "https://later" + i + ".test/openwrt/"))).ToArray();
                }, (r, f) => {
                    Equal(InstallationState.Success, r.State); Equal(7, f.Probes); Equal(1, Count(f, "old.test")); Equal(1, Count(f, "a.test")); Equal(5, Count(f, "b.test"));
                    Equal(0, f.ProbeUrls.Count(u => new Uri(u).Host.StartsWith("later"))); Equal("B", f.Switches.Single());
                });
                SourceCase(test, manager, "candidate canary reused and full stops before B", f => f.BadHosts.Add("old.test"), (r, f) => {
                    Equal(5, Count(f, "a.test")); Equal(0, Count(f, "b.test")); Equal(f.Probes, f.ProbeUrls.Distinct().Count());
                    Equal(5, r.Software.Candidates.Single().Probe.Successful); Equal(RepositoryHealth.FULL, r.Software.Candidates.Single().Probe.Status);
                });
                SourceCase(test, manager, "partial candidate stops at failing remaining feed then advances", f => {
                    f.BadHosts.Add("old.test"); f.BadUrls.Add(Index(f, "a.test", "luci"));
                }, (r, f) => {
                    Equal(InstallationState.Success, r.State); Equal(2, Count(f, "a.test")); Equal(5, Count(f, "b.test")); Equal("B", f.Switches.Single());
                    Equal(RepositoryHealth.PARTIAL, r.Software.Candidates[0].Probe.Status); Equal(5, r.Software.Candidates[0].Probe.Total);
                });
                SourceCase(test, manager, "normalized duplicate mirror aliases are attempted once", f => {
                    f.BadHosts.UnionWith(new[] { "old.test", "a.test" });
                    f.candidates = new[] { f.candidates[0], f.candidates[1], Source("Alias", "https://A.TEST:443/openwrt"), f.candidates[2] };
                }, (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, Count(f, "a.test")); Equal(7, f.Probes); });
                SourceCase(test, manager, "failed current duplicate list entry is never reprobed", f => {
                    f.BadHosts.Add("old.test"); f.candidates = f.candidates.Concat(new[] { Source("OldAlias", "https://OLD.TEST:443/openwrt") }).ToArray();
                }, (r, f) => { Equal(1, Count(f, "old.test")); Equal(1, f.CandidateReads); Equal(6, f.Probes); });
                SourceCase(test, manager, "switched full source has no repeated HTTP requests", f => f.BadHosts.Add("old.test"), (r, f) => {
                    Equal(1, f.UpdateCalls); Equal(5, Count(f, "a.test")); Equal(1, f.Starts); True(r.SourceRestored);
                });
                SourceCase(test, manager, "identity mismatch blocks update and dispatch", f => {
                    f.BadHosts.Add("old.test"); f.SwitchKeepsOldIdentity = true;
                }, (r, f) => { Equal(InstallationFailureKind.Tool, r.Failure.Kind); Equal(0, f.UpdateCalls); Equal(0, f.Starts); True(r.SourceRestored); });
                SourceCase(test, manager, "complete distfeeds mismatch blocks update and dispatch", f => {
                    f.BadHosts.Add("old.test"); f.SwitchKeepsOldFeeds = true;
                }, (r, f) => { Equal(InstallationFailureKind.Tool, r.Failure.Kind); Equal(0, f.UpdateCalls); Equal(0, f.Starts); True(r.SourceRestored); });
                SourceCase(test, manager, "current update official failure lazily loads next source", f => f.Updates.Enqueue(UpdateError(Index(f, "old.test", "base"))), (r, f) => {
                    Equal(InstallationState.Success, r.State); Equal(6, f.Probes); Equal(2, f.UpdateCalls); Equal(1, f.Starts); Equal("A", f.Switches.Single());
                });
                SourceCase(test, manager, "full candidate update official failure resumes unattempted next", f => {
                    f.BadHosts.Add("old.test"); f.Updates.Enqueue(UpdateError(Index(f, "a.test", "base")));
                }, (r, f) => {
                    Equal(InstallationState.Success, r.State); Equal(11, f.Probes); Equal(2, f.UpdateCalls); Equal("A,B", string.Join(",", f.Switches)); Equal(1, f.Starts);
                    Equal(f.Probes, f.ProbeUrls.Distinct().Count());
                });
                SourceCase(test, manager, "full candidate non-source update failure stops rotation", f => {
                    f.BadHosts.Add("old.test"); f.Updates.Enqueue(new PackageUpdateResult { ExitCode = 1, Stderr = "package conflicts with installed package" });
                }, (r, f) => { Equal(InstallationFailureKind.PackageConflict, r.Failure.Kind); Equal(0, Count(f, "b.test")); Equal(1, f.UpdateCalls); Equal(0, f.Starts); });
                SourceCase(test, manager, "installer official failure resumes lazy search once", f => {
                    f.BadHosts.Add("old.test"); f.Runs.Enqueue(new Outcome { Exit = 1, Log = "failed to download " + Index(f, "a.test", "base") + ": connection timeout" });
                }, (r, f) => { Equal(InstallationState.Success, r.State); Equal(2, f.Starts); Equal(11, f.Probes); Equal("A,B", string.Join(",", f.Switches)); });
                SourceCase(test, manager, "HTTPS precedes earlier HTTP and preserves dynamic order", f => {
                    f.BadHosts.Add("old.test"); f.candidates = new[] { f.candidates[0], Source("Http", "http://http.test/openwrt/"), f.candidates[2], f.candidates[1] };
                }, (r, f) => { Equal("B", f.Switches.Single()); Equal(0, Count(f, "http.test")); Equal(0, Count(f, "a.test")); Equal(6, f.Probes); });
                SourceCase(test, manager, "all canaries down exhaust unique mirrors without update", f => f.BadHosts.UnionWith(new[] { "old.test", "a.test", "b.test" }), (r, f) => {
                    Equal(InstallationFailureKind.MirrorsExhausted, r.Failure.Kind); Equal(5, f.Probes); Equal(0, f.UpdateCalls); Equal(0, f.Starts); // Current normal + two fast failures + two normal fallback failures.
                });
                SourceCase(test, manager, "additional index failure does not select official mirrors", f => f.BadHosts.Add("compat.test"), (r, f) => {
                    Equal(InstallationState.Success, r.State); Equal(1, f.Probes); Equal(0, f.CandidateReads); Equal(1, f.UpdateCalls); Equal(0, f.Switches.Count);
                });
                SourceCase(test, manager, "additional real update failure stops without candidate list", f => f.Updates.Enqueue(UpdateError(manager == PackageManagerKind.APK ? Compat : "https://compat.test/repo/Packages.gz")), (r, f) => {
                    Equal(InstallationFailureKind.AdditionalRepository, r.Failure.Kind); Equal(0, f.CandidateReads); Equal(0, f.Starts); Equal(1, f.Probes);
                });
                SourceCase(test, manager, "base preferred even when not first configured feed", f => {
                    var lines = f.Current.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries); f.Current = string.Join("\n", lines.Reverse()) + "\n"; f.Original = f.Current;
                }, (r, f) => { Equal(Index(f, "old.test", "base"), f.ProbeUrls.Single()); Equal(InstallationState.Success, r.State); });
                SourceCase(test, manager, "no ROM template still permits healthy fast path", f => f.Rom = "", (r, f) => {
                    Equal(InstallationState.Success, r.State); Equal(0, f.CandidateReads); Equal(1, f.Probes);
                });
            }
            test("V1.0.2 r3 cache reuses both failure and success within round only", () => {
                var f = new Fake(); var feeds = Feeds(f.Current); var round = new RepositoryProbeRound();
                f.BadHosts.Add("old.test"); Equal(RepositoryHealth.DOWN, round.Verify(f, "https://OLD.test:443/openwrt/", feeds, IgnoreProbe, CancellationToken.None, "test").Status);
                f.BadHosts.Clear(); Equal(RepositoryHealth.DOWN, round.Verify(f, "https://old.test/openwrt", feeds, IgnoreProbe, CancellationToken.None, "test").Status); Equal(1, f.Probes);
                Equal(RepositoryHealth.FULL, new RepositoryProbeRound().Verify(f, "https://old.test/openwrt", feeds, IgnoreProbe, CancellationToken.None, "test").Status); Equal(2, f.Probes);
                round = new RepositoryProbeRound(); round.Verify(f, "https://old.test/openwrt", feeds, IgnoreProbe, CancellationToken.None, "test");
                f.BadHosts.Add("old.test"); Equal(RepositoryHealth.FULL, round.Verify(f, "https://old.test/openwrt", feeds, IgnoreProbe, CancellationToken.None, "test").Status); Equal(3, f.Probes);
            });
            test("V1.0.2 r3 canary excludes auxiliary feeds and falls back to valid official", () => {
                var feeds = new List<RepositoryFeed> { new RepositoryFeed { Kind = RepositoryKind.Custom, IndexUrl = "https://custom.test/base/Packages.gz" },
                    new RepositoryFeed { Kind = RepositoryKind.Official, ParseError = "bad" },
                    new RepositoryFeed { Kind = RepositoryKind.Official, IndexUrl = "https://official.test/luci/Packages.gz" } };
                Equal(feeds[2], RepositoryProbeRound.Canary(feeds));
                Equal(null, RepositoryProbeRound.Canary(feeds.Take(1).ToList()));
            });
            test("V1.0.2 r3 malformed remaining official feed prevents FULL without HTTP", () => {
                var f = new Fake(); var feeds = Feeds(f.Current); feeds.Add(new RepositoryFeed { Kind = RepositoryKind.Official, ParseError = "bad feed" });
                var h = new RepositoryProbeRound().Verify(f, "https://old.test/openwrt", feeds, IgnoreProbe, CancellationToken.None, "test");
                Equal(RepositoryHealth.PARTIAL, h.Status); Equal(1, f.Probes); Equal(2, h.Total);
            });
        }
        private static void IgnoreProbe(string step, string log) { }
        private static PackageUpdateResult UpdateError(string url) { return new PackageUpdateResult { ExitCode = 1, Stderr = "failed to download " + url + ": connection timeout" }; }
        private static int Count(Fake f, string host) { return f.ProbeUrls.Count(u => new Uri(u).Host == host); }
        private static SoftwareSourceInfo Source(string id, string url) { return new SoftwareSourceInfo { Identity = id, Name = id, Url = url }; }
        private static string Index(Fake f, string host, string feed)
        { return "https://" + host + "/openwrt/releases/" + f.RouterSdk + "/packages/" + f.RouterArchitecture + "/" + feed + (f.Manager == PackageManagerKind.APK ? "/packages.adb" : "/Packages.gz"); }
        private static void SourceCase(Action<string, Action> test, PackageManagerKind manager, string name, Action<Fake> setup, Action<InstallationWorkflowResult, Fake> check)
        {
            string filename = manager == PackageManagerKind.APK ? Name : "PassWall_26.9.16_aarch64_a53_all_sdk_24.10.run";
            test("V1.0.2 r3 " + manager + " " + name, () => Fixture(filename, path => {
                var f = new Fake { Manager = manager };
                if (manager == PackageManagerKind.OPKG) { f.RouterArchitecture = "aarch64_cortex-a53"; f.ArchiveArchitecture = "aarch64_a53"; f.RouterSdk = "24.10"; f.RouterVersion = "24.10.8"; }
                f.Rom = string.Join("\n", new[] { "base", "luci", "packages", "routing", "telephony" }.Select(feed =>
                    (manager == PackageManagerKind.OPKG ? "src/gz openwrt_" + feed + " " : "") + "https://downloads.test/releases/" + f.RouterSdk + "/packages/" + f.RouterArchitecture + "/" + feed + (manager == PackageManagerKind.APK ? "/packages.adb" : ""))) + "\n";
                f.Original = "# exact original\n" + SoftwareSourceService.RebaseRom(f.Rom, "https://old.test/openwrt/"); f.Current = f.Original;
                setup(f); check(Run(Service(), f, path), f);
            }));
        }
    }
}
