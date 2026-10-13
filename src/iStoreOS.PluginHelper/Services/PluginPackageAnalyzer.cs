using System;
using System.IO;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class PluginPackageAnalyzer
    {
        private static readonly string[] Architectures =
            { "aarch64_generic", "aarch64_a53", "aarch64_a72", "x86_64", "aarch64" };
        private static readonly string[] PluginNames =
            { "openwrt-daede", "OpenWrt-nikki", "OpenWrt-momo", "PassWall2", "PassWall", "homeproxy", "OpenClash", "daede", "SSR-Plus" };

        public PluginPackageInfo AnalyzeFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择 .run 文件。");
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists) throw new FileNotFoundException("文件不存在或无法访问。", path);
            if ((file.Attributes & FileAttributes.Directory) != 0) throw new ArgumentException("请选择文件。");
            if (!string.Equals(file.Extension, ".run", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请选择扩展名为 .run 的文件。");
            PluginPackageInfo info = AnalyzeFileName(file.Name);
            info.FilePath = file.FullName;
            info.FileSize = file.Length;
            return info;
        }

        // No file is opened or executed. This entry point supports small filename-only tests.
        public PluginPackageInfo AnalyzeFileName(string fileName)
        {
            var info = new PluginPackageInfo
            {
                FileName = Path.GetFileName(fileName ?? string.Empty),
                PluginName = "未知插件 / 通用 Are-u-ok 包",
                CompatibilityStatus = CompatibilityStatus.Unknown,
                CompatibilityMessage = "无法确定兼容性：请先连接并检测路由器。"
            };
            if (!string.Equals(Path.GetExtension(info.FileName), ".run", StringComparison.OrdinalIgnoreCase)) return info;
            string stem = Path.GetFileNameWithoutExtension(info.FileName);
            int sdkMarker = stem.LastIndexOf("_sdk_", StringComparison.OrdinalIgnoreCase);
            string prefix = stem;
            if (sdkMarker >= 0)
            {
                string sdk = stem.Substring(sdkMarker + 5);
                if (Regex.IsMatch(sdk, @"^\d{2}\.\d{2}$")) info.SdkSeries = sdk;
                prefix = stem.Substring(0, sdkMarker);
            }
            if (prefix.EndsWith("_all", StringComparison.OrdinalIgnoreCase))
            { info.PackageType = "all"; prefix = prefix.Substring(0, prefix.Length - 4); }
            else if (prefix.EndsWith("_core", StringComparison.OrdinalIgnoreCase))
            { info.PackageType = "core"; prefix = prefix.Substring(0, prefix.Length - 5); }

            // Longest tokens first prevents aarch64 from swallowing aarch64_generic.
            // Architecture must occupy the package's suffix, including OpenClash's '+' form.
            string nameAndVersion = prefix;
            foreach (string architecture in Architectures)
            {
                if (prefix.EndsWith("_" + architecture, StringComparison.Ordinal) ||
                    prefix.EndsWith("+" + architecture, StringComparison.Ordinal))
                {
                    info.Architecture = architecture;
                    nameAndVersion = prefix.Substring(0, prefix.Length - architecture.Length - 1);
                    break;
                }
            }
            foreach (string name in PluginNames)
            {
                if (nameAndVersion.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    nameAndVersion.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))
                {
                    info.PluginName = name;
                    info.IsRecognizedPlugin = true;
                    if (nameAndVersion.Length > name.Length)
                        info.PluginVersion = nameAndVersion.Substring(name.Length + 1);
                    break;
                }
            }
            if (!info.IsRecognizedPlugin)
            {
                int separator = nameAndVersion.LastIndexOf('_');
                if (separator >= 0 && separator + 1 < nameAndVersion.Length &&
                    char.IsDigit(nameAndVersion[separator + 1]))
                    info.PluginVersion = nameAndVersion.Substring(separator + 1);
            }
            return info;
        }
    }
}
