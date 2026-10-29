using System;
using System.Collections.Generic;

namespace iStoreOS.PluginHelper.Models
{
    public enum MaintenancePreparationStatus { Rejected, Busy, Failed, Cancelled, PreparedExecutionDisabled, Completed, ScriptFailed, FinalStateUnknown }
    public enum MaintenancePreparationStage { Gate, Commit, Download, Validate, LocalFiles, LocalHash, RemoteDirectory, Upload, RemoteHash, Prepared, Cleanup, Executing, Network }
    public enum MaintenanceLogOrigin { PluginHelper, ThirdPartyScript }

    public sealed class ThirdPartyScriptDescriptor
    {
        public string PluginName { get; internal set; }
        public string FileName { get; internal set; }
        public string Project { get; internal set; }
        public string Repository { get; internal set; }
        public string SourceRef { get; internal set; }
        public string Commit { get; internal set; }
        public string DownloadUrl { get; internal set; }
        public string LicenseUrl { get; internal set; }
        public string Purpose { get; internal set; }
        public string Copyright { get; internal set; }
    }
    public sealed class MaintenanceOperationLog
    {
        public Guid OperationId { get; private set; }
        public MaintenanceLogOrigin Origin { get; private set; }
        public string Message { get; private set; }
        public DateTimeOffset Timestamp { get; private set; }
        public MaintenancePreparationStage Stage { get; private set; }
        public bool IsError { get; private set; }
        public MaintenanceOperationLog(Guid operationId, MaintenanceLogOrigin origin, string message,
            MaintenancePreparationStage stage = MaintenancePreparationStage.Gate, bool isError = false)
        { OperationId = operationId; Origin = origin; Message = origin == MaintenanceLogOrigin.ThirdPartyScript ? message ?? "" : (message ?? "").Replace('\r', ' ').Replace('\n', ' '); Timestamp = DateTimeOffset.Now; Stage = stage; IsError = isError; }
        public string Format()
        { return Timestamp.ToString("HH:mm:ss") + "  [" + (Origin == MaintenanceLogOrigin.PluginHelper ? "Plugin Helper" : "Third-party Script") + "] " + Message; }
    }
    public sealed class MaintenancePreparationResult
    {
        public Guid OperationId { get; internal set; }
        public MaintenancePreparationStatus Status { get; internal set; }
        public MaintenancePreparationStage Stage { get; internal set; }
        public ThirdPartyScriptDescriptor Descriptor { get; internal set; }
        public string ScriptSha256 { get; internal set; }
        public string LicenseSha256 { get; internal set; }
        public string RemoteDirectory { get; internal set; }
        public string LocalDirectory { get; internal set; }
        public bool IntegrityVerified { get; internal set; }
        public bool LocalCleanupSucceeded { get; internal set; }
        public bool RemoteCleanupSucceeded { get; internal set; }
        public string Error { get; internal set; }
        public string CleanupWarning { get; internal set; }
        public bool ExecutionAttempted { get; internal set; }
        public int? ExitCode { get; internal set; }
        public IList<MaintenanceOperationLog> Logs { get; internal set; }
        public MaintenancePreparationResult() { Logs = new List<MaintenanceOperationLog>().AsReadOnly(); }
    }
}
