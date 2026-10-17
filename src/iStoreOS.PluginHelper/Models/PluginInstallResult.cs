using System;
using System.Collections.Generic;

namespace iStoreOS.PluginHelper.Models
{
    public enum InstallStatus
    {
        FailedPrecheck, FailedUpload, FailedIntegrity, FailedInstaller, FailedVerification,
        Cancelled, AlreadyInstalled, SuccessVerified, SuccessWithWarning, InstallerStateUnknown
    }
    public sealed class RemotePackageInfo
    {
        public string Identification { get; set; }
        public string TargetDirectory { get; set; }
        public string Architecture { get; set; }
        public string SdkSeries { get; set; }
        public long? UncompressedBytes { get; set; }
        public string Compression { get; set; }
        public string MakeselfVersion { get; set; }
    }
    public sealed class PackageSnapshot
    {
        public PackageSnapshot() { Packages = new Dictionary<string, string>(StringComparer.Ordinal); }
        public Dictionary<string, string> Packages { get; private set; }
    }
    public sealed class PackageChange
    {
        public string Name { get; set; }
        public string BeforeVersion { get; set; }
        public string AfterVersion { get; set; }
    }
    public sealed class PackageDiff
    {
        public PackageDiff() { Added = new List<PackageChange>(); Updated = new List<PackageChange>(); Removed = new List<PackageChange>(); }
        public List<PackageChange> Added { get; private set; }
        public List<PackageChange> Updated { get; private set; }
        public List<PackageChange> Removed { get; private set; }
    }
    public sealed class PluginVerificationResult
    {
        public bool Verified { get; set; }
        public bool StrongRule { get; set; }
        public string PackageName { get; set; }
        public string Version { get; set; }
        public string Message { get; set; }
    }
    public sealed class InstallConfirmation
    {
        public PluginPackageInfo Plugin { get; set; }
        public RouterEnvironment Router { get; set; }
        public string Sha256 { get; set; }
        public SoftwareEnvironmentResult SoftwareEnvironment { get; set; }
        public string ExistingPackage { get; set; }
        public string ExistingVersion { get; set; }
    }
    public sealed class PluginInstallResult
    {
        public PluginInstallResult() { Warnings = new List<string>(); Status = InstallStatus.FailedPrecheck; }
        public InstallStatus Status { get; set; }
        public string Message { get; set; }
        public PluginPackageInfo Plugin { get; set; }
        public string Sha256 { get; set; }
        public string RemoteDirectory { get; set; }
        public RemotePackageInfo Archive { get; set; }
        public SoftwareEnvironmentResult SoftwareEnvironment { get; set; }
        public PackageSnapshot BeforePackages { get; set; }
        public PackageSnapshot AfterPackages { get; set; }
        public PackageDiff Diff { get; set; }
        public PluginVerificationResult Verification { get; set; }
        public bool InstallerStarted { get; set; }
        public bool InstallerEnded { get; set; }
        public int? InstallerExitCode { get; set; }
        public bool CleanupSucceeded { get; set; }
        public List<string> Warnings { get; private set; }
    }
    public sealed class InstallProgress
    {
        public string Step { get; set; }
        public string Log { get; set; }
        public long? UploadedBytes { get; set; }
        public long? TotalBytes { get; set; }
        public bool InstallerStarted { get; set; }
    }
    public sealed class InstallerPoll
    {
        public byte[] Stdout { get; set; }
        public byte[] Stderr { get; set; }
        public int? ExitCode { get; set; }
        public string Diagnostic { get; set; }
    }
}
