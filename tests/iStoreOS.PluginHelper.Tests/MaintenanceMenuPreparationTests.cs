using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class MaintenanceMenuPreparationTests
    {
        private const string Commit = "0123456789abcdef0123456789abcdef01234567";
        private static readonly byte[] Menu = Encoding.UTF8.GetBytes("#!/bin/sh\r\n# inert menu fixture 中文\r\nprintf 'fixture only\\n'\r\n");

        public static void Run(Action<string, Action> test)
        {
            test("Menu preparation main descriptor uses only the two public branch files without a fabricated SHA", () => {
                var descriptor = ThirdPartyScriptCatalog.DescribeMenuMain();
                Equal("main", descriptor.SourceRef); Equal<string>(null, descriptor.Commit); Equal("Menu", descriptor.Purpose);
                Equal("https://raw.githubusercontent.com/slobys/openclash-auto-installer/main/menu.sh", descriptor.DownloadUrl);
                Equal("https://raw.githubusercontent.com/slobys/openclash-auto-installer/main/LICENSE", descriptor.LicenseUrl);
            });
            test("Menu preparation descriptor pins menu and LICENSE to the same full SHA", () => {
                var descriptor = ThirdPartyScriptCatalog.DescribeMenu(Commit.ToUpperInvariant());
                Equal(Commit, descriptor.Commit); Equal("commit", descriptor.SourceRef);
                Equal("menu.sh", descriptor.FileName); Equal("Menu", descriptor.Purpose);
                Equal(ThirdPartyScriptCatalog.Project, descriptor.Project);
                Equal(ThirdPartyScriptCatalog.Repository, descriptor.Repository);
                Equal(ThirdPartyScriptCatalog.Copyright, descriptor.Copyright);
                Equal(ThirdPartyScriptCatalog.RawUrl(Commit, "menu.sh"), descriptor.DownloadUrl);
                Equal(ThirdPartyScriptCatalog.RawUrl(Commit, "LICENSE"), descriptor.LicenseUrl);
            });
            foreach (string invalid in new[] { null, "", "main", "0123456", new string('a', 39), new string('a', 41), new string('g', 40), Commit + "\n", "../" + Commit, Commit + ";id" })
            {
                string value = invalid;
                test("Menu preparation invalid SHA rejected before side effects " + (value ?? "<null>"), () => {
                    var http = new Http(); var remote = new Remote();
                    Throws<InvalidDataException>(() => Service(http).PrepareMenuAtCommitAsync(remote, value, null, CancellationToken.None));
                    Equal(0, http.Urls.Count); Equal(0, remote.Creates);
                    Throws<InvalidDataException>(() => ThirdPartyScriptCatalog.DescribeMenu(value));
                });
            }
            test("Menu preparation source URL allowlist admits pinned files and only two exact main endpoints", () => {
                string valid = ThirdPartyScriptCatalog.RawUrl(Commit, "menu.sh");
                True(ThirdPartyScriptCatalog.IsSourceUrl(valid));
                True(ThirdPartyScriptCatalog.IsSourceUrl(ThirdPartyScriptCatalog.MainMenuUrl));
                True(ThirdPartyScriptCatalog.IsSourceUrl(ThirdPartyScriptCatalog.MainMenuLicenseUrl));
                foreach (string bad in new[] { valid + "?x=1", valid + "#fragment", valid + "\n", valid + "/",
                    valid.Replace(Commit, Commit.ToUpperInvariant()), valid.Replace("https://", "http://"),
                    valid.Replace("raw.githubusercontent.com/", "raw.githubusercontent.com.evil.test/"),
                    valid.Replace(ThirdPartyScriptCatalog.Project, "other/repository"), valid.Replace("/menu.sh", "/%6denu.sh"),
                    valid.Replace("/menu.sh", "/../menu.sh"), valid.Replace("/menu.sh", "/dir/menu.sh"),
                    valid.Replace("/menu.sh", "/helper-launcher.sh"), valid.Replace("/menu.sh", "/menu.pid") })
                    Equal(false, ThirdPartyScriptCatalog.IsSourceUrl(bad));
                foreach (string main in new[] { ThirdPartyScriptCatalog.MainMenuUrl, ThirdPartyScriptCatalog.MainMenuLicenseUrl })
                    foreach (string bad in new[] { main + "?token=secret", main + "#fragment", main + "\n", main + "/", main.Replace("/main/", "/Main/"),
                        main.Replace("https://", "http://"), main.Replace("raw.githubusercontent.com/", "user:password@raw.githubusercontent.com/"),
                        main.Replace("raw.githubusercontent.com/", "raw.githubusercontent.com:443/"), main.Replace("raw.githubusercontent.com/", "raw.githubusercontent.com.evil.test/"),
                        main.Replace(ThirdPartyScriptCatalog.Project, "other/repository"), main.Replace("/main/", "/main/../main/") })
                        Equal(false, ThirdPartyScriptCatalog.IsSourceUrl(bad));
                foreach (string file in new[] { "install.sh", "passwall.sh", "passwall2.sh", "nikki.sh", "smartdns.sh", "mosdns.sh", "daed.sh", "check-updates.sh", "uninstall.sh", "helper-launcher.sh", "menu.pid", "%6denu.sh" })
                    Equal(false, ThirdPartyScriptCatalog.IsSourceUrl("https://raw.githubusercontent.com/" + ThirdPartyScriptCatalog.Project + "/main/" + file));
                foreach (string bad in new[] { "../menu.sh", "menu.sh;id", "/menu.sh", "Menu.sh", "menu.sh\n", "menu.sh\0", "helper-launcher.sh", "menu.pid", ".owner" })
                    Throws<ArgumentException>(() => ThirdPartyScriptCatalog.RawUrl(Commit, bad));
            });
            test("Menu preparation rejects invalid URLs before either HTTP adapter can send", () => Async(async () => {
                var http = new Http(); var source = new ThirdPartyScriptSource(http);
                foreach (string bad in new[] { "https://evil.test/menu.sh", ThirdPartyScriptCatalog.RawUrl(Commit, "menu.sh") + "?redirect=1",
                    ThirdPartyScriptCatalog.MainMenuUrl + "?redirect=1", ThirdPartyScriptCatalog.MainMenuLicenseUrl + "#token",
                    ThirdPartyScriptCatalog.MainMenuUrl.Replace("/menu.sh", "/install.sh"),
                    ThirdPartyScriptCatalog.MainMenuUrl.Replace("raw.githubusercontent.com/", "user:password@raw.githubusercontent.com/") })
                {
                    await ThrowsAsync<ArgumentException>(() => source.DownloadAsync(bad, 1024, CancellationToken.None));
                    await ThrowsAsync<ArgumentException>(() => new WindowsThirdPartyHttpClient().GetAsync(bad, 1024, CancellationToken.None));
                }
                Equal(0, http.Urls.Count);
            }));
            test("Menu preparation workspace separates payload reserved launcher and runtime state", () => {
                var id = Guid.NewGuid(); var workspace = RemoteMaintenanceWorkspace.CreateMenu(id);
                Equal(RemoteMaintenanceWorkspace.BaseDirectory + "/maintenance-" + id.ToString("N"), workspace.Directory);
                Equal("menu.sh|LICENSE|NOTICE.txt", string.Join("|", workspace.Files));
                Equal("menu.pid|menu.start|menu.exit|menu.exit.tmp", string.Join("|", workspace.RuntimeFiles));
                Equal(8, workspace.OwnedFiles.Length);
                foreach (string state in workspace.RuntimeFiles)
                {
                    Equal(workspace.Directory + "/" + state, workspace.OwnedFilePath(state));
                    Throws<ArgumentException>(() => workspace.FilePath(state));
                    Throws<ArgumentException>(() => workspace.HashCommand(state));
                }
                Equal(workspace.Directory + "/helper-launcher.sh", workspace.OwnedFilePath(RemoteMaintenanceWorkspace.MenuLauncherFile));
                Throws<ArgumentException>(() => workspace.FilePath(RemoteMaintenanceWorkspace.MenuLauncherFile));
                var mutable = workspace.Files; mutable[0] = "../outside";
                Equal("menu.sh", workspace.Files[0]);
                mutable = workspace.OwnedFiles; mutable[0] = "../outside";
                Equal("menu.sh", workspace.OwnedFiles[0]);
            });
            test("Menu preparation launcher is explicit local payload and never an upstream endpoint", () => {
                var workspace = RemoteMaintenanceWorkspace.CreateMenu(Guid.NewGuid(), true);
                Equal(4, workspace.Files.Length);
                Equal(workspace.Directory + "/helper-launcher.sh", workspace.FilePath(RemoteMaintenanceWorkspace.MenuLauncherFile));
                True(workspace.HashCommand(RemoteMaintenanceWorkspace.MenuLauncherFile).Contains("sha256sum"));
                Throws<ArgumentException>(() => ThirdPartyScriptCatalog.RawUrl(Commit, RemoteMaintenanceWorkspace.MenuLauncherFile));
                Throws<ArgumentException>(() => RemoteMaintenanceWorkspace.CreateMenu(Guid.Empty));
            });
            test("Menu preparation paths reject traversal owner access and command injection", () => {
                var workspace = RemoteMaintenanceWorkspace.CreateMenu(Guid.NewGuid());
                foreach (string bad in new[] { null, "", "..", "../LICENSE", "sub/menu.sh", "/etc/hosts", ".owner", "menu.sh;id", "menu.sh\n", "MENU.SH", "menu.pid/../menu.sh" })
                {
                    Throws<ArgumentException>(() => workspace.FilePath(bad));
                    Throws<ArgumentException>(() => workspace.OwnedFilePath(bad));
                }
                string cleanup = workspace.CleanupCommand();
                True(cleanup.Contains("for f in ")); True(cleanup.Contains("case \"$f\" in "));
                True(cleanup.Contains("test ! -L")); Equal(false, cleanup.Contains("rm -rf"));
                Equal(false, cleanup.Contains("menu.pid")); // Reserved runtime files are not deletion targets.
                Equal(false, cleanup.Contains("helper-launcher.sh"));
                foreach (string command in new[] { workspace.CreateCommand(), workspace.HashCommand("menu.sh"), cleanup })
                    foreach (string bad in new[] { "apk add", "opkg install", "chmod", "reboot", "curl ", "wget ", "exec sh", "/etc/" })
                        Equal(false, command.Contains(bad));
            });
            test("Menu preparation downloads main directly and uploads original bytes plus honest provenance", () => Async(async () => {
                var http = new Http(); var remote = new Remote();
                var result = await Service(http).PrepareMenuAsync(remote, null, CancellationToken.None);
                Prepared(result, remote);
                Equal(2, http.Urls.Count); Equal(0, http.Urls.Count(u => u == ComputerProxyPolicy.ApiUrl));
                Equal(ThirdPartyScriptCatalog.MainMenuUrl, http.Urls[0]); Equal(ThirdPartyScriptCatalog.MainMenuLicenseUrl, http.Urls[1]);
                Equal(1048576, http.Limits[0]); Equal(65536, http.Limits[1]);
                Equal("main", result.Descriptor.SourceRef); Equal<string>(null, result.Descriptor.Commit);
                True(remote.Uploaded["menu.sh"].SequenceEqual(Menu));
                True(remote.Uploaded["LICENSE"].SequenceEqual(http.LicenseBytes));
                string notice = Encoding.UTF8.GetString(remote.Uploaded["NOTICE.txt"]);
                True(notice.Contains("Ref: main")); True(notice.Contains("Commit: not resolved (direct branch download)"));
                True(notice.Contains(ThirdPartyScriptCatalog.MainMenuUrl)); True(notice.Contains(ThirdPartyScriptCatalog.MainMenuLicenseUrl));
                True(notice.Contains("Script: menu.sh")); True(notice.Contains("MIT License"));
                Equal(false, result.Logs.Any(l => l.Stage == MaintenancePreparationStage.Commit));
                Equal(Hash(Menu), result.ScriptSha256); Equal(Hash(http.LicenseBytes), result.LicenseSha256);
                True(result.Logs.All(l => l.Origin == MaintenanceLogOrigin.PluginHelper));
                True(result.Logs.Any(l => l.Message.Contains("Prepared menu payload (NOT executed)")));
            }));
            test("Menu preparation explicit SHA skips branch lookup and normalizes case", () => Async(async () => {
                var http = new Http(); var remote = new Remote();
                var result = await Service(http).PrepareMenuAtCommitAsync(remote, Commit.ToUpperInvariant(), null, CancellationToken.None);
                Prepared(result, remote); Equal(Commit, result.Descriptor.Commit);
                Equal(2, http.Urls.Count); True(http.Urls.All(u => u.Contains("/" + Commit + "/")));
            }));
            test("Menu preparation moving main records the actual second response without claiming one commit", () => Async(async () => {
                var http = new Http(); var remote = new Remote();
                http.AfterRequest = url => { if (url == ThirdPartyScriptCatalog.MainMenuUrl) http.LicenseBytes = http.LicenseBytes.Concat(Encoding.UTF8.GetBytes("\nbranch revision fixture\n")).ToArray(); };
                var result = await Service(http).PrepareMenuAsync(remote, null, CancellationToken.None);
                Prepared(result, remote); Equal<string>(null, result.Descriptor.Commit);
                Equal(Hash(http.LicenseBytes), result.LicenseSha256); True(http.LicenseBytes.SequenceEqual(remote.Uploaded["LICENSE"]));
                True(http.Urls.All(u => u.Contains("/main/")));
            }));
            test("Menu preparation each new task fetches fresh main bytes and owns a unique directory", () => Async(async () => {
                var http = new Http(); var remote = new Remote(); var service = Service(http);
                var first = await service.PrepareMenuAsync(remote, null, CancellationToken.None);
                http.MenuBytes = Encoding.UTF8.GetBytes("#!/bin/sh\nprintf 'new branch fixture\\n'\n");
                var second = await service.PrepareMenuAsync(remote, null, CancellationToken.None);
                Equal<string>(null, first.Descriptor.Commit); Equal<string>(null, second.Descriptor.Commit);
                Equal(Hash(Menu), first.ScriptSha256); Equal(Hash(http.MenuBytes), second.ScriptSha256); True(first.ScriptSha256 != second.ScriptSha256);
                True(first.OperationId != second.OperationId && first.RemoteDirectory != second.RemoteDirectory);
                Equal(4, http.Urls.Count); Equal(0, http.Urls.Count(u => u == ComputerProxyPolicy.ApiUrl));
                True(service.LogBuffer.Snapshot().All(l => l.OperationId == second.OperationId));
            }));
            test("Menu preparation disconnected session and pre-cancellation cause no downloads or files", () => Async(async () => {
                var http = new Http(); var remote = new Remote { IsConnected = false }; var service = Service(http);
                Equal(MaintenancePreparationStatus.Rejected, (await service.PrepareMenuAsync(remote, null, CancellationToken.None)).Status);
                using (var cancel = new CancellationTokenSource())
                {
                    cancel.Cancel();
                    Equal(MaintenancePreparationStatus.Cancelled, (await service.PrepareMenuAsync(remote, null, cancel.Token)).Status);
                }
                Equal(0, http.Urls.Count); Equal(0, remote.Creates); Equal(0, remote.Cleanups);
            }));
            test("Menu preparation succeeds without touching an unavailable commit resolver", () => Async(async () => {
                var source = new NoCommitSource(); var remote = new Remote();
                var result = await new PluginMaintenanceExecutionService(source).PrepareMenuAsync(remote, null, CancellationToken.None);
                Prepared(result, remote); Equal(2, source.Downloads);
            }));
            foreach (int code in new[] { 301, 302, 403, 404, 429, 500 })
            {
                int status = code;
                test("Menu preparation HTTP " + code + " never falls back or creates workspace", () => Async(async () => {
                    var http = new Http { MenuStatus = status }; var remote = new Remote();
                    var result = await Service(http).PrepareMenuAsync(remote, null, CancellationToken.None);
                    Equal(MaintenancePreparationStatus.Failed, result.Status); Equal(MaintenancePreparationStage.Download, result.Stage);
                    Equal(1, http.Urls.Count); Equal(ThirdPartyScriptCatalog.MainMenuUrl, http.Urls[0]); Equal(0, remote.Creates);
                    True(result.Error.Contains(status.ToString())); Equal<string>(null, result.LocalDirectory);
                }));
            }
            foreach (string failure in new[] { "empty-menu", "oversize-menu", "empty-license", "oversize-license", "license-http", "null-response", "transport" })
            {
                string kind = failure;
                test("Menu preparation download boundary " + kind, () => Async(async () => {
                    var http = new Http(); var remote = new Remote();
                    if (kind == "empty-menu") http.MenuBytes = new byte[0];
                    if (kind == "oversize-menu") http.MenuBytes = new byte[1048577];
                    if (kind == "empty-license") http.LicenseBytes = new byte[0];
                    if (kind == "oversize-license") http.LicenseBytes = new byte[65537];
                    if (kind == "license-http") http.LicenseStatus = 404;
                    if (kind == "null-response") http.NullResponse = true;
                    if (kind == "transport") http.ThrowDownload = true;
                    var result = await Service(http).PrepareMenuAtCommitAsync(remote, Commit, null, CancellationToken.None);
                    Equal(MaintenancePreparationStatus.Failed, result.Status); Equal(MaintenancePreparationStage.Download, result.Stage);
                    Equal(0, remote.Creates); Equal<string>(null, result.LocalDirectory);
                }));
            }
            foreach (byte[] invalid in new[] { Encoding.UTF8.GetBytes("<html>login</html>"), Encoding.UTF8.GetBytes("#!/bin/sh\n\0"),
                Encoding.UTF8.GetBytes("\ufeff#!/bin/sh\n"), new byte[] { 255, 254 }, Encoding.UTF8.GetBytes("#!/bin/bash\n") })
            {
                byte[] bytes = invalid;
                test("Menu preparation invalid menu content " + Hash(bytes).Substring(0, 8), () => Async(async () => {
                    var remote = new Remote(); var result = await Service(new Http { MenuBytes = bytes }).PrepareMenuAtCommitAsync(remote, Commit, null, CancellationToken.None);
                    Equal(MaintenancePreparationStatus.Failed, result.Status); Equal(MaintenancePreparationStage.Validate, result.Stage);
                    Equal(0, remote.Creates); Equal<string>(null, result.LocalDirectory);
                }));
            }
            test("Menu preparation size limit accepts exactly one MiB without rewriting", () => Async(async () => {
                byte[] prefix = Encoding.UTF8.GetBytes("#!/bin/sh\n#");
                byte[] bytes = prefix.Concat(Enumerable.Repeat((byte)'x', 1048576 - prefix.Length)).ToArray();
                var remote = new Remote(); var result = await Service(new Http { MenuBytes = bytes }).PrepareMenuAtCommitAsync(remote, Commit, null, CancellationToken.None);
                Prepared(result, remote); True(bytes.SequenceEqual(remote.Uploaded["menu.sh"])); Equal(Hash(bytes), result.ScriptSha256);
            }));
            test("Menu preparation invalid LICENSE never falls back to bundled license", () => Async(async () => {
                var remote = new Remote(); var result = await Service(new Http { LicenseBytes = Encoding.UTF8.GetBytes("MIT License wrong author") }).PrepareMenuAsync(remote, null, CancellationToken.None);
                Equal(MaintenancePreparationStatus.Failed, result.Status); Equal(MaintenancePreparationStage.Validate, result.Stage);
                Equal(0, remote.Creates);
            }));
            foreach (string failure in new[] { "create", "upload", "hash", "wrongpath", "extra-line", "missing-hash", "disconnect" })
            {
                string kind = failure;
                test("Menu preparation remote failure " + kind + " cannot produce executable plan", () => Async(async () => {
                    var remote = new Remote { Fault = kind }; var result = await Service(new Http()).PrepareMenuAsync(remote, null, CancellationToken.None);
                    Equal(MaintenancePreparationStatus.Failed, result.Status); Equal(false, result.IntegrityVerified);
                    Equal(false, result.ExecutionAttempted);
                    Equal(1, remote.Cleanups); True(result.LocalCleanupSucceeded); True(!Directory.Exists(result.LocalDirectory));
                    if (kind == "disconnect") { Equal(false, result.RemoteCleanupSucceeded); True(result.CleanupWarning.Contains(result.RemoteDirectory)); }
                    else True(result.RemoteCleanupSucceeded);
                }));
            }
            foreach (string file in new[] { "menu.sh", "LICENSE", "NOTICE.txt" })
            {
                string name = file;
                test("Menu preparation SHA mismatch for " + name + " refuses integrity", () => Async(async () => {
                    var remote = new Remote { Fault = "mismatch", FaultFile = name };
                    var result = await Service(new Http()).PrepareMenuAsync(remote, null, CancellationToken.None);
                    Equal(MaintenancePreparationStatus.Failed, result.Status); Equal(MaintenancePreparationStage.RemoteHash, result.Stage);
                    Equal(false, result.IntegrityVerified); True(result.Error.Contains(name));
                    Equal(0, remote.Files.Count); True(result.LocalCleanupSucceeded);
                }));
            }
            test("Menu preparation cancellation after partial upload uses independent cleanup token", () => Async(async () => {
                using (var cancel = new CancellationTokenSource())
                {
                    var remote = new Remote(); remote.UploadHook = path => cancel.Cancel();
                    var result = await Service(new Http()).PrepareMenuAsync(remote, null, cancel.Token);
                    Equal(MaintenancePreparationStatus.Cancelled, result.Status); Equal(1, remote.Uploads);
                    Equal(1, remote.Cleanups); Equal(false, remote.CleanupTokenCancelled); Equal(0, remote.Files.Count);
                    True(result.LocalCleanupSucceeded && result.RemoteCleanupSucceeded);
                }
            }));
            test("Menu preparation cancellation during download creates no task files", () => Async(async () => {
                using (var cancel = new CancellationTokenSource())
                {
                    var http = new Http(); var remote = new Remote();
                    http.AfterRequest = url => cancel.Cancel();
                    var result = await Service(http).PrepareMenuAtCommitAsync(remote, Commit, null, cancel.Token);
                    Equal(MaintenancePreparationStatus.Cancelled, result.Status); Equal(0, remote.Creates); Equal(0, remote.Cleanups);
                }
            }));
            test("Menu preparation cleanup failure remains explicit even after successful hashes", () => Async(async () => {
                var remote = new Remote { CleanupFails = true };
                var result = await Service(new Http()).PrepareMenuAsync(remote, null, CancellationToken.None);
                True(result.IntegrityVerified); Equal(MaintenancePreparationStatus.Failed, result.Status);
                Equal(MaintenancePreparationStage.Cleanup, result.Stage); Equal(false, result.RemoteCleanupSucceeded);
                True(result.CleanupWarning.Contains(result.RemoteDirectory)); True(result.LocalCleanupSucceeded);
            }));
            test("Menu preparation local cleanup preserves unknown file and reports its failure", () => Async(async () => {
                string extra = null;
                var remote = new Remote();
                remote.UploadHook = path => { if (extra == null) { extra = Path.Combine(Path.GetDirectoryName(path), "keep.txt"); File.WriteAllText(extra, "keep"); } };
                MaintenancePreparationResult result = null;
                try
                {
                    result = await Service(new Http()).PrepareMenuAsync(remote, null, CancellationToken.None);
                    Equal(MaintenancePreparationStatus.Failed, result.Status); Equal(false, result.LocalCleanupSucceeded);
                    True(result.RemoteCleanupSucceeded); Equal("keep", File.ReadAllText(extra));
                }
                finally
                {
                    if (extra != null && File.Exists(extra)) File.Delete(extra);
                    if (result != null && Directory.Exists(result.LocalDirectory)) Directory.Delete(result.LocalDirectory, false);
                }
            }));
            test("Menu preparation gate covers cleanup and excludes concurrent pinned menu tasks", () => Async(async () => {
                var http = new Http(); var service = Service(http); var remote = new Remote();
                var entered = new TaskCompletionSource<bool>(); var release = new TaskCompletionSource<bool>();
                remote.CleanupHook = async () => { entered.TrySetResult(true); await release.Task; };
                var first = service.PrepareMenuAsync(remote, null, CancellationToken.None);
                try
                {
                    await Deadline(entered.Task);
                    Equal(MaintenancePreparationStatus.Busy, (await service.PrepareMenuAsync(new Remote(), null, CancellationToken.None)).Status);
                    Equal(MaintenancePreparationStatus.Busy, (await service.PrepareMenuAtCommitAsync(new Remote(), Commit, null, CancellationToken.None)).Status);
                    Equal(2, http.Urls.Count);
                }
                finally { release.TrySetResult(true); }
                await Deadline(first);
                remote.CleanupHook = null;
                Equal(MaintenancePreparationStatus.PreparedExecutionDisabled, (await service.PrepareMenuAtCommitAsync(remote, Commit, null, CancellationToken.None)).Status);
            }));
            test("Menu preparation disposed observer cannot abort validation or cleanup", () => Async(async () => {
                var remote = new Remote();
                var result = await Service(new Http()).PrepareMenuAsync(remote, new ThrowingProgress(), CancellationToken.None);
                Prepared(result, remote); True(result.Logs.Count > 0);
                True(typeof(IMaintenancePreparationSession).GetMethods().All(m => !m.Name.Contains("Execute") && !m.Name.Contains("Run")));
            }));
        }

        // Explicit separate mode: requires local Git Bash, never a product runtime dependency.
        public static void RunShellBoundaries(Action<string, Action> test)
        {
            ShellCase(test, "dangling payload symlink cannot bypass pre-cleanup checks", (w, b) => {
                string payload = Rebase(w.FilePath("menu.sh"), b);
                return CreateAndFill(w, b) + "rm -f -- " + Quote(payload) + "\nln -s " + Quote(b + "-missing") + " " + Quote(payload) +
                    "\ntest -L " + Quote(payload) + "\ntest ! -e " + Quote(payload) + "\n" +
                    Refuse(Rebase(w.HashCommand("menu.sh"), b)) + Refuse(Rebase(w.CleanupCommand(), b)) +
                    "test -f " + Quote(Rebase(w.Directory, b) + "/.owner") + "\ntest -f " + Quote(Rebase(w.FilePath("LICENSE"), b)) + "\n";
            });
            ShellCase(test, "create hash cleanup and repeated cleanup", (w, b) =>
                CreateAndFill(w, b) + Rebase(w.HashCommand("menu.sh"), b) + "\n" +
                Rebase(w.CleanupCommand(), b) + "\n" + Rebase(w.CleanupCommand(), b) + "\n" +
                "test ! -e " + Quote(Rebase(w.Directory, b)) + "\n", output => {
                    True(output.StartsWith("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad ", StringComparison.Ordinal));
                });
            ShellCase(test, "wrong owner refuses hash and preserves all payloads", (w, b) =>
                CreateAndFill(w, b) + "printf other > " + Quote(Rebase(w.Directory, b) + "/.owner") + "\n" +
                Refuse(Rebase(w.HashCommand("menu.sh"), b)) + Refuse(Rebase(w.CleanupCommand(), b)) + Preserve(w, b));
            ShellCase(test, "base symlink refuses create and cleanup", (w, b) => {
                string outside = b + "-outside";
                return "mkdir -p " + Quote(outside) + "\nprintf sentinel > " + Quote(outside + "/keep") + "\nln -s " + Quote(outside) + " " + Quote(b) + "\ntest -L " + Quote(b) + "\n" +
                    Refuse(Rebase(w.CreateCommand(), b)) + Refuse(Rebase(w.CleanupCommand(), b)) + "test \"$(cat " + Quote(outside + "/keep") + ")\" = sentinel\n";
            });
            ShellCase(test, "task directory symlink refuses hash and cleanup", (w, b) => {
                string outside = b + "-outside";
                return "mkdir -p " + Quote(b) + " " + Quote(outside) + "\nprintf sentinel > " + Quote(outside + "/menu.sh") + "\nprintf '%s' " + Quote(w.OperationId.ToString("N")) + " > " + Quote(outside + "/.owner") +
                    "\nln -s " + Quote(outside) + " " + Quote(Rebase(w.Directory, b)) + "\ntest -L " + Quote(Rebase(w.Directory, b)) + "\n" +
                    Refuse(Rebase(w.HashCommand("menu.sh"), b)) + Refuse(Rebase(w.CleanupCommand(), b)) + "test \"$(cat " + Quote(outside + "/menu.sh") + ")\" = sentinel\n";
            });
            ShellCase(test, "owner symlink cannot authorize hash or cleanup", (w, b) => {
                string owner = Rebase(w.Directory, b) + "/.owner", outside = b + "-owner";
                return CreateAndFill(w, b) + "printf '%s' " + Quote(w.OperationId.ToString("N")) + " > " + Quote(outside) + "\nrm -f -- " + Quote(owner) + "\nln -s " + Quote(outside) + " " + Quote(owner) +
                    "\ntest -L " + Quote(owner) + "\n" + Refuse(Rebase(w.HashCommand("menu.sh"), b)) + Refuse(Rebase(w.CleanupCommand(), b)) +
                    "test -f " + Quote(Rebase(w.FilePath("menu.sh"), b)) + "\ntest -f " + Quote(outside) + "\n";
            });
            foreach (string file in new[] { "menu.sh", "LICENSE", "NOTICE.txt" })
            {
                string name = file;
                ShellCase(test, "payload symlink " + name + " preserves owner and target", (w, b) => {
                    string payload = Rebase(w.FilePath(name), b), outside = b + "-target";
                    return CreateAndFill(w, b) + "printf sentinel > " + Quote(outside) + "\nrm -f -- " + Quote(payload) + "\nln -s " + Quote(outside) + " " + Quote(payload) +
                        "\ntest -L " + Quote(payload) + "\n" + Refuse(Rebase(w.HashCommand(name), b)) + Refuse(Rebase(w.CleanupCommand(), b)) +
                        "test \"$(cat " + Quote(outside) + ")\" = sentinel\ntest -f " + Quote(Rebase(w.Directory, b) + "/.owner") + "\n";
                });
            }
            foreach (string file in new[] { "keep.txt", ".keep", "..keep", "helper-launcher.sh", "menu.pid", "menu.start", "menu.exit", "menu.exit.tmp" })
            {
                string name = file;
                ShellCase(test, "unprepared or runtime file " + name + " blocks every deletion", (w, b) =>
                    CreateAndFill(w, b) + "printf sentinel > " + Quote(Rebase(w.Directory, b) + "/" + name) + "\n" +
                    Refuse(Rebase(w.CleanupCommand(), b)) + Preserve(w, b) +
                    "test \"$(cat " + Quote(Rebase(w.Directory, b) + "/" + name) + ")\" = sentinel\n");
            }
            ShellCase(test, "unexpected directory blocks deletion before payload removal", (w, b) =>
                CreateAndFill(w, b) + "mkdir " + Quote(Rebase(w.Directory, b) + "/keep-dir") + "\n" +
                Refuse(Rebase(w.CleanupCommand(), b)) + Preserve(w, b) + "test -d " + Quote(Rebase(w.Directory, b) + "/keep-dir") + "\n");
            ShellCase(test, "neighbor task remains untouched", (w, b) => {
                string neighbor = b + "/maintenance-" + new string('b', 32);
                return CreateAndFill(w, b) + "mkdir " + Quote(neighbor) + "\nprintf sentinel > " + Quote(neighbor + "/.owner") + "\n" +
                    Rebase(w.CleanupCommand(), b) + "\ntest ! -e " + Quote(Rebase(w.Directory, b)) + "\ntest \"$(cat " + Quote(neighbor + "/.owner") + ")\" = sentinel\n";
            });
            ShellCase(test, "explicit launcher payload hashes and cleans with menu files", (w, b) =>
                CreateAndFill(w, b) + Rebase(w.HashCommand(RemoteMaintenanceWorkspace.MenuLauncherFile), b) + "\n" +
                Rebase(w.CleanupCommand(), b) + "\ntest ! -e " + Quote(Rebase(w.Directory, b)) + "\n", null, true);
        }

        private static void ShellCase(Action<string, Action> test, string name, Func<RemoteMaintenanceWorkspace, string, string> build,
            Action<string> verify = null, bool launcher = false)
        {
            test("Menu shell boundary " + name, () => {
                string repository = FindRepository();
                string caseRoot = Path.GetFullPath(Path.Combine(repository, "artifacts", "menu-preparation-phase2", "shell-fixtures", Guid.NewGuid().ToString("N")));
                string allowed = Path.GetFullPath(Path.Combine(repository, "artifacts", "menu-preparation-phase2", "shell-fixtures")) + Path.DirectorySeparatorChar;
                if (!caseRoot.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture path escaped workspace.");
                Directory.CreateDirectory(caseRoot);
                string localBase = Path.Combine(caseRoot, "remote").Replace('\\', '/');
                var workspace = RemoteMaintenanceWorkspace.CreateMenu(Guid.NewGuid(), launcher);
                string body = "#!/bin/sh\nset -eu\n" + build(workspace, localBase);
                if (body.Contains(RemoteMaintenanceWorkspace.BaseDirectory)) throw new InvalidOperationException("Unrebased router path in local fixture.");
                string script = Path.Combine(caseRoot, "case.sh");
                File.WriteAllText(script, body, new UTF8Encoding(false));
                string bash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
                if (!File.Exists(bash)) throw new FileNotFoundException("Local shell boundary tests require Git Bash.", bash);
                var start = new ProcessStartInfo(bash, "--noprofile --norc \"" + script.Replace('\\', '/') + "\"")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = repository, RedirectStandardOutput = true, RedirectStandardError = true };
                // MSYS otherwise copies link targets on Windows; tests must exercise actual symlinks.
                start.EnvironmentVariables["MSYS"] = "winsymlinks:nativestrict";
                using (var process = Process.Start(start))
                {
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Local fixture shell timed out."); }
                    string output = stdout.GetAwaiter().GetResult(), error = stderr.GetAwaiter().GetResult();
                    File.WriteAllText(Path.Combine(caseRoot, "stdout.txt"), output, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(caseRoot, "stderr.txt"), error, new UTF8Encoding(false));
                    if (process.ExitCode != 0) throw new Exception("Local shell exit " + process.ExitCode + ": " + error);
                    if (verify != null) verify(output);
                }
            });
        }
        private static string CreateAndFill(RemoteMaintenanceWorkspace workspace, string localBase)
        {
            return Rebase(workspace.CreateCommand(), localBase) + "\n" +
                string.Join("", workspace.Files.Select(file => "printf abc > " + Quote(Rebase(workspace.FilePath(file), localBase)) + "\n"));
        }
        private static string Preserve(RemoteMaintenanceWorkspace workspace, string localBase)
        {
            return "test -f " + Quote(Rebase(workspace.Directory, localBase) + "/.owner") + "\n" +
                string.Join("", workspace.Files.Select(file => "test \"$(cat " + Quote(Rebase(workspace.FilePath(file), localBase)) + ")\" = abc\n"));
        }
        private static string Refuse(string command) { return "if (\n" + command + "\n); then printf 'unsafe command accepted\\n' >&2; exit 97; fi\n"; }
        private static string Rebase(string value, string localBase) { return value.Replace(RemoteMaintenanceWorkspace.BaseDirectory, localBase); }
        private static string Quote(string value) { return ShellArgument.Quote(value); }
        private static string FindRepository()
        {
            for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "iStoreOS.PluginHelper.sln"))) return directory.FullName;
            throw new DirectoryNotFoundException("V1 repository root not found.");
        }
        private static PluginMaintenanceExecutionService Service(Http http) { return new PluginMaintenanceExecutionService(new ThirdPartyScriptSource(http)); }
        private static void Prepared(MaintenancePreparationResult result, Remote remote)
        {
            Equal(MaintenancePreparationStatus.PreparedExecutionDisabled, result.Status); Equal(MaintenancePreparationStage.Prepared, result.Stage);
            True(result.IntegrityVerified && result.LocalCleanupSucceeded && result.RemoteCleanupSucceeded);
            Equal(3, remote.Uploads); Equal(3, remote.Hashes); Equal(1, remote.Cleanups);
            Equal(0, remote.Files.Count); True(!Directory.Exists(result.LocalDirectory));
            Equal(false, result.ExecutionAttempted); Equal<int?>(null, result.ExitCode);
        }
        private sealed class Http : IThirdPartyHttpClient
        {
            public readonly List<string> Urls = new List<string>();
            public readonly List<int> Limits = new List<int>();
            public int MenuStatus = 200, LicenseStatus = 200;
            public byte[] MenuBytes = Menu, LicenseBytes = Encoding.UTF8.GetBytes(ThirdPartyScriptCatalog.BundledLicense());
            public bool NullResponse = false, ThrowDownload = false;
            public Action<string> AfterRequest = null;
            public Task<ThirdPartyHttpContent> GetAsync(string url, int maxBytes, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Urls.Add(url); Limits.Add(maxBytes);
                if (ThrowDownload) throw new IOException("fixture-secret HTTP failure");
                if (NullResponse) return Task.FromResult<ThirdPartyHttpContent>(null);
                ThirdPartyHttpContent response;
                if (url == ComputerProxyPolicy.ApiUrl) throw new IOException("Menu must not request the commit API.");
                else if (url.EndsWith("/LICENSE", StringComparison.Ordinal)) response = new ThirdPartyHttpContent { StatusCode = LicenseStatus, Bytes = LicenseBytes };
                else response = new ThirdPartyHttpContent { StatusCode = MenuStatus, Bytes = MenuBytes };
                if (AfterRequest != null) AfterRequest(url);
                return Task.FromResult(response);
            }
        }
        private sealed class Remote : IMaintenancePreparationSession
        {
            public bool IsConnected { get; set; }
            public string Fault = null, FaultFile = null;
            public bool CleanupFails = false, CleanupTokenCancelled = false;
            public int Creates, Uploads, Hashes, Cleanups;
            public RemoteMaintenanceWorkspace Workspace;
            public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public readonly Dictionary<string, byte[]> Uploaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public Action<string> UploadHook = null;
            public Func<Task> CleanupHook = null;
            public Remote() { IsConnected = true; }
            public string Sanitize(string value) { return (value ?? "").Replace("fixture-secret", "[hidden]"); }
            public Task CreateWorkspaceAsync(RemoteMaintenanceWorkspace workspace, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Creates++; Workspace = workspace; Uploaded.Clear();
                if (Fault == "create") throw new IOException("fixture create failed");
                return Task.FromResult(true);
            }
            public Task UploadAsync(string path, RemoteMaintenanceWorkspace workspace, string file, CancellationToken token)
            {
                Uploads++; byte[] bytes = File.ReadAllBytes(path); Files[workspace.FilePath(file)] = bytes; Uploaded[file] = bytes;
                if (UploadHook != null) UploadHook(path);
                token.ThrowIfCancellationRequested();
                if (Fault == "disconnect") { IsConnected = false; throw new IOException("fixture disconnected"); }
                if (Fault == "upload") throw new IOException("fixture partial SFTP failed");
                return Task.FromResult(true);
            }
            public Task<string> ReadHashAsync(RemoteMaintenanceWorkspace workspace, string file, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Hashes++;
                if (Fault == "hash") throw new IOException("fixture sha256sum failed");
                if (Fault == "missing-hash") return Task.FromResult("");
                string hash = Fault == "mismatch" && (FaultFile == null || FaultFile == file) ? new string('0', 64) : Hash(Files[workspace.FilePath(file)]);
                string path = Fault == "wrongpath" ? "/tmp/other" : workspace.FilePath(file);
                return Task.FromResult(hash + "  " + path + "\n" + (Fault == "extra-line" ? "unexpected\n" : ""));
            }
            public async Task<bool> CleanupAsync(RemoteMaintenanceWorkspace workspace, CancellationToken token)
            {
                Cleanups++; CleanupTokenCancelled = token.IsCancellationRequested;
                if (CleanupHook != null) await CleanupHook();
                if (!IsConnected) throw new IOException("fixture cleanup disconnected");
                if (CleanupFails) return false;
                Files.Clear(); return true;
            }
        }
        private sealed class NoCommitSource : IThirdPartyScriptSource
        {
            public int Downloads;
            public Task<byte[]> DownloadAsync(string url, int max, CancellationToken token)
            {
                Downloads++;
                if (url == ThirdPartyScriptCatalog.MainMenuUrl) return Task.FromResult(Menu);
                if (url == ThirdPartyScriptCatalog.MainMenuLicenseUrl) return Task.FromResult(Encoding.UTF8.GetBytes(ThirdPartyScriptCatalog.BundledLicense()));
                throw new InvalidOperationException("Unexpected endpoint.");
            }
        }
        private sealed class ThrowingProgress : IProgress<MaintenanceOperationLog>
        { public void Report(MaintenanceOperationLog value) { throw new ObjectDisposedException("fixture observer"); } }
        private static string Hash(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        private static void Async(Func<Task> action) { Task.Run(action).GetAwaiter().GetResult(); }
        private static async Task Deadline(Task task) { if (await Task.WhenAny(task, Task.Delay(5000)) != task) throw new TimeoutException("Fixture checkpoint timed out."); await task; }
        private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
        private static void True(bool value) { Equal(true, value); }
        private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    }
}

