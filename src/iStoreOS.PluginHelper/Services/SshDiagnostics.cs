using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Renci.SshNet;

namespace iStoreOS.PluginHelper.Services
{
    internal static class SshDiagnostics
    {
        // Explicit public negotiation-field allowlist. Never serialize connection/authentication objects.
        internal static string Negotiated(ConnectionInfo info, string role, bool failed)
        {
            var text = new StringBuilder(failed ? "SSH 协商（失败前）" : "SSH 协商");
            text.Append("[").Append(Safe(role)).Append("]：");
            text.Append("Server=").Append(Safe(info == null ? null : info.ServerVersion));
            text.Append("；KEX=").Append(Safe(info == null ? null : info.CurrentKeyExchangeAlgorithm));
            text.Append("；HostKey=").Append(Safe(info == null ? null : info.CurrentHostKeyAlgorithm));
            text.Append("；ClientCipher=").Append(Safe(info == null ? null : info.CurrentClientEncryption));
            text.Append("；ServerCipher=").Append(Safe(info == null ? null : info.CurrentServerEncryption));
            text.Append("；ClientMAC=").Append(Mac(info, true));
            text.Append("；ServerMAC=").Append(Mac(info, false));
            text.Append("；ClientCompression=").Append(Safe(info == null ? null : info.CurrentClientCompressionAlgorithm));
            text.Append("；ServerCompression=").Append(Safe(info == null ? null : info.CurrentServerCompressionAlgorithm));
            return text.ToString();
        }
        internal static string RuntimeVersion()
        {
            var assembly = typeof(SshClient).Assembly;
            var attributes = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            string library = attributes.Length == 0 ? assembly.GetName().Version.ToString() : ((AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion;
            string runtime = FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location).FileVersion;
            return "SSH runtime：SSH.NET=" + Safe(library) + "；mscorlib=" + Safe(runtime);
        }
        private static string Mac(ConnectionInfo info, bool client)
        {
            if (info == null) return "unknown";
            string cipher = client ? info.CurrentClientEncryption : info.CurrentServerEncryption;
            CipherInfo value;
            if (cipher != null && info.Encryptions.TryGetValue(cipher, out value) && value.IsAead) return "AEAD（集成认证，无独立 MAC）";
            return Safe(client ? info.CurrentClientHmacAlgorithm : info.CurrentServerHmacAlgorithm);
        }
        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            var text = new StringBuilder();
            foreach (char c in value) { if (text.Length >= 256) break; text.Append(char.IsControl(c) ? ' ' : c); }
            return text.ToString();
        }
    }
}
