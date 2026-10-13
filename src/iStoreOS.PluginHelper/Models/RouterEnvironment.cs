namespace iStoreOS.PluginHelper.Models
{
    public enum PackageManagerKind { Unknown, APK, OPKG }

    public sealed class RouterEnvironment
    {
        public string Version { get; set; }
        public string SdkSeries { get; set; }
        public string Architecture { get; set; }
        public PackageManagerKind PackageManager { get; set; }
        public bool QuickStartAvailable { get; set; }
        public string HostKeyFingerprint { get; set; }
        public bool IsConnected { get; set; }
    }
}
