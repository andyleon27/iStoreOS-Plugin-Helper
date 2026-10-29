using System;

namespace iStoreOS.PluginHelper.Models
{
    public enum MaintenanceMenuRunStatus
    {
        NotStarted, Starting, Running, AwaitingClose, ExitedNormally, ExitedWithError, Unknown
    }

    // A detached observation, never an authority for deleting remote files.
    public sealed class MaintenanceMenuRuntimeInfo
    {
        public Guid OperationId { get; internal set; }
        public Guid RunId { get; internal set; }
        public MaintenanceMenuRunStatus Status { get; internal set; }
        public int? ProcessId { get; internal set; }
        public long? ProcessStartTicks { get; internal set; }
        public long? StartedAtUnixSeconds { get; internal set; }
        public int? ExitCode { get; internal set; }
        public bool ChannelClosed { get; internal set; }
        public bool CanCleanup { get; internal set; }
        public bool CleanupCompleted { get; internal set; }
        public int DuplicateMarkers { get; internal set; }
        public int IgnoredMarkers { get; internal set; }
        public string Diagnostic { get; internal set; }
    }
}

