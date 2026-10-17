using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class SftpPollTests
    {
        public static void Run(Action<string, Action> test)
        {
            foreach (string tool in new[] { "stat", "nohup", "base64", "dd" })
            {
                string name = tool;
                test("r2 shell check and launch do not require " + name, () =>
                    Equal(false, Regex.IsMatch(PluginInstallCommands.RunnerCheck + PluginInstallCommands.Launch(RemoteTempSession.Create()), @"\b" + name + @"\b")));
            }
            test("r2 obsolete SSH polling command generators removed", () =>
            { Equal(null, typeof(PluginInstallCommands).GetMethod("ReadLog")); Equal(null, typeof(PluginInstallCommands).GetMethod("ReadExit")); });
            test("r2 missing exit marker means running", () => Equal(null, Poll(new Files()).ExitCode));
            foreach (int code in new[] { 0, 1, 127, 255 })
            {
                int value = code;
                test("r2 exit marker " + value, () =>
                { var files = new Files(); files.Put("exit.code", Encoding.ASCII.GetBytes(" \r\n" + value.ToString(CultureInfo.InvariantCulture) + "\n")); Equal((int?)value, Poll(files).ExitCode); });
            }
            foreach (string text in new[] { "", " \n", "abc", "-1", "256", "2147483648", "+1", "1.5", "0\n1", "0\0", "٠", new string('0', 65) })
            {
                string value = text;
                test("r2 invalid exit rejected " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), () =>
                {
                    var files = new Files(); files.Put("exit.code", Encoding.UTF8.GetBytes(value));
                    Throws<InvalidDataException>(() => Poll(files), "安装器结束状态不可读"); Equal(1, files.Disposals);
                });
            }
            test("r2 exit read bounded to 65 bytes", () =>
            { var files = new Files(); files.Put("exit.code", new byte[100000]); Throws<InvalidDataException>(() => Poll(files)); Equal(65, files.BytesRead); });
            test("r2 missing stdout is empty", () => Equal(0, Poll(new Files()).Stdout.Length));
            test("r2 missing stderr is empty", () => Equal(0, Poll(new Files()).Stderr.Length));
            test("r2 stdout stderr offsets are independent", () =>
            {
                var files = new Files(); files.Put("stdout.log", new byte[] { 1, 2, 3, 4 }); files.Put("stderr.log", new byte[] { 5, 6, 7, 8, 9 });
                var poll = Poll(files, 2, 3); Bytes(new byte[] { 3, 4 }, poll.Stdout); Bytes(new byte[] { 8, 9 }, poll.Stderr);
                Equal(2L, files.Seeks["stdout.log"]); Equal(3L, files.Seeks["stderr.log"]);
            });
            test("r2 log read capped per stream and drained by offsets", () =>
            {
                var files = new Files(); byte[] data = Enumerable.Range(0, 140000).Select(i => (byte)(i % 256)).ToArray(); files.Put("stdout.log", data); files.Put("stderr.log", data);
                var first = Poll(files); Equal(65536, first.Stdout.Length); Equal(65536, first.Stderr.Length);
                var next = Poll(files, 65536, 65536); Bytes(data.Skip(65536).Take(65536).ToArray(), next.Stdout);
                var last = Poll(files, 131072, 131072); Equal(8928, last.Stdout.Length); Bytes(data.Skip(131072).ToArray(), last.Stderr);
            });
            foreach (bool stderr in new[] { false, true })
            {
                bool error = stderr;
                test("r2 offset at EOF " + error, () =>
                { var f = new Files(); f.Put(error ? "stderr.log" : "stdout.log", new byte[3]); var p = Poll(f, error ? 0 : 3, error ? 3 : 0); Equal(0, error ? p.Stderr.Length : p.Stdout.Length); Equal("", p.Diagnostic); });
                test("r2 offset beyond EOF returns empty diagnostic " + error, () =>
                { var f = new Files(); f.Put(error ? "stderr.log" : "stdout.log", new byte[3]); var p = Poll(f, error ? 0 : long.MaxValue, error ? long.MaxValue : 0); Equal(0, error ? p.Stderr.Length : p.Stdout.Length); Equal(true, p.Diagnostic.Contains("offset=")); });
                test("r2 negative offset rejects before connection " + error, () =>
                { var f = new Files(); Throws<ArgumentException>(() => Poll(f, error ? 0 : -1, error ? -1 : 0)); Equal(0, f.Creates); });
            }
            test("r2 raw non ASCII binary and control bytes unchanged", () =>
            {
                var f = new Files(); byte[] data = Enumerable.Range(0, 256).Select(i => (byte)i).Concat(Encoding.UTF8.GetBytes("中文\r\n\b进度\0")).ToArray();
                f.Put("stdout.log", data); f.Put("stderr.log", data); var p = Poll(f); Bytes(data, p.Stdout); Bytes(data, p.Stderr);
            });
            test("r2 UTF8 split across 65536 byte boundary is byte exact", () =>
            {
                var f = new Files(); byte[] data = Enumerable.Repeat((byte)'x', 65535).Concat(Encoding.UTF8.GetBytes("中文\r\n")).ToArray(); f.Put("stdout.log", data);
                byte[] first = Poll(f).Stdout, second = Poll(f, first.Length).Stdout; Bytes(data, first.Concat(second).ToArray());
            });
            test("r2 short SFTP reads filled until EOF or cap", () =>
            { var f = new Files { ShortReads = 3 }; byte[] data = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray(); f.Put("exit.code", Encoding.ASCII.GetBytes("127\n")); f.Put("stdout.log", data); var p = Poll(f); Equal((int?)127, p.ExitCode); Bytes(data, p.Stdout); });
            test("r2 one factory one connection all three files in order and disposed", () =>
            {
                var f = new Files(); f.Put("exit.code", new byte[] { 48 }); f.Put("stdout.log", new byte[] { 1 }); f.Put("stderr.log", new byte[] { 2 }); Poll(f);
                Equal(1, f.Creates); Equal(1, f.Connects); Equal(1, f.Disposals); Equal(3, f.StreamDisposals);
                Equal("exit.code,stdout.log,stderr.log", string.Join(",", f.Opened));
            });
            test("r2 connection failure disposes and never reads", () =>
            { var f = new Files { ConnectFailure = new IOException("SFTP transport lost") }; Throws<IOException>(() => Poll(f)); Equal(1, f.Disposals); Equal(0, f.Opened.Count); });
            test("r2 host key rejection propagated without fallback", () =>
            { var f = new Files { ConnectFailure = new InvalidOperationException("Host key rejected") }; Throws<InvalidOperationException>(() => Poll(f), "Host key"); Equal(1, f.Creates); Equal(1, f.Disposals); Equal(0, f.Opened.Count); });
            test("r2 permission read errors are not treated as missing logs", () =>
            { var f = new Files { OpenFailure = "stdout.log" }; f.Put("stdout.log", new byte[4]); Throws<UnauthorizedAccessException>(() => Poll(f)); Equal(1, f.Disposals); });
            foreach (string file in new[] { "exit.code", "stdout.log", "stderr.log" })
            {
                string name = file;
                test("r2 file disappearing after exists is safe " + name, () =>
                { var f = new Files { Disappear = name }; f.Put(name, new byte[1]); var p = Poll(f); Equal(null, p.ExitCode); Equal(0, p.Stdout.Length + p.Stderr.Length); Equal(1, f.Disposals); });
            }
            test("r2 reconnect creates new SFTP connection and resumes original session offset", () =>
            {
                var f = new Files(); f.Put("stdout.log", new byte[] { 1, 2, 3, 4 }); Bytes(new byte[] { 1, 2, 3, 4 }, Poll(f).Stdout);
                f.Put("stdout.log", new byte[] { 1, 2, 3, 4, 5, 6 }); f.Put("exit.code", new byte[] { 48 }); var next = Poll(f, 4); Bytes(new byte[] { 5, 6 }, next.Stdout);
                Equal((int?)0, next.ExitCode); Equal(2, f.Creates); Equal(2, f.Disposals);
            });
            foreach (var router in new[] {
                new RouterEnvironment { Architecture="x86_64", PackageManager=PackageManagerKind.APK },
                new RouterEnvironment { Architecture="aarch64_cortex-a53", PackageManager=PackageManagerKind.OPKG } })
            {
                string label = router.Architecture + "/" + router.PackageManager;
                test("r2 shared runner poll exit cleanup contract " + label, () =>
                { var f = new Files(); f.Put("exit.code", new byte[] { 48 }); f.Put("stdout.log", Encoding.ASCII.GetBytes("OK")); Equal((int?)0, Poll(f).ExitCode); Equal(1, f.Connects); Equal(true, PluginInstallCommands.Launch(f.Session).Contains("launch.once")); Equal(true, f.Session.CleanupCommand().Contains(f.Session.Directory)); });
            }
        }
        internal static InstallerPoll Poll(Files files, long stdout = 0, long stderr = 0)
        { return new SftpInstallerPoller().Poll(files.Create, files.Session, stdout, stderr); }
        internal static void Bytes(byte[] expected, byte[] actual) { Equal(true, expected.SequenceEqual(actual)); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
        private static void Throws<T>(Action body, string text = null) where T : Exception
        { try { body(); } catch (T e) { if (text != null) Equal(true, e.Message.Contains(text)); return; } throw new Exception("Expected " + typeof(T).Name); }
        internal sealed class Files
        {
            public readonly RemoteTempSession Session = RemoteTempSession.Create();
            public readonly Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
            public readonly Dictionary<string, long> Seeks = new Dictionary<string, long>();
            public readonly List<string> Opened = new List<string>();
            public int Creates, Connects, Disposals, StreamDisposals, BytesRead, ShortReads = int.MaxValue;
            public Exception ConnectFailure;
            public string OpenFailure, Disappear;
            public void Put(string name, byte[] data) { Data[Session.Directory + "/" + name] = data; }
            public IInstallerFileSession Create() { Creates++; return new Client(this); }
            private sealed class Client : IInstallerFileSession
            {
                private readonly Files files;
                public Client(Files files) { this.files = files; }
                public void Connect() { files.Connects++; if (files.ConnectFailure != null) throw files.ConnectFailure; }
                public bool Exists(string path) { return files.Data.ContainsKey(path); }
                public Stream OpenRead(string path)
                {
                    string name = path.Substring(path.LastIndexOf('/') + 1); files.Opened.Add(name);
                    if (name == files.Disappear) throw new FileNotFoundException();
                    if (name == files.OpenFailure) throw new UnauthorizedAccessException();
                    return new Input(files, name, files.Data[path]);
                }
                public void Dispose() { files.Disposals++; }
            }
            private sealed class Input : MemoryStream
            {
                private readonly Files files; private readonly string name;
                public Input(Files files, string name, byte[] data) : base(data, false) { this.files = files; this.name = name; }
                public override int Read(byte[] buffer, int offset, int count) { int read = base.Read(buffer, offset, Math.Min(count, files.ShortReads)); files.BytesRead += read; return read; }
                public override long Seek(long offset, SeekOrigin origin) { files.Seeks[name] = offset; return base.Seek(offset, origin); }
                protected override void Dispose(bool disposing) { if (disposing) files.StreamDisposals++; base.Dispose(disposing); }
            }
        }
    }
}
