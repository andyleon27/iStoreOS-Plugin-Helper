using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // Owned by one operation only. All HTTP calls remain serial and share the full-probe parser.
    public sealed class RepositoryProbeRound
    {
        public const int FastCandidateLimit = 5;
        public const int GoodEnoughMilliseconds = 1200;
        public const int FastSelectionBudgetMilliseconds = 9000;
        private readonly Dictionary<string, RepositoryProbeResult> cache = new Dictionary<string, RepositoryProbeResult>(StringComparer.Ordinal);
        private readonly Dictionary<string, RepositoryProbeResult> fastCache = new Dictionary<string, RepositoryProbeResult>(StringComparer.Ordinal);
        internal readonly InstallationTiming Timing;
        public RepositoryProbeRound() : this(new InstallationTiming()) { }
        public RepositoryProbeRound(InstallationTiming timing) { Timing = timing; }
        public static string NormalizeUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ?
                uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimEnd('/') : (url ?? "").TrimEnd('/');
        }
        public static RepositoryFeed Canary(IList<RepositoryFeed> feeds)
        {
            var valid = feeds.Where(f => f.Kind == RepositoryKind.Official && f.ParseError == null && !string.IsNullOrEmpty(f.IndexUrl)).ToList();
            return valid.FirstOrDefault(f => new Uri(f.IndexUrl).AbsolutePath.EndsWith("/base/Packages.gz", StringComparison.OrdinalIgnoreCase) ||
                new Uri(f.IndexUrl).AbsolutePath.EndsWith("/base/packages.adb", StringComparison.OrdinalIgnoreCase)) ?? valid.FirstOrDefault() ??
                feeds.FirstOrDefault(f => f.Kind == RepositoryKind.Official);
        }
        public RepositoryHealthResult ProbeCanary(ISoftwareSourceSession session, string sourceUrl, IList<RepositoryFeed> feeds,
            Action<string, string> report, CancellationToken token, string label)
        {
            var official = feeds.Where(f => f.Kind == RepositoryKind.Official).ToList();
            var health = new RepositoryHealthResult { ExpectedTotal = official.Count };
            var canary = Canary(official);
            if (canary != null) health.Probes.Add(Probe(session, sourceUrl, canary, report, token, label + " Canary"));
            report(label, "Canary 仅判断可达性；完整官方索引数=" + official.Count + "，真实 package update 仍是最终 Gate。");
            return health;
        }
        public RepositoryHealthResult Verify(ISoftwareSourceSession session, string sourceUrl, IList<RepositoryFeed> feeds,
            Action<string, string> report, CancellationToken token, string label)
        {
            var health = ProbeCanary(session, sourceUrl, feeds, report, token, label);
            if (health.Probes.Count == 0 || !health.Probes[0].Success) return health;
            var canary = Canary(feeds);
            foreach (var feed in feeds.Where(f => f.Kind == RepositoryKind.Official && !object.ReferenceEquals(f, canary)))
            {
                var result = Probe(session, sourceUrl, feed, report, token, label + " Full Verification");
                health.Probes.Add(result);
                if (!result.Success) break;
            }
            return health;
        }
        public RepositoryProbeResult ProbeFastCanary(ISoftwareSourceSession session, string sourceUrl, IList<RepositoryFeed> feeds,
            Action<string, string> report, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var feed = Canary(feeds); if (feed == null) return null;
            string key = Key(sourceUrl, feed); RepositoryProbeResult result;
            if (!fastCache.TryGetValue(key, out result))
            {
                result = new RepositoryProbeService().ProbeOne(session, feed, true); fastCache.Add(key, result);
                RepositoryProbeService.Report(result, report, "Fast Canary");
                // Success is valid for full verification. A short timeout must not poison normal fallback.
                if (result.Success) cache[key] = result;
                else report("Fast Canary", "快速探测未通过，仅降低优先级；正常超时 fallback 仍可尝试。");
            }
            return result;
        }
        private static string Key(string sourceUrl, RepositoryFeed feed)
        { return NormalizeUrl(sourceUrl) + "\n" + NormalizeUrl(feed.IndexUrl) + "\n" + feed.ParseError; }
        private RepositoryProbeResult Probe(ISoftwareSourceSession session, string sourceUrl, RepositoryFeed feed,
            Action<string, string> report, CancellationToken token, string label)
        {
            token.ThrowIfCancellationRequested();
            string key = Key(sourceUrl, feed);
            RepositoryProbeResult result;
            if (!cache.TryGetValue(key, out result))
            {
                report(label, null);
                result = new RepositoryProbeService().ProbeOne(session, feed);
                cache.Add(key, result);
                RepositoryProbeService.Report(result, report, label);
            }
            else report(label, "复用本轮索引结果：" + (feed.IndexUrl ?? feed.Name));
            return result;
        }
    }

    // Enumeration is deliberately lazy: a FULL source suspends discovery until its real update/install fails.
    internal sealed class OfficialSourceSearch
    {
        private sealed class Choice { public SoftwareSourceInfo Source; public List<RepositoryFeed> Feeds; public RepositoryProbeResult Fast; }
        private readonly SoftwareSourceSnapshot snapshot;
        private readonly PackageManagerKind manager;
        private readonly RepositoryProbeRound round;
        public OfficialSourceSearch(SoftwareSourceSnapshot snapshot, PackageManagerKind manager, RepositoryProbeRound round)
        { this.snapshot = snapshot; this.manager = manager; this.round = round; }
        private static string FeedKey(IEnumerable<RepositoryFeed> feeds)
        { return string.Join("\n", feeds.Select(f => RepositoryProbeRound.NormalizeUrl(f.IndexUrl) + "|" + f.ParseError).OrderBy(s => s, StringComparer.Ordinal)); }
        public IEnumerable<SoftwareRepairPlan> Find(ISoftwareSourceSession session, Func<IList<SoftwareSourceInfo>> load,
            Action<string, string> report, CancellationToken token, List<SoftwareRepairPlan> observed)
        {
            if (string.IsNullOrWhiteSpace(snapshot.RomText)) yield break;
            var mirrors = new HashSet<string>(StringComparer.Ordinal);
            var configurations = new HashSet<string>(StringComparer.Ordinal) { FeedKey(snapshot.OfficialFeeds) };
            if (snapshot.CurrentSource != null && snapshot.OfficialFeeds.Count > 0 && snapshot.OfficialFeeds.All(f =>
                f.ParseError == null && NormalizeIndex(f.IndexUrl).StartsWith(RepositoryProbeRound.NormalizeUrl(snapshot.CurrentSource.Url) + "/", StringComparison.Ordinal)))
                mirrors.Add(RepositoryProbeRound.NormalizeUrl(snapshot.CurrentSource.Url));
            var choices = new List<Choice>();
            using (round.Timing.Measure("CandidateSelection"))
            {
                // OrderBy is stable: preserve QuickStart order within each scheme.
                foreach (var source in load().OrderBy(s => new Uri(s.Url).Scheme == "https" ? 0 : 1))
                {
                    token.ThrowIfCancellationRequested();
                    if (!mirrors.Add(RepositoryProbeRound.NormalizeUrl(source.Url))) continue;
                    string text = SoftwareSourceService.RebaseRom(snapshot.RomText, source.Url);
                    var feeds = SoftwareSourceService.ParseFeeds(text, manager, snapshot.DistfeedsPath, RepositoryKind.Official);
                    if (configurations.Add(FeedKey(feeds))) choices.Add(new Choice { Source = source, Feeds = feeds });
                }
            }
            var verified = new HashSet<Choice>();
            Func<Choice, SoftwareRepairPlan> verify = choice => {
                RepositoryHealthResult health;
                using (round.Timing.Measure("CandidateFullVerify"))
                    health = round.Verify(session, choice.Source.Url, choice.Feeds, report, token, "候选 " + choice.Source.Identity);
                var plan = new SoftwareRepairPlan { OriginalSource = snapshot.CurrentSource, ProposedSource = choice.Source, Probe = health };
                verified.Add(choice); observed.Add(plan); return plan;
            };
            var budget = new Stopwatch();
            foreach (var choice in choices.Where(c => new Uri(c.Source.Url).Scheme == "https").Take(RepositoryProbeRound.FastCandidateLimit))
            {
                // Reserve the full three-second curl budget before starting another serial canary.
                if (budget.Elapsed.TotalMilliseconds + 3000 > RepositoryProbeRound.FastSelectionBudgetMilliseconds) break;
                budget.Start();
                try { using (round.Timing.Measure("CandidateSelection"))
                    choice.Fast = round.ProbeFastCanary(session, choice.Source.Url, choice.Feeds, report, token); }
                finally { budget.Stop(); }
                if (choice.Fast != null && choice.Fast.Success && choice.Fast.ResponseMilliseconds <= RepositoryProbeRound.GoodEnoughMilliseconds)
                {
                    report("Candidate Selection", "Good Enough：" + choice.Source.Identity + "；" + choice.Fast.ResponseMilliseconds.ToString("F0") + " ms，立即完整验证。");
                    var plan = verify(choice);
                    if (plan.Probe.Status == RepositoryHealth.FULL) yield return plan;
                }
            }
            foreach (var choice in choices.Where(c => !verified.Contains(c) && c.Fast != null && c.Fast.Success).OrderBy(c => c.Fast.ResponseMilliseconds))
            {
                var plan = verify(choice);
                if (plan.Probe.Status == RepositoryHealth.FULL) yield return plan;
            }
            // Fast timeout is not DOWN: normal serial probing retains the original slow-network compatibility.
            foreach (var choice in choices.Where(c => !verified.Contains(c)))
            {
                var plan = verify(choice);
                if (plan.Probe.Status == RepositoryHealth.FULL) yield return plan;
            }
        }
        private static string NormalizeIndex(string url) { return RepositoryProbeRound.NormalizeUrl(url); }
    }
}
