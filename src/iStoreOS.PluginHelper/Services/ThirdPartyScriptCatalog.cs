using System;
using System.IO;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public static class ThirdPartyScriptCatalog
    {
        public const string Project = "slobys/openclash-auto-installer";
        public const string Repository = "https://github.com/" + Project;
        public const string MenuScriptFile = "menu.sh";
        public const string MainMenuUrl = "https://raw.githubusercontent.com/" + Project + "/main/" + MenuScriptFile;
        public const string MainMenuLicenseUrl = "https://raw.githubusercontent.com/" + Project + "/main/LICENSE";
        public const string Copyright = "Copyright (c) 2026 slobys";
        public const string Notice = "openclash-auto-installer\n" + Copyright + "\nMIT License\nRepository: " + Repository + "\nThird-party execution logic and compatibility belong to the upstream project.\n";
        public static void ValidateCommit(string commit)
        { if (!Regex.IsMatch(commit ?? "", @"\A[0-9a-fA-F]{40}\z")) throw new InvalidDataException("必须使用完整的 40 位 upstream commit SHA。"); }
        public static string RawUrl(string commit, string fileName)
        {
            ValidateCommit(commit);
            if (fileName != MenuScriptFile && fileName != "LICENSE") throw new ArgumentException("Unsupported upstream file.");
            return "https://raw.githubusercontent.com/" + Project + "/" + commit.ToLowerInvariant() + "/" + fileName;
        }
        public static bool IsSourceUrl(string url)
        {
            if (url == MainMenuUrl || url == MainMenuLicenseUrl) return true;
            string prefix = "https://raw.githubusercontent.com/" + Project + "/";
            if (url == null || !url.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string[] parts = url.Substring(prefix.Length).Split('/');
            return parts.Length == 2 && Regex.IsMatch(parts[0], @"\A[0-9a-f]{40}\z") && (parts[1] == "LICENSE" || parts[1] == MenuScriptFile);
        }
        public static ThirdPartyScriptDescriptor DescribeMenu(string commit)
        {
            ValidateCommit(commit);
            return new ThirdPartyScriptDescriptor { PluginName = "维护菜单", FileName = MenuScriptFile, Project = Project,
                Repository = Repository, SourceRef = "commit", Commit = commit.ToLowerInvariant(),
                DownloadUrl = RawUrl(commit, MenuScriptFile), LicenseUrl = RawUrl(commit, "LICENSE"),
                Purpose = "Menu", Copyright = Copyright };
        }
        public static ThirdPartyScriptDescriptor DescribeMenuMain()
        {
            return new ThirdPartyScriptDescriptor { PluginName = "维护菜单", FileName = MenuScriptFile, Project = Project,
                Repository = Repository, SourceRef = "main", Commit = null, DownloadUrl = MainMenuUrl,
                LicenseUrl = MainMenuLicenseUrl, Purpose = "Menu", Copyright = Copyright };
        }
        public static string BundledLicense()
        {
            using (var stream = typeof(ThirdPartyScriptCatalog).Assembly.GetManifestResourceStream("iStoreOS.PluginHelper.ThirdParty.openclash-auto-installer.LICENSE"))
            {
                if (stream == null) throw new InvalidDataException("Third-party MIT License resource missing.");
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
    }
}
