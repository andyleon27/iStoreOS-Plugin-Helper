using System.Collections.Generic;

namespace iStoreOS.PluginHelper.Models
{
    public enum RepositoryKind { Official, FixedAuxiliary, Custom }
    public sealed class SoftwareSourceInfo
    {
        public string Identity { get; set; }
        public string Name { get; set; }
        public string Url { get; set; }
        public override string ToString() { return (string.IsNullOrWhiteSpace(Name) ? "自定义 / 未知来源" : Name) + (string.IsNullOrEmpty(Identity) ? "" : "（" + Identity + "）"); }
    }
    public sealed class RepositoryFeed
    {
        public string Name { get; set; }
        public string ConfigPath { get; set; }
        public string IndexUrl { get; set; }
        public RepositoryKind Kind { get; set; }
        public string ParseError { get; set; }
    }
    public sealed class SoftwareSourceSnapshot
    {
        public string DistfeedsPath { get; set; }
        public string OriginalText { get; set; }
        public string RomText { get; set; }
        public SoftwareSourceInfo CurrentSource { get; set; }
        public List<RepositoryFeed> OfficialFeeds { get; set; }
        public List<RepositoryFeed> AdditionalFeeds { get; set; }
    }
    public sealed class SoftwareRepairPlan
    {
        public SoftwareSourceInfo OriginalSource { get; set; }
        public SoftwareSourceInfo ProposedSource { get; set; }
        public RepositoryHealthResult Probe { get; set; }
    }
}
