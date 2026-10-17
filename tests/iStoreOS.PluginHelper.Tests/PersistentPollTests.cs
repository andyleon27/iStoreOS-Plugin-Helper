using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class PersistentPollTests
    {
        internal static void Run(Action<string, Action> test)
        {
            test("r4 persistent ten polls connect once and dispose at monitor exit", () => {
                var f = new Files(); var remote = RemoteTempSession.Create();
                using (var monitor = new PersistentInstallerMonitor(f.Create, remote)) {
                    for (int i = 0; i < 10; i++) monitor.Poll(0, 0);
                    Equal(1, f.Creates); Equal(1, f.Connects); Equal(0, f.Disposals); Equal(1, monitor.ConnectionCount);
                    f.Data[remote.Directory + "/exit.code"] = Encoding.ASCII.GetBytes("0\n"); Equal((int?)0, monitor.Poll(0, 0).ExitCode);
                }
                Equal(1, f.Disposals);
            });
            test("r4 disconnected persistent client recreates and keeps session offsets", () => {
                var f = new Files(); var remote = RemoteTempSession.Create(); f.Data[remote.Directory + "/stdout.log"] = new byte[] { 1, 2, 3, 4 };
                using (var monitor = new PersistentInstallerMonitor(f.Create, remote)) {
                    SftpPollTests.Bytes(new byte[] { 1, 2, 3, 4 }, monitor.Poll(0, 0).Stdout); f.Last.Connected = false;
                    f.Data[remote.Directory + "/stdout.log"] = new byte[] { 1, 2, 3, 4, 5 };
                    SftpPollTests.Bytes(new byte[] { 5 }, monitor.Poll(4, 0).Stdout); Equal(2, monitor.ConnectionCount); Equal(1, f.Disposals);
                }
                Equal(2, f.Creates); Equal(2, f.Disposals); Equal(4L, f.Seeks.Last()); True(f.Paths.All(p => p.StartsWith(remote.Directory + "/")));
            });
            test("r4 connection failure mid-poll retries unchanged raw offsets once", () => {
                var f = new Files { FailExistsCall = 3 }; var remote = RemoteTempSession.Create(); f.Data[remote.Directory + "/stdout.log"] = new byte[] { 1, 2, 3 };
                using (var monitor = new PersistentInstallerMonitor(f.Create, remote)) {
                    SftpPollTests.Bytes(new byte[] { 2, 3 }, monitor.Poll(1, 0).Stdout); Equal(2, f.Creates); Equal(2, f.Seeks.Count); True(f.Seeks.All(s => s == 1));
                }
                Equal(2, f.Disposals);
            });
            test("r4 persistent permission error does not reconnect or hide failure", () => {
                var f = new Files { ReadError = new UnauthorizedAccessException("denied") }; var remote = RemoteTempSession.Create(); f.Data[remote.Directory + "/stdout.log"] = new byte[1];
                using (var monitor = new PersistentInstallerMonitor(f.Create, remote)) Throws<UnauthorizedAccessException>(() => monitor.Poll(0, 0));
                Equal(1, f.Creates); Equal(1, f.Disposals);
            });
            test("r4 persistent malformed exit preserves strict parsing and scope disposal", () => {
                var f = new Files(); var remote = RemoteTempSession.Create(); f.Data[remote.Directory + "/exit.code"] = Encoding.ASCII.GetBytes("256");
                using (var monitor = new PersistentInstallerMonitor(f.Create, remote)) Throws<InvalidDataException>(() => monitor.Poll(0, 0));
                Equal(1, f.Creates); Equal(1, f.Disposals);
            });
            test("r4 persistent missing exit and logs remain RUNNING", () => {
                var f = new Files(); using (var monitor = new PersistentInstallerMonitor(f.Create, RemoteTempSession.Create())) {
                    var p = monitor.Poll(0, 0); Equal(null, p.ExitCode); Equal(0, p.Stdout.Length + p.Stderr.Length);
                }
                Equal(1, f.Disposals);
            });
            test("r4 persistent raw 65536 chunks and independent stderr unchanged", () => {
                var f = new Files(); var remote = RemoteTempSession.Create(); var data = Enumerable.Range(0, 70000).Select(i => (byte)(i % 256)).ToArray();
                f.Data[remote.Directory + "/stdout.log"] = data; f.Data[remote.Directory + "/stderr.log"] = new byte[] { 7, 8 }; f.Data[remote.Directory + "/exit.code"] = new byte[] { 48 };
                using (var monitor = new PersistentInstallerMonitor(f.Create, remote)) {
                    var first = monitor.Poll(0, 0); Equal(65536, first.Stdout.Length); SftpPollTests.Bytes(new byte[] { 7, 8 }, first.Stderr);
                    var second = monitor.Poll(65536, 2); SftpPollTests.Bytes(data.Skip(65536).ToArray(), second.Stdout); Equal(0, second.Stderr.Length); Equal(1, f.Creates);
                }
            });
            test("r4 disposed monitor never creates another trusted client", () => {
                var f = new Files(); var monitor = new PersistentInstallerMonitor(f.Create, RemoteTempSession.Create()); monitor.Poll(0, 0); monitor.Dispose(); monitor.Dispose();
                Throws<ObjectDisposedException>(() => monitor.Poll(0, 0)); Equal(1, f.Creates); Equal(1, f.Disposals);
            });
            test("r4 monitor connect authentication rejection never retries", () => {
                var f = new Files { ConnectError = new SshAuthenticationException("rejected") };
                using (var monitor = new PersistentInstallerMonitor(f.Create, RemoteTempSession.Create())) Throws<SshAuthenticationException>(() => monitor.Poll(0, 0));
                Equal(1, f.Creates); Equal(1, f.Disposals);
            });
            test("r4 monitor connect host-key rejection never retries", () => {
                var f = new Files { ConnectError = new SshConnectionException("host key rejected") };
                using (var monitor = new PersistentInstallerMonitor(f.Create, RemoteTempSession.Create())) Throws<SshConnectionException>(() => monitor.Poll(0, 0));
                Equal(1, f.Creates); Equal(1, f.Disposals);
            });
            test("r4 repeated SFTP connection failure is bounded at one reconnect", () => {
                var f = new Files { AlwaysDisconnect = true };
                using (var monitor = new PersistentInstallerMonitor(f.Create, RemoteTempSession.Create())) Throws<SshConnectionException>(() => monitor.Poll(0, 0));
                Equal(2, f.Creates); Equal(2, f.Disposals);
            });
            test("r4 persistent negative offset rejected before factory", () => {
                var f = new Files(); using (var monitor = new PersistentInstallerMonitor(f.Create, RemoteTempSession.Create())) Throws<ArgumentException>(() => monitor.Poll(-1, 0));
                Equal(0, f.Creates);
            });
            test("r4 persistent generic IO error is not treated as network reconnect", () => {
                var f = new Files { ReadError = new IOException("filesystem error") }; var remote = RemoteTempSession.Create(); f.Data[remote.Directory + "/stdout.log"] = new byte[1];
                using (var monitor = new PersistentInstallerMonitor(f.Create, remote)) Throws<IOException>(() => monitor.Poll(0, 0)); Equal(1, f.Creates);
            });
            test("r4 persistent timeout reconnect retains byte position", () => {
                var f = new Files { ReadError = new SshOperationTimeoutException("read timeout"), ErrorOnce = true }; var remote = RemoteTempSession.Create(); f.Data[remote.Directory + "/stdout.log"] = new byte[] { 3, 4 };
                using (var monitor = new PersistentInstallerMonitor(f.Create, remote)) SftpPollTests.Bytes(new byte[] { 4 }, monitor.Poll(1, 0).Stdout);
                Equal(2, f.Creates); Equal(2, f.Disposals);
            });
        }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
        private static void True(bool value) { Equal(true, value); }
        private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        internal sealed class Files
        {
            public readonly Dictionary<string, byte[]> Data = new Dictionary<string, byte[]>();
            public readonly List<string> Paths = new List<string>(); public readonly List<long> Seeks = new List<long>();
            public int Creates, Connects, Disposals, ExistsCalls, FailExistsCall;
            public bool AlwaysDisconnect, ErrorOnce;
            public Exception ReadError, ConnectError;
            public Client Last;
            public IInstallerFileSession Create() { Creates++; Last = new Client(this); return Last; }
            internal sealed class Client : IConnectedInstallerFileSession
            {
                private readonly Files files; public bool Connected; private bool disposed;
                public Client(Files files) { this.files = files; }
                public bool IsConnected { get { return Connected && !disposed; } }
                public void Connect() { files.Connects++; if (files.ConnectError != null) throw files.ConnectError; Connected = true; }
                public bool Exists(string path) {
                    files.ExistsCalls++; files.Paths.Add(path);
                    if (files.AlwaysDisconnect || files.ExistsCalls == files.FailExistsCall) { Connected = false; throw new SshConnectionException("lost SFTP"); }
                    return files.Data.ContainsKey(path);
                }
                public Stream OpenRead(string path) {
                    files.Paths.Add(path); if (files.ReadError != null) { var error = files.ReadError; if (files.ErrorOnce) files.ReadError = null; throw error; }
                    return new Input(files, files.Data[path]);
                }
                public void Dispose() { if (disposed) throw new Exception("double dispose"); disposed = true; Connected = false; files.Disposals++; }
            }
            private sealed class Input : MemoryStream
            { private readonly Files files; public Input(Files files, byte[] bytes) : base(bytes, false) { this.files = files; }
                public override long Seek(long offset, SeekOrigin origin) { files.Seeks.Add(offset); return base.Seek(offset, origin); } }
        }
    }
}
