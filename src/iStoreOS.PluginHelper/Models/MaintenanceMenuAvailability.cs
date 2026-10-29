namespace iStoreOS.PluginHelper.Models
{
    public enum MaintenanceMenuAvailabilityStatus { Ready, NotConnected, Busy, PendingInstallation, IdentityUnconfirmed, Closed, CheckingNetwork, NetworkUnavailable, ReadyViaComputerProxy }

    public sealed class MaintenanceMenuAvailability
    {
        public MaintenanceMenuAvailabilityStatus Status { get; private set; }
        public string Message { get; private set; }
        public bool CanOpen { get { return Status == MaintenanceMenuAvailabilityStatus.Ready || Status == MaintenanceMenuAvailabilityStatus.ReadyViaComputerProxy; } }
        public MaintenanceMenuAvailability(MaintenanceMenuAvailabilityStatus status, string message)
        { Status = status; Message = message ?? ""; }
    }
}
