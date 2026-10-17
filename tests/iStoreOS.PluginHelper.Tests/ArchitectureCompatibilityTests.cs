using System;
using System.Globalization;
using System.Threading;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class ArchitectureCompatibilityTests
    {
        public static void Run(Action<string, Action> test)
        {
            var routers = new[] { "x86_64", "aarch64", "aarch64_generic", "aarch64_a53", "aarch64_cortex-a53",
                "aarch64_a72", "aarch64_cortex-a72", "aarch64_cortex-a76", "arm_cortex-a53", "mips_24kc", "mipsel_24kc", "aarch64evil" };
            MatrixRow(test, routers, "x86_64", new[] { "x86_64" });
            MatrixRow(test, routers, "aarch64", new[] { "aarch64", "aarch64_generic", "aarch64_a53", "aarch64_cortex-a53", "aarch64_a72", "aarch64_cortex-a72", "aarch64_cortex-a76" });
            MatrixRow(test, routers, "aarch64_generic", new[] { "aarch64_generic" });
            MatrixRow(test, routers, "aarch64_a53", new[] { "aarch64_generic", "aarch64_a53", "aarch64_cortex-a53" });
            MatrixRow(test, routers, "aarch64_cortex-a53", new[] { "aarch64_generic", "aarch64_a53", "aarch64_cortex-a53" });
            MatrixRow(test, routers, "aarch64_a72", new[] { "aarch64_a72", "aarch64_cortex-a72" });
            MatrixRow(test, routers, "aarch64_cortex-a72", new[] { "aarch64_a72", "aarch64_cortex-a72" });

            var aliases = new[] { "aarch64_a53", "aarch64_cortex-a53", "aarch64_a72", "aarch64_cortex-a72", "aarch64_generic", "aarch64", "x86_64" };
            var canonical = new[] { "aarch64_cortex-a53", "aarch64_cortex-a53", "aarch64_cortex-a72", "aarch64_cortex-a72", "aarch64_generic", "aarch64", "x86_64" };
            for (int index = 0; index < aliases.Length; index++)
            {
                string raw = aliases[index], expected = canonical[index];
                test("V1.0.1 canonical " + raw, () => Equal(expected, ArchitectureCompatibilityService.Canonicalize(raw)));
            }
            foreach (string missing in new string[] { null, "", " ", "\t" })
            {
                string value = missing;
                test("V1.0.1 missing plugin architecture " + Label(value), () => Equal(false, ArchitectureCompatibilityService.IsCompatible(value, "aarch64_cortex-a53")));
                test("V1.0.1 missing router architecture " + Label(value), () => Equal(false, ArchitectureCompatibilityService.IsCompatible("aarch64", value)));
            }
            foreach (string invalid in new[] { "aarch64evil", "aarch64evil_cortex-a53", "aarch640", "arm_cortex-a53", "mips64", "aarch64_" })
            {
                string value = invalid;
                test("V1.0.1 bare ARM64 rejects family lookalike " + value, () => Equal(false, ArchitectureCompatibilityService.IsCompatible("aarch64", value)));
            }
            foreach (string culture in new[] { "en-US", "tr-TR" })
            {
                string name = culture;
                test("V1.0.1 ordinal architecture comparison under " + name, () =>
                {
                    CultureInfo previous = Thread.CurrentThread.CurrentCulture;
                    try
                    {
                        Thread.CurrentThread.CurrentCulture = new CultureInfo(name);
                        Equal(true, ArchitectureCompatibilityService.IsCompatible("AARCH64_GENERIC", "AaRcH64_GeNeRiC"));
                        Equal(true, ArchitectureCompatibilityService.IsCompatible("AARCH64_A53", "aarch64_CORTEX-A53"));
                        Equal(false, ArchitectureCompatibilityService.IsCompatible("AARCH64_A72", "aarch64_CORTEX-A53"));
                    }
                    finally { Thread.CurrentThread.CurrentCulture = previous; }
                });
            }
            foreach (string token in new[] { "aarch64_cortex-a53", "aarch64_cortex-a72", "aarch64_generic", "aarch64_a53", "aarch64_a72" })
            {
                string architecture = token;
                test("V1.0.1 longest filename architecture token " + architecture, () =>
                {
                    var analyzer = new PluginPackageAnalyzer();
                    var all = analyzer.AnalyzeFileName("PassWall_1.2_" + architecture + "_all_sdk_24.10.run");
                    var core = analyzer.AnalyzeFileName("OpenClash_1.2+" + architecture + "_core_sdk_24.10.run");
                    Equal(architecture, all.Architecture); Equal(architecture, core.Architecture);
                    Equal("1.2", all.PluginVersion); Equal("1.2", core.PluginVersion); Equal("all", all.PackageType); Equal("core", core.PackageType);
                });
            }
            test("V1.0.1 parser retains original architecture spelling", () =>
            {
                var plugin = new PluginPackageAnalyzer().AnalyzeFileName("PassWall_1.2_AARCH64_CORTEX-A53_all_sdk_24.10.run");
                var router = Router("aarch64_Cortex-A53"); new CompatibilityService().Evaluate(plugin, router);
                Equal("AARCH64_CORTEX-A53", plugin.Architecture); Equal("aarch64_Cortex-A53", router.Architecture);
                Equal(CompatibilityStatus.Compatible, plugin.CompatibilityStatus);
            });

            RealFilename(test, "OpenClash_0.47.156+aarch64_core_sdk_24.10.run", "OpenClash", "0.47.156", "aarch64", "core");
            RealFilename(test, "daede_1.15-r6_aarch64_a53_all_sdk_24.10.run", "daede", "1.15-r6", "aarch64_a53", "all");
            RealFilename(test, "PassWall_26.10.16_aarch64_a53_all_sdk_24.10.run", "PassWall", "26.10.16", "aarch64_a53", "all");
            RealFilename(test, "SSR-Plus_196-r19_aarch64_a53_all_sdk_24.10.run", "SSR-Plus", "196-r19", "aarch64_a53", "all");
            test("V1.0.1 A72 filename counterexample stays incompatible on A53", () =>
            {
                var plugin = new PluginPackageAnalyzer().AnalyzeFileName("PassWall_xxx_aarch64_a72_all_sdk_24.10.run");
                var router = Router("aarch64_cortex-a53"); new CompatibilityService().Evaluate(plugin, router);
                Equal("aarch64_a72", plugin.Architecture); Equal(CompatibilityStatus.Incompatible, plugin.CompatibilityStatus);
                Equal("✗ 插件架构 aarch64_a72 与目标 aarch64_cortex-a53 不兼容", plugin.CompatibilityMessage);
            });
            foreach (string architecture in new[] { "aarch64", "aarch64_a53", "aarch64_cortex-a53" })
            {
                string value = architecture;
                test("V1.0.1 SDK mismatch still blocks compatible architecture " + value, () =>
                {
                    var plugin = new PluginPackageAnalyzer().AnalyzeFileName("PassWall_1_" + value + "_all_sdk_24.10.run");
                    var router = Router("aarch64_cortex-a53"); router.SdkSeries = "25.12";
                    new CompatibilityService().Evaluate(plugin, router);
                    Equal(CompatibilityStatus.Incompatible, plugin.CompatibilityStatus);
                    Equal(true, plugin.CompatibilityMessage.Contains("插件 SDK 24.10 与目标 25.12 不匹配"));
                });
            }
            test("V1.0.1 A53 generic exception is directional and names Are-u-ok policy", () =>
            {
                var plugin = new PluginPackageAnalyzer().AnalyzeFileName("PassWall_1_aarch64_a53_all_sdk_24.10.run");
                var router = Router("aarch64_generic"); new CompatibilityService().Evaluate(plugin, router);
                Equal(CompatibilityStatus.Compatible, plugin.CompatibilityStatus);
                Equal(true, plugin.CompatibilityMessage.Contains("Are-u-ok ARM64 compatibility rule"));
                Equal(true, plugin.CompatibilityMessage.Contains("aarch64_a53 → aarch64_generic"));
                Equal(false, ArchitectureCompatibilityService.IsCompatible("aarch64_generic", "aarch64_cortex-a53"));
                Equal(false, ArchitectureCompatibilityService.IsCompatible("aarch64_a72", "aarch64_generic"));
                Equal("aarch64_a53", plugin.Architecture); Equal("aarch64_generic", router.Architecture);
            });
        }

        private static void MatrixRow(Action<string, Action> test, string[] routers, string plugin, string[] acceptedRouters)
        {
            foreach (string architecture in routers)
            {
                string routerArchitecture = architecture; bool expected = Array.IndexOf(acceptedRouters, routerArchitecture) >= 0;
                test("V1.0.1 architecture matrix " + plugin + " -> " + routerArchitecture, () =>
                {
                    string explanation;
                    Equal(expected, ArchitectureCompatibilityService.IsCompatible(plugin, routerArchitecture, out explanation));
                    Equal(expected, ArchitectureCompatibilityService.IsCompatible(plugin, routerArchitecture));
                    Equal(false, string.IsNullOrWhiteSpace(explanation));
                    var package = new PluginPackageInfo { Architecture = plugin, SdkSeries = "24.10" }; var router = Router(routerArchitecture);
                    new CompatibilityService().Evaluate(package, router);
                    Equal(expected ? CompatibilityStatus.Compatible : CompatibilityStatus.Incompatible, package.CompatibilityStatus);
                    Equal(plugin, package.Architecture); Equal(routerArchitecture, router.Architecture);
                    if (!expected) { Equal(true, package.CompatibilityMessage.Contains(plugin)); Equal(true, package.CompatibilityMessage.Contains(routerArchitecture)); }
                });
            }
        }
        private static void RealFilename(Action<string, Action> test, string file, string name, string version, string architecture, string kind)
        {
            test("V1.0.1 filename regression " + file, () =>
            {
                var plugin = new PluginPackageAnalyzer().AnalyzeFileName(file); var router = Router("aarch64_cortex-a53");
                new CompatibilityService().Evaluate(plugin, router);
                Equal(name, plugin.PluginName); Equal(version, plugin.PluginVersion); Equal(architecture, plugin.Architecture);
                Equal(kind, plugin.PackageType); Equal("24.10", plugin.SdkSeries); Equal(CompatibilityStatus.Compatible, plugin.CompatibilityStatus);
                Equal("aarch64_cortex-a53", router.Architecture); Equal(PackageManagerKind.OPKG, router.PackageManager);
                Equal(true, plugin.CompatibilityMessage.Contains(architecture + " → aarch64_cortex-a53"));
            });
        }
        private static RouterEnvironment Router(string architecture)
        { return new RouterEnvironment { IsConnected = true, Version = "24.10.8", SdkSeries = "24.10", Architecture = architecture, PackageManager = PackageManagerKind.OPKG }; }
        private static string Label(string value) { return value == null ? "null" : value.Length == 0 ? "empty" : value == " " ? "space" : "tab"; }
        private static void Equal<T>(T expected, T actual)
        { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    }
}
