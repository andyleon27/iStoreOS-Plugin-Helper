using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Services
{
    public static class MaintenanceScriptIntegrity
    {
        public static void ValidateScript(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 1048576) throw new InvalidDataException("脚本为空或超过 1 MiB。");
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.IndexOf('\0') >= 0 || !(text.StartsWith("#!/bin/sh\n", StringComparison.Ordinal) || text.StartsWith("#!/bin/sh\r\n", StringComparison.Ordinal)))
                throw new InvalidDataException("下载内容不是预期的原始 shell 脚本；未修改或修补内容。");
        }
        public static void ValidateLicense(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 65536) throw new InvalidDataException("LICENSE 为空或超过限制。");
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (!text.Contains("MIT License") || !text.Contains(ThirdPartyScriptCatalog.Copyright) || !text.Contains("Permission is hereby granted") || !text.Contains("THE SOFTWARE IS PROVIDED"))
                throw new InvalidDataException("upstream LICENSE 与已知 MIT 许可信息不符；停止准备。");
        }
        public static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public static string ParseRemoteHash(string output, string expectedPath)
        {
            var match = Regex.Match(output ?? "", @"\A([0-9a-fA-F]{64})[ \t]+\*?" + Regex.Escape(expectedPath) + @"\r?\n?\z");
            if (!match.Success) throw new InvalidDataException("远端 SHA256 输出无效或路径不匹配。");
            return match.Groups[1].Value.ToLowerInvariant();
        }
    }
    public sealed class RemoteMaintenanceWorkspace
    {
        public const string BaseDirectory = "/tmp/istoreos-plugin-helper";
        public const string MenuLauncherFile = "helper-launcher.sh";
        private readonly bool includeMenuLauncher;
        public Guid OperationId { get; private set; }
        public string Directory { get; private set; }
        public string ScriptFile { get { return ThirdPartyScriptCatalog.MenuScriptFile; } }
        // Only these payloads may be uploaded/hashed. Runtime state is never an upload input.
        public string[] Files { get { return includeMenuLauncher ? new[] { ScriptFile, "LICENSE", "NOTICE.txt", MenuLauncherFile } : new[] { ScriptFile, "LICENSE", "NOTICE.txt" }; } }
        public string[] RuntimeFiles { get { return new[] { "menu.pid", "menu.start", "menu.exit", "menu.exit.tmp" }; } }
        public string[] OwnedFiles { get { return Files.Concat(new[] { MenuLauncherFile }).Concat(RuntimeFiles).Distinct(StringComparer.Ordinal).ToArray(); } }
        private string Owner { get { return Directory + "/.owner"; } }
        private RemoteMaintenanceWorkspace(Guid operationId, bool includeMenuLauncher)
        {
            if (operationId == Guid.Empty) throw new ArgumentException("Invalid maintenance workspace.");
            OperationId = operationId;
            this.includeMenuLauncher = includeMenuLauncher;
            Directory = BaseDirectory + "/maintenance-" + operationId.ToString("N");
        }
        public static RemoteMaintenanceWorkspace CreateMenu(Guid operationId, bool includeLauncher = false)
        { return new RemoteMaintenanceWorkspace(operationId, includeLauncher); }
        public string FilePath(string file)
        { if (!Files.Contains(file, StringComparer.Ordinal)) throw new ArgumentException("Not an owned maintenance file."); return Directory + "/" + file; }
        public string OwnedFilePath(string file)
        { if (!OwnedFiles.Contains(file, StringComparer.Ordinal)) throw new ArgumentException("Not an owned maintenance file."); return Directory + "/" + file; }
        private string CheckOwner()
        {
            return "test ! -L " + ShellArgument.Quote(BaseDirectory) + " && test ! -L " + ShellArgument.Quote(Directory) +
                " && test -f " + ShellArgument.Quote(Owner) + " && test ! -L " + ShellArgument.Quote(Owner) +
                " && test \"$(cat " + ShellArgument.Quote(Owner) + ")\" = " + ShellArgument.Quote(OperationId.ToString("N"));
        }
        public string CreateCommand()
        {
            return "umask 077; test ! -L " + ShellArgument.Quote(BaseDirectory) + " && mkdir -p " + ShellArgument.Quote(BaseDirectory) +
                " && test ! -L " + ShellArgument.Quote(BaseDirectory) + " && mkdir " + ShellArgument.Quote(Directory) +
                " && printf '%s' " + ShellArgument.Quote(OperationId.ToString("N")) + " > " + ShellArgument.Quote(Owner);
        }
        public string HashCommand(string file)
        { string path = FilePath(file); return CheckOwner() + " && test -f " + ShellArgument.Quote(path) + " && test ! -L " + ShellArgument.Quote(path) + " && sha256sum " + ShellArgument.Quote(path); }
        public string CleanupCommand()
        {
            string paths = string.Join(" ", Files.Select(f => ShellArgument.Quote(FilePath(f))));
            string noLinks = string.Join(" && ", Files.Select(f => "test ! -L " + ShellArgument.Quote(FilePath(f))));
            return "test ! -L " + ShellArgument.Quote(BaseDirectory) + " || exit 1; " +
                "if test ! -e " + ShellArgument.Quote(Directory) + " && test ! -L " + ShellArgument.Quote(Directory) +
                "; then rmdir " + ShellArgument.Quote(BaseDirectory) + " 2>/dev/null || :; exit 0; fi; " +
                CheckOwner() + " && " + MenuCleanupGuard() + " && " + noLinks + " && rm -f -- " + paths + " " + ShellArgument.Quote(Owner) +
                " && rmdir " + ShellArgument.Quote(Directory) + " || exit 1; rmdir " + ShellArgument.Quote(BaseDirectory) + " 2>/dev/null || :";
        }
        private string MenuCleanupGuard()
        {
            // Stage 2 has no running menu. Runtime/unknown files require a later state-aware cleanup.
            // Check every entry before removing any payload or its owner marker.
            string allowed = string.Join("|", Files.Select(f => ShellArgument.Quote(FilePath(f))).Concat(new[] { ShellArgument.Quote(Owner) }));
            string directory = ShellArgument.Quote(Directory);
            return "{ for f in " + directory + "/* " + directory + "/.[!.]* " + directory + "/..?*; do " +
                "if test -e \"$f\" || test -L \"$f\"; then test -f \"$f\" && test ! -L \"$f\" || exit 1; " +
                "case \"$f\" in " + allowed + ") ;; *) exit 1;; esac; fi; done; }";
        }
    }
    // Short-lived Windows workspace. Delete only the known files and the owned empty leaf.
    internal sealed class LocalMaintenanceWorkspace
    {
        public string DirectoryPath { get; private set; }
        private readonly string[] files;
        public LocalMaintenanceWorkspace(Guid id, string[] files)
        {
            this.files = (string[])files.Clone();
            DirectoryPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "iStoreOS.PluginHelper-maintenance-" + id.ToString("N")));
            if (Directory.Exists(DirectoryPath) || File.Exists(DirectoryPath)) throw new IOException("Windows 临时任务目录已存在，未覆盖。");
            Directory.CreateDirectory(DirectoryPath);
        }
        public string FilePath(string name)
        { if (!files.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Unknown local task file."); return Path.Combine(DirectoryPath, name); }
        public void Write(string name, byte[] bytes)
        { using (var stream = new FileStream(FilePath(name), FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(bytes, 0, bytes.Length); }
        public void Cleanup()
        {
            if (!Directory.Exists(DirectoryPath)) return;
            if ((File.GetAttributes(DirectoryPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("Windows 临时目录变为重解析点，未继续删除。");
            foreach (string name in files) File.Delete(FilePath(name));
            Directory.Delete(DirectoryPath, false);
        }
    }
}
