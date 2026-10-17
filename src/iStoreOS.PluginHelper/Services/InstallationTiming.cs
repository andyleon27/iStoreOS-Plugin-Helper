using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace iStoreOS.PluginHelper.Services
{
    // Monotonic, additive observations only; never used to decide installation outcomes.
    public sealed class InstallationTiming
    {
        private readonly Stopwatch total = Stopwatch.StartNew();
        private readonly Dictionary<string, double> milliseconds = new Dictionary<string, double>();
        private static readonly string[] stages = { "Preflight / SourceSelect", "PackageUpdate", "UploadIntegrity", "SnapshotBefore",
            "InstallerRuntime", "SnapshotAfterVerify", "RestoreCleanup", "CandidateSelection", "CandidateFullVerify" };
        public IDisposable Measure(string stage) { return new Scope(this, stage); }
        public string Summary()
        {
            return "Timing Summary: " + string.Join("; ", stages.Select(stage => stage + " = " + Value(stage).ToString("F0", CultureInfo.InvariantCulture) + " ms")) +
                "; Total = " + total.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms";
        }
        private double Value(string stage) { double value; return milliseconds.TryGetValue(stage, out value) ? value : 0; }
        private sealed class Scope : IDisposable
        {
            private readonly InstallationTiming owner; private readonly string stage; private readonly Stopwatch watch = Stopwatch.StartNew();
            private bool disposed;
            public Scope(InstallationTiming owner, string stage) { this.owner = owner; this.stage = stage; }
            public void Dispose() { if (disposed) return; disposed = true; owner.milliseconds[stage] = owner.Value(stage) + watch.Elapsed.TotalMilliseconds; }
        }
    }
    public interface IInstallationTimingSession { InstallationTiming Timing { get; } }
}
