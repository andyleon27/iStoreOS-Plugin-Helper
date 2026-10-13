using System;

namespace iStoreOS.PluginHelper.Services
{
    public enum RouterProbe { OsRelease, OpenWrtRelease, PackageManager, QuickStartSources, QuickStartCurrentSource }

    public sealed class ProbeResult
    {
        public int ExitStatus { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
    }

    public interface IReadOnlyRouterSession
    {
        ProbeResult Execute(RouterProbe probe);
    }

    public static class ReadOnlyRouterCommands
    {
        // An enum allowlist prevents UI values and local file paths from becoming shell code.
        // No arbitrary-command, SFTP, upload or installer API is exposed in V0.1.
        public static string Get(RouterProbe probe)
        {
            switch (probe)
            {
                case RouterProbe.OsRelease: return "cat /etc/os-release";
                case RouterProbe.OpenWrtRelease: return "cat /etc/openwrt_release";
                case RouterProbe.PackageManager:
                    return "if test -f /etc/apk/repositories.d/distfeeds.list; then printf 'APK'; " +
                           "elif test -f /etc/opkg/distfeeds.conf; then printf 'OPKG'; else printf 'Unknown'; fi";
                case RouterProbe.QuickStartSources:
                    return HttpGet("http://127.0.0.1:3038/cgi-bin/luci/istore/guide/soft-source/list/");
                case RouterProbe.QuickStartCurrentSource:
                    return HttpGet("http://127.0.0.1:3038/cgi-bin/luci/istore/guide/soft-source/");
                default: throw new ArgumentOutOfRangeException("probe");
            }
        }

        private static string HttpGet(string url)
        {
            return "if command -v curl >/dev/null 2>&1; then " +
                "curl --silent --show-error --fail --max-time 4 --max-filesize 262144 --noproxy '*' " +
                "-H 'X-Forwarded-Sid: ssh-root-poc' '" + url + "'; " +
                "elif command -v wget >/dev/null 2>&1; then " +
                "wget -q -T 4 -O - --header='X-Forwarded-Sid: ssh-root-poc' '" + url + "'; " +
                "else printf '__NO_HTTP_CLIENT__'; fi";
        }
    }
}
