using System;
using System.Collections.Generic;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class HostKeyTrustStore
    {
        // V0.1 TOFU is intentionally scoped to this application run. A future persistent
        // store can replace this class without changing HostKeyReceived's trust decision.
        private readonly Dictionary<string, string> fingerprints =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object sync = new object();

        public bool TryTrust(string host, int port, string fingerprint, out bool firstSeen)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fingerprint))
                throw new ArgumentException("Host and fingerprint are required.");
            string key = host.Trim() + ":" + port;
            lock (sync)
            {
                string previous;
                firstSeen = !fingerprints.TryGetValue(key, out previous);
                if (firstSeen) fingerprints.Add(key, fingerprint);
                return firstSeen || string.Equals(previous, fingerprint, StringComparison.Ordinal);
            }
        }
        public bool IsTrusted(string host, int port, string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(fingerprint)) return false;
            lock (sync)
            {
                string previous;
                return fingerprints.TryGetValue(host.Trim() + ":" + port, out previous) &&
                    string.Equals(previous, fingerprint, StringComparison.Ordinal);
            }
        }
    }
}
