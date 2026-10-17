using System;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Services
{
    public static class SoftwareSourceCommands
    {
        public static string DistfeedsPath(PackageManagerKind manager)
        {
            if (manager == PackageManagerKind.APK) return "/etc/apk/repositories.d/distfeeds.list";
            if (manager == PackageManagerKind.OPKG) return "/etc/opkg/distfeeds.conf";
            throw new NotSupportedException("未知包管理器，不执行软件源操作。");
        }
        public static string ReadOfficial(PackageManagerKind manager, bool rom)
        { return "cat " + ShellArgument.Quote((rom ? "/rom" : "") + DistfeedsPath(manager)); }
        public static string ListAdditional(PackageManagerKind manager)
        {
            string pattern = manager == PackageManagerKind.APK ? "/etc/apk/repositories.d/*.list" : manager == PackageManagerKind.OPKG ? "/etc/opkg/*.conf" : null;
            if (pattern == null) throw new NotSupportedException();
            return "for f in " + pattern + "; do test -f \"$f\" || continue; printf '%s\\n' \"$f\"; done";
        }
        public static bool IsAdditionalPath(PackageManagerKind manager, string path)
        {
            string pattern = manager == PackageManagerKind.APK ? @"^/etc/apk/repositories\.d/[A-Za-z0-9_.-]+\.list$" : @"^/etc/opkg/[A-Za-z0-9_.-]+\.conf$";
            return path != null && (manager == PackageManagerKind.APK || manager == PackageManagerKind.OPKG) && Regex.IsMatch(path, pattern) &&
                path != DistfeedsPath(manager) && path != "/etc/opkg/opkg.conf";
        }
        public static string ReadAdditional(PackageManagerKind manager, string path)
        { if (!IsAdditionalPath(manager, path)) throw new ArgumentException("Invalid additional feed path."); return "cat " + ShellArgument.Quote(path); }
        public static string QuickStartSwitch(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity) || identity.Length > 256 || identity.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("Invalid source identity.");
            string json = new JavaScriptSerializer().Serialize(new { softSourceIdentity = identity });
            return "curl --silent --show-error --fail --connect-timeout 5 --max-time 20 --noproxy '*' " +
                "-H 'X-Forwarded-Sid: ssh-root-poc' -H 'Content-Type: application/json' --data-binary " + ShellArgument.Quote(json) +
                " 'http://127.0.0.1:3038/cgi-bin/luci/istore/guide/soft-source/'";
        }
        public static string ProbeIndex(string url)
        {
            if (!ShellArgument.IsHttpUrl(url)) throw new ArgumentException("Invalid HTTP repository URL.");
            return "curl --silent --show-error -L -r 0-0 --max-filesize 1 --connect-timeout 5 --max-time 15 " +
                "--proto '=http,https' --proto-redir '=http,https' -o /dev/null --write-out 'ISTORE_PROBE|%{http_code}|%{time_total}' -- " + ShellArgument.Quote(url);
        }
        public static string ProbeIndexFast(string url)
        {
            if (!ShellArgument.IsHttpUrl(url)) throw new ArgumentException("Invalid HTTP repository URL.");
            return "curl --silent --show-error -L -r 0-0 --max-filesize 1 --connect-timeout 2 --max-time 3 " +
                "--proto '=http,https' --proto-redir '=http,https' -o /dev/null --write-out 'ISTORE_PROBE|%{http_code}|%{time_total}' -- " + ShellArgument.Quote(url);
        }
        public static string Update(PackageManagerKind manager)
        {
            string command = manager == PackageManagerKind.APK ? "apk update" : manager == PackageManagerKind.OPKG ? "opkg update" : null;
            if (command == null) throw new NotSupportedException();
            // Bound the router-side process too, when BusyBox's timeout applet is present.
            return "if command -v timeout >/dev/null 2>&1; then timeout 120 " + command + "; else " + command + "; fi";
        }
        public static string BackupOfficial(PackageManagerKind manager)
        {
            return "umask 077; d=$(mktemp -d /tmp/istore-plugin-helper.XXXXXXXX) || exit 1; " +
                "cp -p " + ShellArgument.Quote(DistfeedsPath(manager)) + " \"$d/distfeeds.backup\" || exit 1; printf '%s' \"$d/distfeeds.backup\"";
        }
        public static void ValidateBackupPath(string path)
        { if (path == null || !Regex.IsMatch(path, @"^/tmp/istore-plugin-helper\.[A-Za-z0-9]{8}/distfeeds\.backup\z")) throw new ArgumentException("Invalid recovery path."); }
        public static string ReadBackup(string path) { ValidateBackupPath(path); return "cat " + ShellArgument.Quote(path); }
        public static string RestoreBackup(PackageManagerKind manager, string path)
        {
            ValidateBackupPath(path);
            string target = DistfeedsPath(manager);
            string directory = target.Substring(0, target.LastIndexOf('/'));
            return "t=$(mktemp " + ShellArgument.Quote(directory + "/.istore-plugin-helper.XXXXXXXX") + ") || exit 1; cp -p " +
                ShellArgument.Quote(path) + " \"$t\" && mv -f \"$t\" " + ShellArgument.Quote(target);
        }
    }
}
