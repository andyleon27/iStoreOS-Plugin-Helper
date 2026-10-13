using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class RouterDetectionService
    {
        public RouterEnvironment Detect(IReadOnlyRouterSession session, Action<string> log, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProbeResult os = session.Execute(RouterProbe.OsRelease);
            var fields = ParseRelease(os.ExitStatus == 0 ? os.Output : string.Empty);
            string version = GetField(fields, "VERSION_ID");
            string architecture = GetField(fields, "OPENWRT_ARCH");
            if (string.IsNullOrWhiteSpace(architecture))
            {
                cancellationToken.ThrowIfCancellationRequested();
                log("OPENWRT_ARCH 缺失，读取 /etc/openwrt_release 的 DISTRIB_ARCH。");
                ProbeResult fallback = session.Execute(RouterProbe.OpenWrtRelease);
                var fallbackFields = ParseRelease(fallback.ExitStatus == 0 ? fallback.Output : string.Empty);
                architecture = GetField(fallbackFields, "DISTRIB_ARCH");
            }
            cancellationToken.ThrowIfCancellationRequested();
            ProbeResult manager = session.Execute(RouterProbe.PackageManager);
            PackageManagerKind kind = PackageManagerKind.Unknown;
            if (manager.ExitStatus == 0)
            {
                string output = (manager.Output ?? string.Empty).Trim();
                if (output == "APK") kind = PackageManagerKind.APK;
                else if (output == "OPKG") kind = PackageManagerKind.OPKG;
            }
            var environment = new RouterEnvironment
            {
                Version = version,
                SdkSeries = GetSdkSeries(version),
                Architecture = architecture,
                PackageManager = kind,
                IsConnected = true
            };
            log("系统版本：" + Display(version) + "；SDK 系列：" + Display(environment.SdkSeries));
            log("包架构：" + Display(architecture));
            log("包管理器：" + kind);
            return environment;
        }

        public static string GetSdkSeries(string version)
        {
            Match match = Regex.Match(version ?? string.Empty, @"^(\d{2}\.\d{2})(?:\.\d+)?(?:$|[-+])");
            return match.Success ? match.Groups[1].Value : null;
        }

        public static IDictionary<string, string> ParseRelease(string content)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string line in (content ?? string.Empty).Split('\n'))
            {
                string item = line.Trim();
                if (item.Length == 0 || item.StartsWith("#", StringComparison.Ordinal)) continue;
                int separator = item.IndexOf('=');
                if (separator <= 0) continue;
                string key = item.Substring(0, separator).Trim();
                string value = item.Substring(separator + 1).Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') ||
                                         (value[0] == '\'' && value[value.Length - 1] == '\'')))
                    value = value.Substring(1, value.Length - 2);
                result[key] = value;
            }
            return result;
        }

        private static string GetField(IDictionary<string, string> fields, string name)
        {
            string value;
            return fields.TryGetValue(name, out value) ? value : null;
        }

        private static string Display(string value) { return string.IsNullOrWhiteSpace(value) ? "未识别" : value; }
    }
}
