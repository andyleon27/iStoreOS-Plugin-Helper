using System;
using System.Collections.Generic;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class CompatibilityService
    {
        public void Evaluate(PluginPackageInfo plugin, RouterEnvironment router)
        {
            if (plugin == null) return;
            plugin.CompatibilityStatus = CompatibilityStatus.Unknown;
            if (router == null || !router.IsConnected)
            { plugin.CompatibilityMessage = "无法确定兼容性：请先连接并检测路由器。"; return; }
            if (plugin.Format != LocalPackageFormat.Run)
            {
                if (!plugin.MetadataRead || string.IsNullOrEmpty(router.Architecture) || router.PackageManager == PackageManagerKind.Unknown)
                { plugin.CompatibilityMessage = "无法确定兼容性：包元数据、设备架构或包管理器未识别。"; return; }
                bool managerMatches = plugin.Format == LocalPackageFormat.Ipk ? router.PackageManager == PackageManagerKind.OPKG : router.PackageManager == PackageManagerKind.APK;
                bool archMatches = string.Equals(plugin.Architecture, router.Architecture, StringComparison.Ordinal) ||
                    plugin.Architecture == (plugin.Format == LocalPackageFormat.Ipk ? "all" : "noarch");
                plugin.CompatibilityStatus = managerMatches && archMatches ? CompatibilityStatus.Compatible : CompatibilityStatus.Incompatible;
                plugin.CompatibilityMessage = !managerMatches ? "✗ IPK 仅支持 OPKG，APK 仅支持 APK 包管理器。" : !archMatches ?
                    "✗ 包架构 " + plugin.Architecture + " 与设备 " + router.Architecture + " 不匹配。" : "✓ 包管理器 / 包架构匹配；依赖、ABI 与签名由设备包管理器进一步校验。";
                return;
            }
            if (string.IsNullOrWhiteSpace(plugin.SdkSeries) || string.IsNullOrWhiteSpace(plugin.Architecture) ||
                string.IsNullOrWhiteSpace(router.SdkSeries) || string.IsNullOrWhiteSpace(router.Architecture))
            { plugin.CompatibilityMessage = "无法确定兼容性：插件或设备的 SDK / 架构未完整识别。"; return; }
            var reasons = new List<string>();
            string architectureExplanation;
            if (!ArchitectureCompatibilityService.IsCompatible(plugin.Architecture, router.Architecture, out architectureExplanation))
                reasons.Add(architectureExplanation);
            if (!string.Equals(plugin.SdkSeries, router.SdkSeries, StringComparison.Ordinal))
                reasons.Add("插件 SDK " + plugin.SdkSeries + " 与目标 " + router.SdkSeries + " 不匹配");
            plugin.CompatibilityStatus = reasons.Count == 0 ? CompatibilityStatus.Compatible : CompatibilityStatus.Incompatible;
            plugin.CompatibilityMessage = reasons.Count == 0 ? "✓ 与当前设备兼容（SDK / 架构匹配）" : "✗ " + string.Join("；", reasons);
            if (reasons.Count == 0 && !string.Equals(plugin.Architecture, router.Architecture, StringComparison.OrdinalIgnoreCase))
                plugin.CompatibilityMessage += "；" + architectureExplanation;
        }
    }
}
