namespace iStoreOS.PluginHelper.Models
{
    public sealed class PackageUpdateResult
    {
        public int ExitCode { get; set; }
        public string Stdout { get; set; }
        public string Stderr { get; set; }
        public bool TimedOut { get; set; }
        public bool Success { get; set; }
        public bool OfficialRepositoryFailure { get; set; }
        public bool AdditionalRepositoryFailure { get; set; }
        public SoftwareErrorCategory ErrorCategory { get; set; }
    }
}
