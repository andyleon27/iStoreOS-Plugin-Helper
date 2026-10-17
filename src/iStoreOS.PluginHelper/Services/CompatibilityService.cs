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
