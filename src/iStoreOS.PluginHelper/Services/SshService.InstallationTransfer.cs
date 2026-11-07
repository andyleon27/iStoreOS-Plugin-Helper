using System;
using System.Security.Cryptography;
using Renci.SshNet;

namespace iStoreOS.PluginHelper.Services
{
    public sealed partial class SshService
    {
        private SftpClient CreateTrustedInstallationTransfer(string fingerprint)
        {
            var transfer = (InstallationSftpClient)SshConnectionFactory.Sftp(connectionFactory(), true, text => connectionDiagnostics(sanitizeLog(text)));
            TrustInstallationTransfer(transfer, fingerprint, accepted => transfer.HostKeyAccepted = accepted);
            return transfer;
        }
        private ScpClient CreateTrustedScp(string fingerprint)
        {
            // Paths come only from RemoteTempSession; explicitly quote the remote shell path.
            var transfer = SshConnectionFactory.Scp(connectionFactory(), text => connectionDiagnostics(sanitizeLog(text)));
            TrustInstallationTransfer(transfer, fingerprint, null);
            return transfer;
        }
        private void TrustInstallationTransfer(BaseClient transfer, string fingerprint, Action<bool> decision)
        {
            SshConnectionFactory.Trust(transfer, key => {
                string actual = SshConnectionFactory.Fingerprint(key);
                return actual == fingerprint && trustStore.IsTrusted(transfer.ConnectionInfo.Host, transfer.ConnectionInfo.Port, actual);
            }, decision);
        }
    }
}
