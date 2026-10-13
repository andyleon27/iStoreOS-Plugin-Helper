using System.Collections.Generic;
using System.Linq;

namespace iStoreOS.PluginHelper.Models
{
    public enum RepositoryHealth { DOWN, PARTIAL, FULL }
    public enum SoftwareErrorCategory { None, Transport, RepoIndex, Compatibility, PackageAvailability, Conflict, Timeout, Unknown }
    public sealed class RepositoryProbeResult
    {
        public RepositoryFeed Feed { get; set; }
        public bool Success { get; set; }
        public int HttpStatus { get; set; }
        public int ExitCode { get; set; }
        public double ResponseMilliseconds { get; set; }
        public SoftwareErrorCategory ErrorCategory { get; set; }
        public string ErrorMessage { get; set; }
    }
    public sealed class RepositoryHealthResult
    {
        public RepositoryHealthResult() { Probes = new List<RepositoryProbeResult>(); }
        public List<RepositoryProbeResult> Probes { get; set; }
        public int Total { get { return Probes.Count; } }
        public int Successful { get { return Probes.Count(p => p.Success); } }
        public RepositoryHealth Status { get { return Total > 0 && Successful == Total ? RepositoryHealth.FULL : Successful > 0 ? RepositoryHealth.PARTIAL : RepositoryHealth.DOWN; } }
        public double AverageMilliseconds { get { return Total == 0 ? double.MaxValue : Probes.Average(p => p.ResponseMilliseconds); } }
        public override string ToString() { return Status + " " + Successful + "/" + Total; }
    }
}
