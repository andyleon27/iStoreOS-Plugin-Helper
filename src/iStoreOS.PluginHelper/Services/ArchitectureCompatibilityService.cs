using System;

namespace iStoreOS.PluginHelper.Services
{
    // Directional filename preflight for Are-u-ok .run packages, not a general
    // OpenWrt package ABI policy. Keep caller-owned architecture strings intact.
    public static class ArchitectureCompatibilityService
    {
        private static bool Same(string left, string right)
        { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }

        public static string Canonicalize(string architecture)
        {
            if (Same(architecture, "aarch64_a53") || Same(architecture, "aarch64_cortex-a53")) return "aarch64_cortex-a53";
            if (Same(architecture, "aarch64_a72") || Same(architecture, "aarch64_cortex-a72")) return "aarch64_cortex-a72";
            if (Same(architecture, "aarch64_generic")) return "aarch64_generic";
            if (Same(architecture, "aarch64")) return "aarch64";
            if (Same(architecture, "x86_64")) return "x86_64";
            return architecture;
        }

        public static bool IsCompatible(string pluginArchitecture, string routerArchitecture)
        {
            string explanation;
            return IsCompatible(pluginArchitecture, routerArchitecture, out explanation);
        }

        public static bool IsCompatible(string pluginArchitecture, string routerArchitecture, out string explanation)
        {
            if (string.IsNullOrWhiteSpace(pluginArchitecture) || string.IsNullOrWhiteSpace(routerArchitecture))
            { explanation = "无法确定架构兼容性：插件或设备架构未完整识别。"; return false; }

            string plugin = Canonicalize(pluginArchitecture), router = Canonicalize(routerArchitecture);
            bool exact = Same(plugin, router);
            bool bareArm64 = Same(plugin, "aarch64") && IsArm64Family(router);
            bool areUOkA53Generic = Same(plugin, "aarch64_cortex-a53") && Same(router, "aarch64_generic");
            if (!exact && !bareArm64 && !areUOkA53Generic)
            {
                explanation = "插件架构 " + pluginArchitecture + " 与目标 " + routerArchitecture + " 不兼容";
                return false;
            }

            explanation = "架构兼容映射：" + pluginArchitecture + " → " + routerArchitecture;
            if (!Same(pluginArchitecture, plugin) || !Same(routerArchitecture, router))
                explanation += "（canonical：" + plugin + " → " + router + "）";
            if (areUOkA53Generic)
                explanation += "；Are-u-ok ARM64 compatibility rule（A53 插件 → generic 设备，仅用于作者 .run 文件名预检）";
            else if (bareArm64 && !exact)
                explanation += "；bare aarch64 为 ARM64 通用包标记";
            return true;
        }

        private static bool IsArm64Family(string architecture)
        {
            return Same(architecture, "aarch64") ||
                (architecture.Length > "aarch64_".Length && architecture.StartsWith("aarch64_", StringComparison.OrdinalIgnoreCase));
        }
    }
}
