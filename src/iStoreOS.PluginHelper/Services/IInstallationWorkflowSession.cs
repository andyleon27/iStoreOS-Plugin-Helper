using System;
using System.Collections.Generic;
using System.Threading;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // Composes existing typed operations. Reconnection replaces the underlying SSH client;
    // callers obtain fresh adapters instead of continuing to use a disposed client.
    public interface IInstallationWorkflowSession
    {
        bool IsConnected { get; }
        RouterEnvironment ConnectAndDetect(RouterEnvironment expected, bool force, Action<string> log, CancellationToken token);
        IPluginInstallSession Installer { get; }
        ISoftwareSourceSession Sources { get; }
        RouterReadiness ReadReadiness(IList<RepositoryFeed> feeds);
        bool PackageManagerBusy(PackageManagerKind manager);
        bool WasLaunched(RemoteTempSession remote);
        ProbeResult RestoreOriginal(PackageManagerKind manager, string text, string backupPath);
        bool DeleteBackup(string backupPath);
        string Sanitize(string text);
    }
    public sealed class InstallationPolicyException : Exception
    {
        public InstallationFailureKind Kind { get; private set; }
        public InstallationPolicyException(InstallationFailureKind kind, string message) : base(message) { Kind = kind; }
    }
    public sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> action;
        public InlineProgress(Action<T> action) { this.action = action; }
        public void Report(T value) { action(value); }
    }
}
