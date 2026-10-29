using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static partial class WorkflowTests
    {
        public static void SourceRetentionTests(Action<string,Action> test)
        {
            foreach (var value in new[] { PackageManagerKind.OPKG, PackageManagerKind.APK })
            {
                var manager = value;
                test("Source retention " + manager + " success keeps actual selected mirror with no post-install probes updates or restoration", () => Fixture(Name, path => {
                    var fake = new Fake { RestoreFailure = true }; if (manager == PackageManagerKind.OPKG) fake.MakeOpkg(); fake.BadHosts.Add("old.test"); fake.candidates[1].Name = "动态可用镜像";
                    int probes = 0, updates = 0; fake.AfterStart = () => { probes = fake.Probes; updates = fake.UpdateCalls; };
                    var logs = new List<string>(); var service = Service();
                    var result = Run(service, fake, path, CancellationToken.None, new InlineProgress<InstallationWorkflowProgress>(p => { if (p.Log != null) logs.Add(p.Log); }));
                    Equal(InstallationState.Success, result.State); True(result.SourceRetained); Equal(false, result.RestoreAttempted); Equal(0, fake.Restores); Equal(1, fake.UpdateCalls);
                    Equal(probes, fake.Probes); Equal(updates, fake.UpdateCalls); Equal("A", fake.Identity); Equal(1, fake.BackupDeletes); True(result.Installation.CleanupSucceeded); Equal(false, service.HasPendingWork);
                    True(logs.Any(l => l.Contains("动态可用镜像") && l.Contains("已保留当前软件源设置")));
                }));
                test("Source retention " + manager + " failed install preserves exact recovery cleanup and update gate", () => Fixture(Name, path => {
                    var fake = new Fake(); if (manager == PackageManagerKind.OPKG) fake.MakeOpkg(); fake.BadHosts.Add("old.test"); fake.Runs.Enqueue(new Outcome { Exit = 2, Log = "syntax error" });
                    var result = Run(Service(), fake, path); Equal(InstallationFailureKind.InstallerScript, result.Failure.Kind); True(result.SourceRestored); Equal(false, result.SourceRetained);
                    Equal(fake.Original, fake.Current); Equal(1, fake.Restores); Equal(1, fake.UpdateCalls); True(fake.Events.IndexOf("restore") < fake.Events.IndexOf("cleanup")); True(result.Installation.CleanupSucceeded);
                }));
            }
            test("Source retention reconnects old completed runner and retains its chosen mirror without redispatch", () => Fixture(Name, path => {
                var fake = new Fake { NeverEnds = true }; fake.BadHosts.Add("old.test"); var service = Service(); service.Engine.OverallLimit = TimeSpan.Zero;
                True(Run(service, fake, path).PendingInstaller); Equal(0, fake.Restores); fake.NeverEnds = false; service.Engine.OverallLimit = TimeSpan.FromSeconds(1);
                var result = Run(service, fake, path); Equal(InstallationState.Success,result.State); True(result.SourceRetained); Equal(1, fake.Starts); Equal(0, fake.Restores);
                Equal("A", result.Software.CurrentSource.Identity); Equal(false, service.HasPendingWork); True(result.Installation.CleanupSucceeded);
            }));
        }
    }
}
