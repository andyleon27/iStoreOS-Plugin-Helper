using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public static class PackageSnapshotService
    {
        public static PackageSnapshot Parse(string text, PackageManagerKind manager)
        {
            var result = new PackageSnapshot();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim(); if (line.Length == 0) continue;
                Match match;
                if (manager == PackageManagerKind.APK) match = Regex.Match(line, @"\A(.+)-([0-9][A-Za-z0-9.+_~:-]*)\z");
                else if (manager == PackageManagerKind.OPKG) match = Regex.Match(line, @"\A([^\s]+)\s+-\s+(\S+)\z");
                else throw new ArgumentException("Unsupported package manager.");
                if (!match.Success) throw new InvalidDataException("无法可靠解析 package snapshot：" + line);
                string name = match.Groups[1].Value; PluginInstallCommands.ValidatePackageName(name);
                if (result.Packages.ContainsKey(name)) throw new InvalidDataException("snapshot 包名重复：" + name);
                result.Packages.Add(name, match.Groups[2].Value);
            }
            if (result.Packages.Count == 0) throw new InvalidDataException("package snapshot 为空。");
            return result;
        }
        public static PackageDiff Compare(PackageSnapshot before, PackageSnapshot after)
        {
            var result = new PackageDiff(); string previous;
            foreach (var package in after.Packages.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!before.Packages.TryGetValue(package.Key, out previous)) result.Added.Add(new PackageChange { Name = package.Key, AfterVersion = package.Value });
                else if (previous != package.Value) result.Updated.Add(new PackageChange { Name = package.Key, BeforeVersion = previous, AfterVersion = package.Value });
            }
            foreach (var package in before.Packages.OrderBy(p => p.Key, StringComparer.Ordinal))
                if (!after.Packages.ContainsKey(package.Key)) result.Removed.Add(new PackageChange { Name = package.Key, BeforeVersion = package.Value });
            return result;
        }
        public static bool QueryReportsInstalled(ProbeResult result, PackageManagerKind manager, string name)
        {
            PluginInstallCommands.ValidatePackageName(name);
            if (manager == PackageManagerKind.APK)
            {
                if (result.ExitStatus == 1 && string.IsNullOrWhiteSpace(result.Error)) return false;
                if (result.ExitStatus != 0) throw new IOException("独立 APK 查询失败：" + result.Error);
                return Regex.IsMatch(result.Output ?? "", @"^" + Regex.Escape(name) + @"\s*$", RegexOptions.Multiline);
            }
            if (manager != PackageManagerKind.OPKG) throw new ArgumentException("Unsupported package manager.");
            if (result.ExitStatus != 0) throw new IOException("独立 OPKG 查询失败：" + result.Error);
            return Regex.IsMatch(result.Output ?? "", @"^Package:\s*" + Regex.Escape(name) + @"\s*$", RegexOptions.Multiline) &&
                Regex.IsMatch(result.Output ?? "", @"^Status:\s*\S+\s+\S+\s+installed\s*$", RegexOptions.Multiline);
        }
    }
    public sealed class PluginVerificationService
    {
        public static string KnownMainPackage(PluginPackageInfo plugin)
        {
            if (string.Equals(plugin.PluginName, "PassWall", StringComparison.OrdinalIgnoreCase)) return "luci-app-passwall";
            if (string.Equals(plugin.PluginName, "OpenClash", StringComparison.OrdinalIgnoreCase)) return "luci-app-openclash";
            return null;
        }
        public PluginVerificationResult Verify(PluginPackageInfo plugin, PackageSnapshot after, PackageDiff diff, Func<string, bool> independentQuery)
        {
            string name = KnownMainPackage(plugin); bool strong = name != null;
            if (name == null)
            {
                var candidates = diff.Added.Concat(diff.Updated).Where(p => p.Name.StartsWith("luci-app-", StringComparison.Ordinal)).Select(p => p.Name).Distinct().ToList();
                if (candidates.Count == 1) name = candidates[0];
                else if (candidates.Count > 1)
                {
                    string hint = plugin.IsRecognizedPlugin ? plugin.PluginName : System.IO.Path.GetFileNameWithoutExtension(plugin.FileName).Split('_')[0];
                    string normalized = Normalize(hint);
                    var matching = candidates.Where(p => Normalize(p.Substring(9)) == normalized).ToList();
                    if (matching.Count == 1) name = matching[0];
                }
            }
            var result = new PluginVerificationResult { StrongRule = strong, PackageName = name };
            string version;
            result.Verified = name != null && after.Packages.TryGetValue(name, out version) && independentQuery(name);
            if (result.Verified) result.Version = after.Packages[name];
            bool versionMismatch = false;
            if (result.Verified && strong)
            {
                Match expected = Regex.Match(plugin.PluginVersion ?? "", @"\Av?(\d+(?:\.\d+)+)(?:\z|[-_+])");
                Match actual = Regex.Match(result.Version, @"\Av?(\d+(?:\.\d+)+)(?:\z|[-_+])");
                if (expected.Success && actual.Success && expected.Groups[1].Value != actual.Groups[1].Value)
                { result.Verified = false; versionMismatch = true; }
            }
            result.Message = result.Verified ? "已通过独立 package 查询验证：" + name + " " + result.Version :
                versionMismatch ? "主 package 仍为 " + result.Version + "，与安装包版本 " + plugin.PluginVersion + " 不一致；不能确认更新成功。" :
                strong ? "目标主 package 不存在或独立查询未通过：" + name : "安装完成，但无法唯一确认主插件 package；请查看日志 / LuCI 菜单。";
            return result;
        }
        private static string Normalize(string text) { return Regex.Replace((text ?? "").ToLowerInvariant(), "[^a-z0-9]", ""); }
    }
}
