using System.Collections.Generic;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // Small typed boundary for the existing SSH session and deterministic workflow tests.
    // There is no arbitrary-command, upload, install, UCI or service-control method.
    public interface ISoftwareSourceSession
    {
        ProbeResult ReadOfficial(PackageManagerKind manager, bool rom);
        IDictionary<string, string> ReadAdditional(PackageManagerKind manager);
        ProbeResult QuickStartGet(bool list);
        ProbeResult QuickStartSwitch(string identity);
        ProbeResult ProbeIndex(string url);
        PackageUpdateResult Update(PackageManagerKind manager);
        string BackupOfficial(PackageManagerKind manager);
        ProbeResult ReadBackup(string backupPath);
        ProbeResult RestoreBackup(PackageManagerKind manager, string backupPath);
        string Sanitize(string text);
    }
}
