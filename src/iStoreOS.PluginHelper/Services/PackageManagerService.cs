using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class PackageManagerService
    {
        private const string ErrorPattern = @"(?im)\b(error|fatal|warning)\s*:|failed to (download|fetch|open|update)|download (failed|error)|index.*(invalid|failed)|invalid.*index|signature.*(invalid|failed|error)|UNTRUSTED|BAD signature|repository.*(unavailable|error)|unable to (fetch|download)|collected errors|architecture.*mismatch|SDK.*mismatch|kernel.*ABI.*mismatch|version conflict|replacement conflict|package.*not found|cannot satisfy.*depend";
        public PackageUpdateResult Update(ISoftwareSourceSession session, PackageManagerKind manager, IList<RepositoryFeed> official, IList<RepositoryFeed> additional, Action<string, string> report)
        {
            string step = "正在执行 " + (manager == PackageManagerKind.APK ? "apk update" : "opkg update") + "…";
            report(step, "开始真实 package update（远程进程 120 秒，SSH 命令 125 秒上限）。");
            PackageUpdateResult result = session.Update(manager);
            Analyze(result, official, additional);
            result.Stdout = session.Sanitize(result.Stdout ?? ""); result.Stderr = session.Sanitize(result.Stderr ?? "");
            report(step, "package update stdout：\n" + result.Stdout);
            report(step, "package update stderr：\n" + result.Stderr);
            report(step, "package update exit=" + result.ExitCode + "；" + (result.Success ? "成功" : "失败：" + result.ErrorCategory));
            return result;
        }
        public static void Analyze(PackageUpdateResult result, IList<RepositoryFeed> official, IList<RepositoryFeed> additional)
        {
            string text = (result.Stdout ?? "") + "\n" + (result.Stderr ?? "");
            result.TimedOut = result.TimedOut || result.ExitCode == 124 || (result.ExitCode == 143 && text.Contains("Terminated"));
            bool outputError = Regex.IsMatch(text, ErrorPattern);
            result.Success = result.ExitCode == 0 && !result.TimedOut && !outputError;
            result.ErrorCategory = result.Success ? SoftwareErrorCategory.None : Classify(text, result.TimedOut);
            result.OfficialRepositoryFailure = HasFeedFailure(result.Stdout ?? "", official) || HasFeedFailure(result.Stderr ?? "", official);
            result.AdditionalRepositoryFailure = HasFeedFailure(result.Stdout ?? "", additional) || HasFeedFailure(result.Stderr ?? "", additional);
        }
        public static SoftwareErrorCategory Classify(string text, bool timeout)
        {
            if (timeout || Regex.IsMatch(text, @"(?i)timed out|timeout")) return SoftwareErrorCategory.Timeout;
            if (Regex.IsMatch(text, @"(?i)architecture.*(mismatch|incompatible)|incompatible.*architecture|SDK.*mismatch|kernel.*ABI|kernel.*(mismatch|incompatible)")) return SoftwareErrorCategory.Compatibility;
            if (Regex.IsMatch(text, @"(?i)version conflict|replacement conflict|conflicts with|trying to overwrite")) return SoftwareErrorCategory.Conflict;
            if (Regex.IsMatch(text, @"(?i)package.*not found|no such package|dependency.*(not available|unavailable)|cannot satisfy.*depend")) return SoftwareErrorCategory.PackageAvailability;
            if (Regex.IsMatch(text, @"(?i)signature|UNTRUSTED|invalid.*index|index.*invalid|repository error")) return SoftwareErrorCategory.RepoIndex;
            if (Regex.IsMatch(text, @"(?i)could not resolve|DNS|bad address|temporary error|network|connection refused|TLS|SSL|HTTP.*[45]\d\d|404|failed to download|failed to fetch|fetch.*(error|failed)")) return SoftwareErrorCategory.Transport;
            return SoftwareErrorCategory.Unknown;
        }
        private static bool HasFeedFailure(string text, IList<RepositoryFeed> feeds)
        {
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], ErrorPattern) && !Regex.IsMatch(lines[i], @"(?i)temporary error|404|signature|timed out|timeout")) continue;
                // Include the immediately preceding fetch line for two-line diagnostics.
                string context = lines[i];
                if (i > 0 && !Regex.IsMatch(context, @"https?://")) context = lines[i - 1] + "\n" + context;
                if (feeds.Any(feed => !string.IsNullOrEmpty(feed.IndexUrl) && context.Contains(feed.IndexUrl))) return true;
            }
            return false;
        }
        public static bool IsNonSourceFailure(PackageUpdateResult result)
        { return result.ErrorCategory == SoftwareErrorCategory.Compatibility || result.ErrorCategory == SoftwareErrorCategory.PackageAvailability || result.ErrorCategory == SoftwareErrorCategory.Conflict; }
    }
}
