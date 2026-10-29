using System;

namespace iStoreOS.PluginHelper.Models
{
    public enum MaintenanceNetworkPath { None, Direct, ComputerProxy }
    public enum MaintenanceNetworkState { NotChecked, Checking, Available, Unavailable }
    public sealed class MaintenanceNetworkSnapshot
    {
        public MaintenanceNetworkState State { get; internal set; }
        public MaintenanceNetworkPath Path { get; internal set; }
        public int LocalProxyPort { get; internal set; }
        public DateTimeOffset CheckedAt { get; internal set; }
        public ComputerProxyProbe RouterRaw { get; internal set; }
        public ComputerProxyProbe RouterApi { get; internal set; }
        public string Code { get; internal set; }
        public bool Available { get { return State == MaintenanceNetworkState.Available && Path != MaintenanceNetworkPath.None; } }
        public bool NeedsRefresh { get { return State == MaintenanceNetworkState.NotChecked || State != MaintenanceNetworkState.Checking &&
            DateTimeOffset.UtcNow - CheckedAt > TimeSpan.FromSeconds(Available ? 30 : 10); } }
    }
}
