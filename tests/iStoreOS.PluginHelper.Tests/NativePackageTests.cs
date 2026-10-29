using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class NativePackageTests
    {
        private const string Name = "luci-app-passwall", Version = "26.9.16-r1";
        public static void Run(Action<string, Action> test)
        {
            foreach (string format in new[] { "ipk", "ar", "v2", "signed-v2", "adb", "deflate", "adbc-none", "adbc-deflate", "zstd" })
            {
                string kind = format;
                test("Native metadata reads actual content " + kind, () => Fixture(kind, path =>
                {
                    var info = new PluginPackageAnalyzer().AnalyzeFile(path);
                    Equal(Name, info.PluginName); Equal(Version, info.PluginVersion); Equal("x86_64", info.Architecture);
                    Equal(4096L, info.InstalledBytes.Value); Equal(true, info.MetadataRead); Equal(null, info.SdkSeries);
                    Equal(kind == "ipk" || kind == "ar" ? LocalPackageFormat.Ipk : LocalPackageFormat.Apk, info.Format);
                }));
            }
            foreach (var format in new[] { LocalPackageFormat.Ipk, LocalPackageFormat.Apk })
            foreach (var manager in new[] { PackageManagerKind.APK, PackageManagerKind.OPKG, PackageManagerKind.Unknown })
            {
                var f = format; var m = manager;
                test("Native manager gate " + f + "/" + m, () =>
                {
                    var p = Plugin(f); var r = Router(m); new CompatibilityService().Evaluate(p, r);
                    Equal(m == PackageManagerKind.Unknown ? CompatibilityStatus.Unknown : (f == LocalPackageFormat.Ipk) == (m == PackageManagerKind.OPKG) ? CompatibilityStatus.Compatible : CompatibilityStatus.Incompatible, p.CompatibilityStatus);
                });
            }
            foreach (string architecture in new[] { "all", "noarch", "aarch64_cortex-a53", "aarch64_a53", "x86_64" })
            {
                string arch = architecture;
                test("Native exact architecture and neutral scopes " + arch, () =>
                {
                    foreach (var format in new[] { LocalPackageFormat.Ipk, LocalPackageFormat.Apk })
                    {
                        var p = Plugin(format); p.Architecture = arch; var r = Router(format == LocalPackageFormat.Ipk ? PackageManagerKind.OPKG : PackageManagerKind.APK); r.Architecture = "aarch64_generic";
                        new CompatibilityService().Evaluate(p, r);
                        Equal(arch == (format == LocalPackageFormat.Ipk ? "all" : "noarch") ? CompatibilityStatus.Compatible : CompatibilityStatus.Incompatible, p.CompatibilityStatus);
                    }
                });
            }
            test("Native unconnected and unverified metadata never compatible", () =>
            { var p = Plugin(LocalPackageFormat.Ipk); var r = Router(PackageManagerKind.OPKG); r.IsConnected = false; new CompatibilityService().Evaluate(p, r); Equal(CompatibilityStatus.Unknown, p.CompatibilityStatus); r.IsConnected = true; p.MetadataRead = false; new CompatibilityService().Evaluate(p, r); Equal(CompatibilityStatus.Unknown, p.CompatibilityStatus); });
            foreach (string metadata in new[] { "Package: x\nVersion: 1\n", "Package: x\nPackage: y\nVersion: 1\nArchitecture: all\n", "Package: x; touch pwn\nVersion: 1\nArchitecture: all\n", "Package: x\nVersion: 1\nArchitecture: all\nInstalled-Size: -1\n", "Package: x\nVersion: 1\nArchitecture: all\nInstalled-Size: 999999999999999999999999\n", "Package: x\nVersion: 1\nArchitecture: all\nVersion: 2\n" })
            {
                string text = metadata;
                test("Native invalid metadata rejected " + text.Replace('\n', '|'), () => BytesFixture(".ipk", Ipk(text), path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            }
            test("Native Android ZIP renamed APK rejected", () => BytesFixture(".apk", Encoding.ASCII.GetBytes("PK\u0003\u0004AndroidManifest.xml"), path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            test("Native renamed APK cannot masquerade as IPK", () => BytesFixture(".ipk", Adb("adb"), path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            test("Native truncated ADB rejected", () => BytesFixture(".apk", Adb("adb").Take(25).ToArray(), path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            test("Native ADB invalid object pointer rejected", () => { var bytes = Adb("adb"); Put(bytes, 16, 0xefffffff); BytesFixture(".apk", bytes, path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))); });
            test("Native ADB index schema rejected", () => { var bytes = Adb("adb"); Put(bytes, 4, 0x78646e69); BytesFixture(".apk", bytes, path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))); });
            test("Native unsupported compression fails closed", () => BytesFixture(".apk", new byte[] { 65, 68, 66, 99, 99, 3, 0, 0 }, path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            test("Native corrupt zstd fails closed", () => BytesFixture(".apk", new byte[] { 65, 68, 66, 99, 2, 3, 0, 0, 0, 0 }, path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            test("Native bad tar checksum rejected", () => { var bytes = Tar(new[] { Pair("debian-binary", Encoding.ASCII.GetBytes("2.0\n")) }); bytes[0] ^= 1; BytesFixture(".ipk", Gzip(bytes), path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))); });
            test("Native duplicate control entry rejected", () => BytesFixture(".ipk", Ipk(Control(), true), path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            test("Native archive symlink is never metadata", () => { var control = Tar(new[] { Pair("control", Encoding.UTF8.GetBytes(Control())) }, (byte)'2'); var bytes = Gzip(Tar(new[] { Pair("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), Pair("data.tar.gz", Gzip(new byte[1024])), Pair("control.tar.gz", Gzip(control)) })); BytesFixture(".ipk", bytes, path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))); });
            test("Native gzip metadata bomb bounded", () => BytesFixture(".apk", Gzip(new byte[1024 * 1024 + 1]), path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            test("Native no filename metadata guessing", () => { var p = new PluginPackageAnalyzer().AnalyzeFileName("luci-app-passwall_26.9.16-r1_x86_64.ipk"); Equal(false, p.MetadataRead); Equal(null, p.Architecture); });
            test("Native oversized package name rejected", () => BytesFixture(".ipk", Ipk(Control().Replace(Name, new string('x', 256))), path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            test("Native archive extension headers cannot override metadata paths", () => BytesFixture(".ipk", Gzip(Tar(new[] { Pair("control.tar.gz", new byte[10]) }, (byte)'L')), path => Throws(() => new PluginPackageAnalyzer().AnalyzeFile(path))));
            foreach (string kind in new[] { "ipk", "deflate", "zstd" })
            {
                string k = kind;
                Case(test, k + " full source SFTP hash snapshot verify cleanup", k, f => { }, (r, f) =>
                { Equal(InstallationState.Success, r.State); Equal(1, f.Starts); Equal(1, f.Uploads); Equal(1, f.UpdateCalls); Equal(1, f.Cleanups); Equal(Name, r.Installation.Verification.PackageName); Equal(Version, r.Installation.Verification.Version); Equal(1, r.Installation.Diff.Added.Count); Equal(true, r.SourceRetained); Equal(false, f.LastLaunch.Contains("--allow-untrusted")); Equal(false, f.LastLaunch.Contains("--force")); Equal(true, f.LastLaunch.Contains(k == "ipk" ? "opkg install" : "apk add")); });
                Case(test, k + " SHA mismatch never starts", k, f => f.HashMismatch = true, (r, f) => { Equal(0, f.Starts); Equal(1, f.Cleanups); Equal(InstallationFailureKind.Integrity, r.Failure.Kind); });
                Case(test, k + " absent SHA never starts", k, f => f.HashUnavailable = true, (r, f) => { Equal(0, f.Starts); Equal(1, f.Cleanups); Equal(InstallationFailureKind.Integrity, r.Failure.Kind); });
                Case(test, k + " persistent space gate before upload", k, f => f.Readiness.PersistentBytes = 1024 * 1024 * 32, (r, f) => { Equal(0, f.Starts); Equal(0, f.Uploads); Equal(InstallationFailureKind.NoSpace, r.Failure.Kind); });
                Case(test, k + " readiness rechecked immediately before launch", k, f => f.AfterStart = () => { }, (r, f) => { Equal(true, f.ReadinessReads >= 2); });
                Case(test, k + " tmp gate before upload", k, f => f.Readiness.TmpBytes = 1, (r, f) => { Equal(0, f.Uploads); Equal(InstallationFailureKind.NoSpace, r.Failure.Kind); });
                Case(test, k + " manager mismatch before source update", k, f => { if (k == "ipk") f.Manager = PackageManagerKind.APK; else f.MakeOpkg(); }, (r, f) => { Equal(0, f.Uploads); Equal(0, f.UpdateCalls); Equal(InstallationFailureKind.Compatibility, r.Failure.Kind); });
                Case(test, k + " busy prevents source or installation", k, f => f.Busy = true, (r, f) => { Equal(0, f.Starts); Equal(0, f.UpdateCalls); Equal(InstallationFailureKind.PackageManagerBusy, r.Failure.Kind); });
                Case(test, k + " lost dispatch reply resumes one runner", k, f => f.LostAcknowledgement = true, (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, f.Starts); Equal(1, f.Reconnects); });
                Case(test, k + " polling disconnect reconnects same runner", k, f => f.DisconnectPoll = true, (r, f) => { Equal(InstallationState.Success, r.State); Equal(1, f.Starts); Equal(1, f.Reconnects); });
                Case(test, k + " unknown status retains package", k, f => { f.NeverEnds = true; }, (r, f) => { Equal(true, r.PendingInstaller); Equal(1, f.Starts); Equal(0, f.Cleanups); });
                Case(test, k + " disappeared upload restores same native file", k, f => f.MissingBeforeDispatch = true, (r, f) => { Equal(InstallationState.Success, r.State); Equal(2, f.Uploads); Equal(1, f.Starts); });
                Case(test, k + " post-upload corruption blocks dispatch", k, f => f.CorruptBeforeDispatch = true, (r, f) => { Equal(0, f.Starts); Equal(InstallationFailureKind.Integrity, r.Failure.Kind); });
                Case(test, k + " signature or dependency failure does not rotate", k, f => f.Runs.Enqueue(new WorkflowTests.Outcome { Exit = 1, Log = "UNTRUSTED signature / cannot satisfy dependencies" }), (r, f) => { Equal(1, f.Starts); Equal(1, f.Cleanups); Equal(false, r.State == InstallationState.Success); Equal(0, f.Switches.Count); });
            }
            test("Native verification requires exact revision and independent presence", () =>
            {
                var p = Plugin(LocalPackageFormat.Apk); var after = new PackageSnapshot(); after.Packages[Name] = "26.9.16-r2";
                var v = new PluginVerificationService(); Equal(false, v.Verify(p, after, new PackageDiff(), n => true).Verified);
                after.Packages[Name] = Version; Equal(false, v.Verify(p, after, new PackageDiff(), n => false).Verified); Equal(true, v.Verify(p, after, new PackageDiff(), n => true).Verified);
            });
            test("Native package name is used even for a non LuCI package", () =>
            { var p = Plugin(LocalPackageFormat.Ipk); p.PluginName = "libexample42"; var after = new PackageSnapshot(); after.Packages[p.PluginName] = Version; Equal(true, new PluginVerificationService().Verify(p, after, new PackageDiff(), n => n == "libexample42").Verified); });
            test("Native cancellation before launch never dispatches and cleans", () => Fixture("ipk", path =>
            {
                var f = new WorkflowTests.Fake(); f.MakeOpkg(); using (var cancel = new CancellationTokenSource())
                {
                    var progress = new ProgressAction<InstallationWorkflowProgress>(value => { if (value.Transfer != null && value.Transfer.TotalBytes.HasValue) cancel.Cancel(); });
                    var r = Task.Run(() => new InstallationWorkflowService().RunAsync(f, Router(f.Manager), path, progress, cancel.Token)).GetAwaiter().GetResult();
                    Equal(0, f.Starts); Equal(1, f.Uploads); Equal(1, f.Cleanups); Equal(InstallationFailureKind.Cancelled, r.Failure.Kind);
                }
            }));
            test("Native lock binds metadata and bytes through confirmation", () => Fixture("deflate", path =>
            {
                var f = new WorkflowTests.Fake(); bool locked = false;
                var r = new PluginInstallationService().RunAsync(f, Router(f.Manager), path, () => Task.FromResult(new SoftwareEnvironmentResult { Status = SoftwareEnvironmentStatus.Ready }), summary =>
                { try { File.WriteAllText(path, "replace"); } catch (IOException) { locked = true; } Equal(Name, summary.Plugin.PluginName); return Task.FromResult(false); }, null, CancellationToken.None).GetAwaiter().GetResult();
                Equal(true, locked); Equal(InstallStatus.Cancelled, r.Status); Equal(0, f.Starts); Equal(1, f.Cleanups);
            }));
            test("Native unchanged existing version needs exact independent query", () => Fixture("deflate", path =>
            {
                var f = new WorkflowTests.Fake(); f.Packages.Packages[Name] = "26.9.16-r0";
                f.Runs.Enqueue(new WorkflowTests.Outcome { Exit = 1, Log = "signature invalid" });
                var r = new PluginInstallationService().RunAsync(f, Router(f.Manager), path, () => Task.FromResult(new SoftwareEnvironmentResult { Status = SoftwareEnvironmentStatus.Ready }), summary => Task.FromResult(true), null, CancellationToken.None).GetAwaiter().GetResult();
                Equal(false, r.Verification.Verified); Equal(InstallStatus.FailedInstaller, r.Status);
            }));
            test("Native UI selection dialog and drag accept three extensions", () =>
            {
                using (var form = new MainForm())
                {
                    var dialog = UiTests.Field<OpenFileDialog>(form, "fileDialog"); Equal(true, dialog.Filter.Contains("*.run;*.ipk;*.apk"));
                    Equal(true, UiTests.Control(form, "dropLabel").Text.Contains(".ipk / .apk"));
                    foreach (string extension in new[] { ".run", ".IPK", ".APK", ".exe" })
                    { var data = new DataObject(); data.SetData(DataFormats.FileDrop, new[] { "C:\\test" + extension }); var args = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None); UiTests.Invoke(form, "DropLabel_DragEnter", form, args); Equal(extension == ".exe" ? DragDropEffects.None : DragDropEffects.Copy, args.Effect); }
                }
            });
            test("Native UI shows content metadata gates mismatch and clears stale selection", () => Fixture("ipk", path =>
            {
                using (var form = new MainForm())
                {
                    UiTests.Set(form, "router", Router(PackageManagerKind.OPKG)); UiTests.Invoke(form, "SelectPlugin", path); Equal(Name, UiTests.Field<PluginPackageInfo>(form, "plugin").PluginName); Equal(true, UiTests.Control(form, "installButton").Enabled);
                    Equal(true, UiTests.Control(form, "pluginSummaryLabel").Text.Contains("IPK")); UiTests.Set(form, "router", Router(PackageManagerKind.APK)); UiTests.Invoke(form, "RefreshCompatibility"); Equal(false, UiTests.Control(form, "installButton").Enabled);
                    File.WriteAllText(path, "invalid"); UiTests.Invoke(form, "SelectPlugin", path); Equal(null, UiTests.Field<PluginPackageInfo>(form, "plugin")); Equal(false, UiTests.Control(form, "installButton").Enabled);
                }
            }));
            test("Native session filenames and runner guard", () =>
            {
                foreach (var format in new[] { LocalPackageFormat.Ipk, LocalPackageFormat.Apk })
                { var s = RemoteTempSession.Create(format); Equal(true, s.RunPath.EndsWith(format == LocalPackageFormat.Ipk ? ".ipk" : ".apk")); Throws(() => PluginInstallCommands.Inspect(s, true)); Equal(true, PluginInstallCommands.Hash(s).Contains(s.PackageFileName)); Equal(true, PluginInstallCommands.Launch(s).Contains("launch.once")); Equal(true, PluginInstallCommands.Launch(s).Contains("exit.tmp")); }
                Throws(() => RemoteTempSession.Create((LocalPackageFormat)99));
            });
            test("Native detection uses executable and active database", () => { string cmd = ReadOnlyRouterCommands.Get(RouterProbe.PackageManager); Equal(true, cmd.Contains("command -v apk")); Equal(true, cmd.Contains("command -v opkg")); Equal(true, cmd.Contains("/lib/apk/db/installed")); Equal(true, cmd.Contains("/usr/lib/opkg/status")); });
            foreach (var format in new[] { LocalPackageFormat.Ipk, LocalPackageFormat.Apk })
            { var f = format; test("Native actual shell dispatch captures args logs exit and launch once " + f, () => Shell(f)); }
        }
        private static void Case(Action<string, Action> test, string title, string kind, Action<WorkflowTests.Fake> setup, Action<InstallationWorkflowResult, WorkflowTests.Fake> check)
        {
            test("Native workflow " + title, () => Fixture(kind, path =>
            {
                var f = new WorkflowTests.Fake(); if (kind == "ipk") f.MakeOpkg(); setup(f);
                var service = new InstallationWorkflowService { BusyChecks = 2, BusyInterval = TimeSpan.FromMilliseconds(1), ReconnectInterval = TimeSpan.FromMilliseconds(1), Engine = new PluginInstallationService { PollInterval = TimeSpan.FromMilliseconds(1), OverallLimit = TimeSpan.FromMilliseconds(20) } };
                var r = Task.Run(() => service.RunAsync(f, Router(f.Manager), path, null, CancellationToken.None)).GetAwaiter().GetResult(); check(r, f);
            }));
        }
        private static PluginPackageInfo Plugin(LocalPackageFormat format) { return new PluginPackageInfo { Format = format, MetadataRead = true, PluginName = Name, PluginVersion = Version, Architecture = "x86_64" }; }
        private static RouterEnvironment Router(PackageManagerKind manager) { return new RouterEnvironment { IsConnected = true, Architecture = "x86_64", Version = "25.12.5", SdkSeries = "25.12", HostKeyFingerprint = "SHA256:fixture", PackageManager = manager }; }
        private static string Control() { return "Package: " + Name + "\nVersion: " + Version + "\nArchitecture: x86_64\nInstalled-Size: 4096\nDescription: fixture\n"; }
        private static void Fixture(string kind, Action<string> action) { BytesFixture(kind == "ipk" || kind == "ar" ? ".ipk" : ".apk", kind == "ipk" ? Ipk(Control()) : kind == "ar" ? Ar() : kind == "v2" || kind == "signed-v2" ? ApkV2(kind == "signed-v2") : Adb(kind), action); }
        private static void BytesFixture(string extension, byte[] bytes, Action<string> action)
        { string file = Path.Combine(Path.GetTempPath(), "filename-is-not-metadata-" + Guid.NewGuid().ToString("N") + extension); try { File.WriteAllBytes(file, bytes); action(file); } finally { File.Delete(file); } }
        private static byte[] Ipk(string control, bool duplicate = false)
        { var contents = new List<KeyValuePair<string, byte[]>> { Pair("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), Pair("data.tar.gz", Gzip(new byte[1024])), Pair("control.tar.gz", Gzip(Tar(new[] { Pair("control", Encoding.UTF8.GetBytes(control)) }))) }; if (duplicate) contents.Add(contents[2]); return Gzip(Tar(contents)); }
        private static byte[] Ar()
        {
            using (var output = new MemoryStream())
            {
                Write(output, Encoding.ASCII.GetBytes("!<arch>\n"));
                foreach (var entry in new[] { Pair("debian-binary", Encoding.ASCII.GetBytes("2.0\n")), Pair("control.tar.gz", Gzip(Tar(new[] { Pair("control", Encoding.UTF8.GetBytes(Control())) }))), Pair("data.tar.gz", Gzip(new byte[1024])) })
                { string header = (entry.Key + "/").PadRight(16) + "0".PadRight(12) + "0".PadRight(6) + "0".PadRight(6) + "100644".PadRight(8) + entry.Value.Length.ToString().PadRight(10) + "`\n"; Write(output, Encoding.ASCII.GetBytes(header)); Write(output, entry.Value); if (entry.Value.Length % 2 != 0) output.WriteByte(10); } return output.ToArray();
            }
        }
        private static byte[] ApkV2(bool signed)
        {
            var control = Gzip(Tar(new[] { Pair(".PKGINFO", Encoding.UTF8.GetBytes("pkgname = " + Name + "\npkgver = " + Version + "\narch = x86_64\nsize = 4096\n")) }));
            return (signed ? Gzip(Tar(new[] { Pair(".SIGN.RSA.test", new byte[64]) })) : new byte[0]).Concat(control).Concat(Gzip(new byte[1024])).ToArray();
        }
        private static byte[] Adb(string kind)
        {
            byte[] adb = new byte[256]; int blob = 112;
            Put(adb, 4, 0xe0000008); Put(adb, 8, 2); Put(adb, 12, 0xe0000010); Put(adb, 16, 13);
            foreach (var pair in new[] { new KeyValuePair<int, string>(1, Name), new KeyValuePair<int, string>(2, Version), new KeyValuePair<int, string>(5, "x86_64") })
            { byte[] s = Encoding.UTF8.GetBytes(pair.Value); Put(adb, 16 + pair.Key * 4, 0x80000000 | (uint)blob); adb[blob++] = (byte)s.Length; Buffer.BlockCopy(s, 0, adb, blob, s.Length); blob += s.Length; }
            Put(adb, 16 + 12 * 4, 0x10001000);
            byte[] raw = new byte[12 + adb.Length]; Put(raw, 0, 0x2e424441); Put(raw, 4, 0x676b6370); Put(raw, 8, (uint)(adb.Length + 4)); Buffer.BlockCopy(adb, 0, raw, 12, adb.Length);
            if (kind == "adb") return raw;
            if (kind == "adbc-none") return new byte[] { 65, 68, 66, 99, 0, 1 }.Concat(raw).ToArray();
            if (kind == "zstd")
                using (var output = new MemoryStream())
                { Write(output, new byte[] { 65, 68, 66, 99, 2, 3 }); using (var zstd = new ZstdSharp.CompressionStream(output)) Write(zstd, raw); return output.ToArray(); }
            using (var output = new MemoryStream())
            { Write(output, kind == "adbc-deflate" ? new byte[] { 65, 68, 66, 99, 1, 9 } : new byte[] { 65, 68, 66, 100 }); using (var deflate = new DeflateStream(output, CompressionMode.Compress, true)) Write(deflate, raw); return output.ToArray(); }
        }
        private static byte[] Tar(IEnumerable<KeyValuePair<string, byte[]>> entries, byte type = 48)
        {
            using (var output = new MemoryStream())
            {
                foreach (var entry in entries)
                {
                    byte[] header = new byte[512]; Buffer.BlockCopy(Encoding.ASCII.GetBytes("./" + entry.Key), 0, header, 0, entry.Key.Length + 2); header[156] = type;
                    byte[] size = Encoding.ASCII.GetBytes(Convert.ToString(entry.Value.Length, 8).PadLeft(11, '0')); Buffer.BlockCopy(size, 0, header, 124, size.Length); for (int i = 148; i < 156; i++) header[i] = 32;
                    byte[] sum = Encoding.ASCII.GetBytes(Convert.ToString(header.Sum(b => (int)b), 8).PadLeft(6, '0')); Buffer.BlockCopy(sum, 0, header, 148, sum.Length); header[154] = 0;
                    Write(output, header); Write(output, entry.Value); Write(output, new byte[(512 - entry.Value.Length % 512) % 512]);
                }
                Write(output, new byte[1024]); return output.ToArray();
            }
        }
        private static byte[] Gzip(byte[] value) { using (var output = new MemoryStream()) { using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) Write(gzip, value); return output.ToArray(); } }
        private static KeyValuePair<string, byte[]> Pair(string name, byte[] value) { return new KeyValuePair<string, byte[]>(name, value); }
        private static void Put(byte[] bytes, int offset, uint value) { Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4); }
        private static void Write(Stream stream, byte[] bytes) { stream.Write(bytes, 0, bytes.Length); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
        private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } catch (InvalidDataException) { return; } catch (OverflowException) { return; } throw new Exception("Expected rejection"); }
        private sealed class ProgressAction<T> : IProgress<T>
        { private readonly Action<T> action; public ProgressAction(Action<T> action) { this.action = action; } public void Report(T value) { action(value); } }
        private static void Shell(LocalPackageFormat format)
        {
            string root = Path.Combine(Path.GetTempPath(), "native-shell-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string unix = "/" + char.ToLowerInvariant(root[0]) + root.Substring(2).Replace('\\', '/'); var s = RemoteTempSession.Create(format);
                string dir = unix + "/" + s.Id, name = format == LocalPackageFormat.Ipk ? "opkg" : "apk";
                string command = PluginInstallCommands.Launch(s).Replace(s.Directory, dir).Replace(RemoteTempSession.Root, unix);
                string script = "#!/bin/sh\nset -eu\nmkdir -p " + ShellArgument.Quote(dir) + "\nexport PATH=" + ShellArgument.Quote(unix) + ":$PATH\ncat > " + ShellArgument.Quote(unix + "/" + name) + " <<'FAKE'\n#!/bin/sh\nprintf '%s\\n' \"$@\"\nprintf 'native stderr\\n' >&2\nexit 9\nFAKE\nchmod +x " + ShellArgument.Quote(unix + "/" + name) + "\nprintf 'not an executable shell script' > " + ShellArgument.Quote(dir + "/" + s.PackageFileName) + "\n" + command + "\ni=0\nwhile test ! -f " + ShellArgument.Quote(dir + "/exit.code") + "; do i=$((i+1)); test \"$i\" -lt 200 || exit 77; sleep 0.01; done\n" +
                    "test \"$(cat " + ShellArgument.Quote(dir + "/exit.code") + ")\" = 9\n" +
                    "if " + command + "; then exit 78; fi\n";
                string file = Path.Combine(root, "check.sh"); File.WriteAllText(file, script, new UTF8Encoding(false));
                var start = new ProcessStartInfo { FileName = @"C:\Program Files\Git\bin\bash.exe", Arguments = "--noprofile --norc \"" + file.Replace('\\', '/') + "\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(start)) { if (!process.WaitForExit(15000)) { process.Kill(); throw new TimeoutException("Native offline shell fixture"); } Equal(0, process.ExitCode); }
                string outText = File.ReadAllText(Path.Combine(root, s.Id, "stdout.log")).Replace("\r", ""); Equal((format == LocalPackageFormat.Ipk ? "install" : "add") + "\n" + dir + "/" + s.PackageFileName + "\n", outText);
                Equal("native stderr\n", File.ReadAllText(Path.Combine(root, s.Id, "stderr.log")).Replace("\r", ""));
            }
            finally { Directory.Delete(root, true); }
        }
    }
}
