using System;
using System.Collections.Generic;

namespace iStoreOS.PluginHelper.Models
{
    public enum InstallationState
    {
        Idle, Connected, PluginReady, Preflight, SourceProbe, SourceReady, Uploading, IntegrityCheck,
        Installing, ReconnectChecking, Verifying, RestoringSource, Cleaning, Success,
        RetryableFailure, ActionRequiredFailure, ToolFailure
    }
    public enum InstallationFailureKind
    {
        None, Offline, DnsFailure, TimeInvalid, PackageManagerBusy, NoSpace, ReadOnlyFileSystem,
        Compatibility, Integrity, Makeself, PackageConflict, KernelAbi, InstallerScript,
        OfficialRepository, AdditionalRepository, ExternalRepository, UnknownInstaller,
        ConnectionLost, IdentityChanged, VerificationIncomplete, Tool, Cancelled, MirrorsExhausted, UpdateFailure, HashUnavailable
    }
    public sealed class InstallationFailure
    {
        public InstallationFailureKind Kind { get; set; }
        public bool CanRotateOfficial { get; set; }
        public bool CanRetry { get; set; }
        public string UserMessage { get; set; }
        public string Evidence { get; set; }
    }
    public sealed class RouterReadiness
    {
        public bool DefaultRoute { get; set; }
        public int DnsChecked { get; set; }
        public int DnsResolved { get; set; }
        public long UnixTime { get; set; }
        public long TmpBytes { get; set; }
        public long PersistentBytes { get; set; }
        public bool PersistentReadOnly { get; set; }
    }
    public sealed class InstallationAttempt
    {
        public string SourceKey { get; set; }
        public PluginInstallResult Result { get; set; }
        public InstallationFailure Failure { get; set; }
    }
    public sealed class InstallationWorkflowResult
    {
        public InstallationWorkflowResult()
        { Attempts = new List<InstallationAttempt>(); Warnings = new List<string>(); States = new List<InstallationState>(); }
        public InstallationState State { get; set; }
        public InstallationFailure Failure { get; set; }
        public PluginInstallResult Installation { get; set; }
        public RouterEnvironment Router { get; set; }
        public SoftwareEnvironmentResult Software { get; set; }
        public bool SourceChanged { get; set; }
        public bool SourceRestored { get; set; }
        public bool RestoreAttempted { get; set; }
        public bool PendingInstaller { get; set; }
        public string OriginalHash { get; set; }
        public string BackupPath { get; set; }
        public List<InstallationAttempt> Attempts { get; private set; }
        public List<string> Warnings { get; private set; }
        public List<InstallationState> States { get; private set; }
        public bool CanRetry { get { return Failure != null && Failure.CanRetry; } }
    }
    public sealed class InstallationWorkflowProgress
    {
        public InstallationState State { get; set; }
        public string Message { get; set; }
        public string Log { get; set; }
        public bool InstallerDispatched { get; set; }
        public InstallProgress Transfer { get; set; }
        public SoftwareEnvironmentResult Software { get; set; }
    }
}
