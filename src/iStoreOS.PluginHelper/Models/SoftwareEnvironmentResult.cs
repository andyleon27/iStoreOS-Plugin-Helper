using System.Collections.Generic;

namespace iStoreOS.PluginHelper.Models
{
    public enum SoftwareEnvironmentStatus { Unknown, Ready, Failed, Cancelled, Unsupported, RecoveryFailed }
    public sealed class SoftwareEnvironmentResult
    {
        public SoftwareEnvironmentResult() { Updates = new List<PackageUpdateResult>(); Candidates = new List<SoftwareRepairPlan>(); }
        public SoftwareEnvironmentStatus Status { get; set; }
        public string Message { get; set; }
        public SoftwareSourceInfo CurrentSource { get; set; }
        public RepositoryHealthResult OfficialHealth { get; set; }
        public RepositoryHealthResult AdditionalHealth { get; set; }
        public List<PackageUpdateResult> Updates { get; set; }
        public List<SoftwareRepairPlan> Candidates { get; set; }
        public bool SwitchAttempted { get; set; }
        public bool SourceSwitchOccurred { get; set; }
        public bool RecoveryAttempted { get; set; }
        public bool RecoverySucceeded { get; set; }
        public string BackupPath { get; set; }
    }
    public sealed class SoftwareProgress
    {
        public string Step { get; set; }
        public string Log { get; set; }
        public SoftwareEnvironmentResult Result { get; set; }
    }
}
