using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class SoftwareSourceService
    {
        public SoftwareSourceSnapshot Read(ISoftwareSourceSession session, PackageManagerKind manager, Action<string> log)
        {
            string path = SoftwareSourceCommands.DistfeedsPath(manager);
            log("当前 distfeeds 路径：" + path);
            ProbeResult current = session.ReadOfficial(manager, false);
            if (current.ExitStatus != 0) throw new InvalidOperationException("无法读取当前官方源配置，退出码 " + current.ExitStatus);
            ProbeResult rom = session.ReadOfficial(manager, true);
            if (rom.ExitStatus != 0) log("ROM 官方源模板不可读；本次仍可检测当前源，但无法生成修复候选。");
            var additional = new List<RepositoryFeed>();
            foreach (var file in session.ReadAdditional(manager))
            {
                string name = file.Key.Substring(file.Key.LastIndexOf('/') + 1);
                RepositoryKind kind = name.StartsWith("compat", StringComparison.OrdinalIgnoreCase) ? RepositoryKind.FixedAuxiliary : RepositoryKind.Custom;
                List<RepositoryFeed> feeds = ParseFeeds(file.Value, manager, file.Key, kind);
                log("辅助配置：" + file.Key + "；类别：" + (kind == RepositoryKind.FixedAuxiliary ? "固定辅助源" : "自定义 / 额外源") + "；启用条目：" + feeds.Count);
                additional.AddRange(feeds);
            }
            return new SoftwareSourceSnapshot
            {
                DistfeedsPath = path, OriginalText = current.Output ?? "", RomText = rom.ExitStatus == 0 ? rom.Output : null,
                OfficialFeeds = ParseFeeds(current.Output, manager, path, RepositoryKind.Official), AdditionalFeeds = additional
            };
        }
        public static List<RepositoryFeed> ParseFeeds(string content, PackageManagerKind manager, string path, RepositoryKind kind)
        {
            var result = new List<RepositoryFeed>();
            int number = 0;
            foreach (string raw in (content ?? "").Split('\n'))
            {
                string line = raw.Trim(); number++;
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                int comment = line.IndexOf('#'); if (comment >= 0) line = line.Substring(0, comment).Trim();
                if (line.Length == 0) continue;
                string url = null; string name = "行 " + number;
                if (manager == PackageManagerKind.OPKG)
                {
                    var parts = Regex.Split(line, @"\s+");
                    if (parts[0] != "src/gz") continue; // Never interpret general opkg options as feeds.
                    if (parts.Length >= 3) { name = parts[1]; url = parts[2]; }
                }
                else if (manager == PackageManagerKind.APK)
                {
                    Match match = Regex.Match(line, @"https?://\S+");
                    if (match.Success) url = match.Value;
                }
                else throw new NotSupportedException();
                bool valid = ShellArgument.IsHttpUrl(url);
                string index = valid ? manager == PackageManagerKind.OPKG ? url.TrimEnd('/') + "/Packages.gz" : url : null;
                if (valid && manager == PackageManagerKind.APK && !new Uri(url).AbsolutePath.EndsWith(".adb", StringComparison.OrdinalIgnoreCase))
                { valid = false; index = null; }
                result.Add(new RepositoryFeed { Name = name, ConfigPath = path, Kind = kind, IndexUrl = index, ParseError = valid ? null : "未识别有效仓库索引 URL；不会把该条目当作健康源。" });
            }
            return result;
        }
        public static string RebaseRom(string romText, string mirrorUrl)
        {
            if (!ShellArgument.IsHttpUrl(mirrorUrl) || !string.IsNullOrEmpty(new Uri(mirrorUrl).Query)) throw new ArgumentException("Invalid mirror base URL.");
            if (string.IsNullOrWhiteSpace(romText)) throw new ArgumentException("ROM 软件源模板为空。");
            // Same base-boundary behavior as QuickStart. The complete release/target/kmods
            // suffix stays byte-for-byte in the template; no model, SDK or ABI guessing.
            return Regex.Replace(romText, @"https?://[^/\s]+/(?:openwrt/)?", match => mirrorUrl.TrimEnd('/') + "/");
        }
        public static bool MatchesFeeds(string actual, string expected, PackageManagerKind manager)
        {
            List<RepositoryFeed> a = ParseFeeds(actual, manager, "", RepositoryKind.Official);
            List<RepositoryFeed> b = ParseFeeds(expected, manager, "", RepositoryKind.Official);
            return a.Count > 0 && a.Count == b.Count && a.All(f => f.ParseError == null) && b.All(f => f.ParseError == null) &&
                a.Select(f => f.IndexUrl).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(b.Select(f => f.IndexUrl).OrderBy(s => s, StringComparer.Ordinal), StringComparer.Ordinal);
        }
    }
}
