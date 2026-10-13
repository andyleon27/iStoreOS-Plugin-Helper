namespace iStoreOS.PluginHelper.Models
{
    public enum CompatibilityStatus { Unknown, Compatible, Incompatible }

    public sealed class PluginPackageInfo
    {
        public string FilePath { get; set; }
        public string FileName { get; set; }
        public long FileSize { get; set; }
        public string PluginName { get; set; }
        public string PluginVersion { get; set; }
        public string Architecture { get; set; }
        public string SdkSeries { get; set; }
        public string PackageType { get; set; }
        public bool IsRecognizedPlugin { get; set; }
        public CompatibilityStatus CompatibilityStatus { get; set; }
        public string CompatibilityMessage { get; set; }
    }
}
