namespace iStoreOS.PluginHelper.Models
{
    public sealed class MaintenanceMenuRunResult
    {
        public MaintenancePreparationResult Preparation { get; internal set; }
        public MaintenanceMenuRuntimeInfo Runtime { get; internal set; }
        public MaintenanceNetworkPath NetworkPath { get; internal set; }
        public ComputerProxyCloseResult ProxyCleanup { get; internal set; }
    }
}
