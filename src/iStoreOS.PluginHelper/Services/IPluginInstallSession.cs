using System;
using System.IO;
using System.Threading;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // Typed operations keep router writes confined to this session and the original installer.
    public interface IPluginInstallSession
    {
        string Sanitize(string text);
        long TmpAvailable();
        void CreateDirectory(RemoteTempSession session);
        void Upload(Stream source, RemoteTempSession session, Action<long> progress, CancellationToken token);
        long RemoteFileSize(RemoteTempSession session);
        ProbeResult RemoteHash(RemoteTempSession session);
        ProbeResult Inspect(RemoteTempSession session, bool check);
        PackageSnapshot Snapshot(PackageManagerKind manager);
        bool PackageExists(PackageManagerKind manager, string name);
        void CheckRunner();
        void StartInstaller(RemoteTempSession session);
        InstallerPoll PollInstaller(RemoteTempSession session, long stdoutOffset, long stderrOffset);
        bool Cleanup(RemoteTempSession session);
    }
}
