using System;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Utilities;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class RemoteTempSession
    {
        public const string Root = "/tmp/istore-plugin-helper";
        public string Id { get; private set; }
        public string Directory { get { return Root + "/" + Id; } }
        public string RunPath { get { return Directory + "/package.run"; } }
        private RemoteTempSession(string id) { Id = id; ValidateDirectory(Directory); }
        public static RemoteTempSession Create() { return new RemoteTempSession(Guid.NewGuid().ToString("N")); }
        public static void ValidateDirectory(string path)
        {
            if (path == null || !path.StartsWith(Root + "/", StringComparison.Ordinal) ||
                !Regex.IsMatch(path, @"\A/tmp/istore-plugin-helper/[a-f0-9]{32}\z"))
                throw new ArgumentException("远程临时路径不合法，禁止操作。");
        }
        internal string File(string name)
        {
            ValidateDirectory(Directory);
            if (name != "package.run" && name != "stdout.log" && name != "stderr.log" && name != "exit.code" && name != "launch.once")
                throw new ArgumentException("Invalid session file.");
            return ShellArgument.Quote(Directory + "/" + name);
        }
        public string CreateCommand()
        {
            // Refuse symlink roots and existing sessions; never take ownership of another directory.
            return "umask 077; test ! -L " + ShellArgument.Quote(Root) + " && { test -d " + ShellArgument.Quote(Root) +
                " || mkdir -- " + ShellArgument.Quote(Root) + "; } && test ! -L " + ShellArgument.Quote(Root) +
                " && test \"$(stat -c %u " + ShellArgument.Quote(Root) + ")\" = \"$(id -u)\" && test \"$(stat -c %a " + ShellArgument.Quote(Root) + ")\" = 700" +
                " && mkdir -- " + ShellArgument.Quote(Directory);
        }
        public string CleanupCommand()
        {
            ValidateDirectory(Directory);
            return "test ! -L " + ShellArgument.Quote(Root) + " && test ! -L " + ShellArgument.Quote(Directory) +
                " && rm -rf -- " + ShellArgument.Quote(Directory);
        }
    }
    public static class PluginInstallCommands
    {
        public const string TmpSpace = "LC_ALL=C df -Pk /tmp";
        public static string FileSize(RemoteTempSession s) { return "wc -c < " + s.File("package.run"); }
        public static string Hash(RemoteTempSession s)
        { return "if command -v sha256sum >/dev/null 2>&1; then sha256sum " + s.File("package.run") + "; else exit 127; fi"; }
        public static string Inspect(RemoteTempSession s, bool check)
        { return "cd " + ShellArgument.Quote(s.Directory) + " && LC_ALL=C TMPDIR=" + ShellArgument.Quote(s.Directory) + " sh " + s.File("package.run") + (check ? " --check" : " --info"); }
        public static string Snapshot(PackageManagerKind manager)
        {
            if (manager == PackageManagerKind.APK) return "LC_ALL=C apk info -v";
            if (manager == PackageManagerKind.OPKG) return "LC_ALL=C opkg list-installed";
            throw new ArgumentException("Unsupported package manager.");
        }
        public static string Query(PackageManagerKind manager, string name)
        {
            ValidatePackageName(name);
            if (manager == PackageManagerKind.APK) return "LC_ALL=C apk info -e " + ShellArgument.Quote(name);
            if (manager == PackageManagerKind.OPKG) return "LC_ALL=C opkg status " + ShellArgument.Quote(name);
            throw new ArgumentException("Unsupported package manager.");
        }
        public static void ValidatePackageName(string name)
        { if (name == null || !Regex.IsMatch(name, @"\A[a-zA-Z0-9][a-zA-Z0-9+_.-]*\z")) throw new ArgumentException("Invalid package name."); }
        public static string Launch(RemoteTempSession s)
        {
            // A detached runner survives a lost SSH channel. Its atomic exit marker is the only
            // proof allowing cleanup after launch. No kill, timeout or automatic second execution.
            string runner = "cd " + ShellArgument.Quote(s.Directory) + " || exit 125; " +
                "LC_ALL=C TMPDIR=" + ShellArgument.Quote(s.Directory) + " sh " + s.File("package.run") + " > " + s.File("stdout.log") + " 2> " + s.File("stderr.log") +
                "; rc=$?; printf '%s\\n' \"$rc\" > exit.tmp && mv -- exit.tmp exit.code";
            return "command -v nohup >/dev/null 2>&1 && command -v base64 >/dev/null 2>&1 && " +
                "test ! -L " + ShellArgument.Quote(RemoteTempSession.Root) + " && test ! -L " + ShellArgument.Quote(s.Directory) +
                " && mkdir -- " + s.File("launch.once") + " && { nohup sh -c " + ShellArgument.Quote(runner) +
                " </dev/null >/dev/null 2>&1 & }";
        }
        public static string ReadExit(RemoteTempSession s)
        { return "if test -f " + s.File("exit.code") + "; then cat -- " + s.File("exit.code") + "; else printf 'RUNNING'; fi"; }
        public static string ReadLog(RemoteTempSession s, bool stderr, long offset)
        {
            if (offset < 0) throw new ArgumentException("Invalid log offset.");
            return "if test -f " + s.File(stderr ? "stderr.log" : "stdout.log") + "; then dd if=" +
                s.File(stderr ? "stderr.log" : "stdout.log") + " bs=1 skip=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " count=65536 2>/dev/null | base64; fi";
        }
        public static string Exists(RemoteTempSession s)
        { return "if test -e " + ShellArgument.Quote(s.Directory) + " || test -L " + ShellArgument.Quote(s.Directory) + "; then exit 1; fi"; }
    }
}
