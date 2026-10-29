using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace iStoreOS.PluginHelper.Services
{
    // Candidates only. A listening SOCKS port is not advertised as a verified HTTP
    // proxy; VerifyComputerProxyTunnelAsync must validate the HTTPS CONNECT path.
    public static class WindowsComputerProxyDetector
    {
        public static IList<int> GetListeningCandidates()
        {
            string configured = null;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                if (key != null) configured = key.GetValue("ProxyServer") as string;
            return Candidates(configured, IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(x => IPAddress.IsLoopback(x.Address) || x.Address.Equals(IPAddress.Any) || x.Address.Equals(IPAddress.IPv6Any)).Select(x => x.Port));
        }
        internal static IList<int> Candidates(string configured, IEnumerable<int> listeners)
        {
            var candidates = new List<int>();
            foreach (string raw in (configured ?? "").Split(';')) {
                string entry = raw.Trim(); int equal = entry.IndexOf('=');
                if (equal >= 0) { string scheme = entry.Substring(0, equal); if (scheme != "http" && scheme != "https") continue; entry = entry.Substring(equal + 1); }
                var match = Regex.Match(entry, @"\A(?:http://)?(?:localhost|127\.0\.0\.1):([0-9]{1,5})\z"); int port;
                if (match.Success && int.TryParse(match.Groups[1].Value, out port) && port > 0 && port <= 65535) candidates.Add(port);
            }
            candidates.AddRange(new[] { 7897, 7890, 8080, 10809, 8888, 1080 });
            var available = new HashSet<int>(listeners); return candidates.Distinct().Where(available.Contains).ToList();
        }
    }
}
