using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Tests
{
    internal static partial class InstallationTests
    {
        private sealed class SnapshotLog : IProgress<InstallProgress>
        { public readonly List<string> Logs = new List<string>(); public void Report(InstallProgress progress) { if (progress.Log != null) Logs.Add(progress.Log); } }
        private static void SnapshotLoggingTests(Action<string, Action> test)
        {
            SnapshotCase(test, "full dictionaries retained for before and after", (result, log) => {
                Equal(802, result.BeforePackages.Packages.Count); Equal(803, result.AfterPackages.Packages.Count);
                Equal("1.0", result.BeforePackages.Packages["unchanged-500"]); Equal("1.0", result.AfterPackages.Packages["unchanged-500"]);
            });
            SnapshotCase(test, "before log reports count without unchanged package rows", (result, log) => {
                True(log.Logs.Single(l => l.StartsWith("BeforePackages:")).Contains("802 packages")); Equal(false, string.Join("\n", log.Logs).Contains("unchanged-500"));
            });
            SnapshotCase(test, "after log reports count without unchanged package rows", (result, log) => {
                True(log.Logs.Single(l => l.StartsWith("AfterPackages:")).Contains("803 packages")); Equal(false, string.Join("\n", log.Logs).Contains("unchanged-799"));
            });
            SnapshotCase(test, "diff still logs every added changed and removed package", (result, log) => {
                Equal(2, result.Diff.Added.Count); Equal(1, result.Diff.Updated.Count); Equal(1, result.Diff.Removed.Count);
                string diff = log.Logs.Single(l => l.StartsWith("Package Diff："));
                True(diff.Contains("Added luci-app-passwall 26.9.16-r1")); True(diff.Contains("Added newdependency 1.0"));
                True(diff.Contains("Updated unchanged-42 1.0 -> 2.0")); True(diff.Contains("Removed unchanged-43 1.0"));
            });
            SnapshotCase(test, "independent strong verification unaffected by compact log", (result, log) => {
                Equal(InstallStatus.SuccessVerified, result.Status); True(result.Verification.Verified); Equal("luci-app-passwall", result.Verification.PackageName);
            });
            test("V1.0.2 r4 standalone failed upload also emits timing summary", () => Fixture(PassWall, path => {
                var fake = new FakeSession { UploadFailure = true }; var log = new SnapshotLog();
                var result = Workflow(fake, path, progress: log); Equal(InstallStatus.FailedUpload, result.Status);
                True(log.Logs.Single(l => l.StartsWith("Timing Summary:")).Contains("UploadIntegrity = ")); Equal(0, fake.Starts);
            }));
        }
        private static void SnapshotCase(Action<string, Action> test, string name, Action<PluginInstallResult, SnapshotLog> check)
        {
            test("V1.0.2 r4 snapshot " + name, () => Fixture(PassWall, path => {
                var fake = new FakeSession();
                for (int i = 0; i < 800; i++) { fake.Before.Packages["unchanged-" + i] = "1.0"; fake.After.Packages["unchanged-" + i] = "1.0"; }
                fake.After.Packages["unchanged-42"] = "2.0"; fake.After.Packages.Remove("unchanged-43"); fake.After.Packages["newdependency"] = "1.0";
                var log = new SnapshotLog(); check(Workflow(fake, path, progress: log), log);
            }));
        }
    }
}
