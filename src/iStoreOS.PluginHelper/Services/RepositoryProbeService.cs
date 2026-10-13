using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class RepositoryProbeService
    {
        public RepositoryHealthResult Probe(ISoftwareSourceSession session, IList<RepositoryFeed> feeds, Action<string, string> report, CancellationToken token, string label)
        {
            var health = new RepositoryHealthResult();
            for (int i = 0; i < feeds.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                RepositoryFeed feed = feeds[i];
                string step = "正在检查" + label + " " + (i + 1) + "/" + feeds.Count + "…";
                report(step, null);
                RepositoryProbeResult result;
                var watch = Stopwatch.StartNew();
                if (feed.ParseError != null) result = new RepositoryProbeResult { Feed = feed, ExitCode = -1, ErrorCategory = SoftwareErrorCategory.RepoIndex, ErrorMessage = feed.ParseError };
                else
                {
                    try { result = Parse(session.ProbeIndex(feed.IndexUrl), feed); }
                    catch (Exception exception) { result = new RepositoryProbeResult { Feed = feed, ExitCode = -1, ErrorCategory = SoftwareErrorCategory.Transport, ErrorMessage = session.Sanitize(exception.Message) }; }
                }
                if (result.ResponseMilliseconds <= 0) result.ResponseMilliseconds = watch.Elapsed.TotalMilliseconds;
                health.Probes.Add(result);
                string kind = feed.Kind == RepositoryKind.FixedAuxiliary ? "固定辅助源" : feed.Kind == RepositoryKind.Custom ? "自定义源" : "官方源";
                report(step, kind + " " + feed.ConfigPath + " | " + (feed.IndexUrl ?? feed.Name) + " | " + (result.Success ? "成功" : "失败") +
                    " | HTTP " + result.HttpStatus + " | exit " + result.ExitCode + " | " + result.ResponseMilliseconds.ToString("F0") + " ms" +
                    (result.Success ? "" : " | " + result.ErrorCategory + ": " + result.ErrorMessage));
            }
            return health;
        }
        public static RepositoryProbeResult Parse(ProbeResult response, RepositoryFeed feed)
        {
            Match match = Regex.Match(response.Output ?? "", @"ISTORE_PROBE\|(\d{3})\|([0-9.]+)");
            int status = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
            double seconds; double.TryParse(match.Success ? match.Groups[2].Value : "", NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
            bool success = response.ExitStatus == 0 && status >= 200 && status < 400;
            string reason = (response.Error ?? "").Trim();
            if (!success && reason.Length == 0) reason = "索引请求失败（HTTP " + status + "，curl exit " + response.ExitStatus + "）";
            SoftwareErrorCategory category = success ? SoftwareErrorCategory.None : response.ExitStatus == 28 ? SoftwareErrorCategory.Timeout : SoftwareErrorCategory.Transport;
            return new RepositoryProbeResult { Feed = feed, Success = success, HttpStatus = status, ExitCode = response.ExitStatus, ResponseMilliseconds = seconds * 1000, ErrorCategory = category, ErrorMessage = reason };
        }
    }
}
