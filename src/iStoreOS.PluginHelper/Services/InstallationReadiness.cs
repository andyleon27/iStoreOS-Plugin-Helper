using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Services
{
    public static class InstallationReadiness
    {
        public const long MinimumPersistentBytes = 1024 * 1024; // Only reject obviously exhausted storage; not an install-size estimate.
        public static InstallationFailure Check(RouterReadiness value, long packageBytes, long windowsUnixTime)
        {
            if (!value.DefaultRoute) return InstallFailureClassifier.Failure(InstallationFailureKind.Offline);
            if (value.DnsChecked >= 2 && value.DnsResolved == 0) return InstallFailureClassifier.Failure(InstallationFailureKind.DnsFailure);
            if (Math.Abs((double)value.UnixTime - windowsUnixTime) > TimeSpan.FromDays(1).TotalSeconds)
                return InstallFailureClassifier.Failure(InstallationFailureKind.TimeInvalid);
            if (value.PersistentReadOnly) return InstallFailureClassifier.Failure(InstallationFailureKind.ReadOnlyFileSystem);
            if (value.TmpBytes <= checked(packageBytes + PluginPackageInspection.SafetyMargin) || value.PersistentBytes < MinimumPersistentBytes)
                return InstallFailureClassifier.Failure(InstallationFailureKind.NoSpace);
            return null;
        }
        public static long UnixNow { get { return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; } }
        public const string Routes = "if command -v ip >/dev/null 2>&1; then ip -4 route show default; ip -6 route show default; else awk 'NR>1 && $2==\"00000000\" && $1!=\"lo\" { print \"default \" $1 }' /proc/net/route; fi";
        public const string Time = "date +%s";
        public const string PersistentSpace = "LC_ALL=C df -Pk /; if test -d /overlay; then LC_ALL=C df -Pk /overlay; fi";
        public const string PersistentMounts = "cat /proc/mounts";
        public const string PendingRunners = "for d in /tmp/istore-plugin-helper/*; do test -d \"$d\" && test ! -L \"$d\" || continue; b=${d##*/}; test ${#b} -eq 32 || continue; case \"$b\" in *[!a-f0-9]*) continue;; esac; if test -d \"$d/launch.once\" && test ! -f \"$d/exit.code\"; then printf 'BUSY_INSTALLER:%s\\n' \"$d\"; exit 0; fi; done; ";
        public const string Busy = PendingRunners + "for p in /proc/[0-9]*/comm; do test -r \"$p\" || continue; read -r n < \"$p\"; case \"$n\" in apk|opkg) printf 'BUSY\\n'; exit 0;; esac; done; " +
            "for f in /lib/apk/db/lock /var/lock/opkg.lock; do test -f \"$f\" || continue; i=$(LC_ALL=C ls -di \"$f\") || exit 2; " +
            "i=$(printf '%s\\n' \"$i\" | awk 'NR==1 {print $1}') || exit 2; case \"$i\" in ''|*[!0-9]*) exit 2;; esac; test -r /proc/locks || exit 2; " +
            "awk -v i=\"$i\" '$6 ~ (\":\" i \"$\") { found=1 } END { exit !found }' /proc/locks; locked=$?; " +
            "case \"$locked\" in 0) printf 'BUSY\\n'; exit 0;; 1) ;; *) exit 2;; esac; done; printf 'IDLE\\n'";
        public static bool HasRoute(string output) { return Regex.IsMatch(output ?? "", @"(?m)^default\s+(?!.*\blo\b).+"); }
        public static bool IsReadOnly(string mounts)
        { return Regex.IsMatch(mounts ?? "", @"(?m)^\S+\s+/(?:overlay)?\s+\S+\s+(?:[^\s,]+,)*ro(?:,|\s)"); }
        public static string DnsCommand(string host)
        {
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown) throw new ArgumentException("Invalid DNS host.");
            return "if command -v nslookup >/dev/null 2>&1 && command -v timeout >/dev/null 2>&1; then timeout 5 nslookup " + ShellArgument.Quote(host) + " >/dev/null 2>&1; printf '%s' \"$?\"; else printf 'UNKNOWN'; fi";
        }
        public static string RestoreCommand(PackageManagerKind manager, string text)
        {
            string target = SoftwareSourceCommands.DistfeedsPath(manager), folder = target.Substring(0, target.LastIndexOf('/'));
            string data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
            return "test ! -L " + ShellArgument.Quote(target) + " && test ! -L " + ShellArgument.Quote(folder) +
                " && { t=$(mktemp " + ShellArgument.Quote(folder + "/.istore-workflow.XXXXXXXX") + ") || exit 1; cp -p " + ShellArgument.Quote(target) +
                " \"$t\" && printf '%s' " + ShellArgument.Quote(data) + " | base64 -d > \"$t\" && mv -f -- \"$t\" " + ShellArgument.Quote(target) + "; }";
        }
    }
}
