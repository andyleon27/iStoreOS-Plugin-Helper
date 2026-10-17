using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Renci.SshNet;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    // Developer/test only. Not compiled into the product or included in the portable ZIP.
    internal static class RunnerSmoke
    {
        internal static readonly byte[] ExpectedStdout = Encoding.ASCII.GetBytes(new string('x', 70400) + "\nRUNNER_STDOUT_OK\n");
        internal static readonly byte[] ExpectedStderr = Encoding.ASCII.GetBytes("RUNNER_STDERR_OK\n");
        internal const string Script = "#!/bin/sh\n# SYNTHETIC_RUNNER_SMOKE_NOT_A_PLUGIN\ni=0\nwhile test \"$i\" -lt 1100; do\nprintf '%s' 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'\ni=$((i + 1))\ndone\nprintf '\\nRUNNER_STDOUT_OK\\n'\nprintf 'RUNNER_STDERR_OK\\n' >&2\nexit 0\n";
        internal sealed class Result
        {
            public bool Passed, CleanupSucceeded, Ended;
            public int Starts, Polls;
            public long StdoutBytes, StderrBytes;
            public int? ExitCode;
            public string Directory;
        }
        internal static Result Run(IPluginInstallSession session, TimeSpan limit)
        {
            var result = new Result(); var remote = RemoteTempSession.Create(); result.Directory = remote.Directory;
            bool created = false, dispatched = false;
            try
            {
                session.CreateDirectory(remote); created = true;
                using (var script = new MemoryStream(Encoding.UTF8.GetBytes(Script))) session.Upload(script, remote, count => { }, CancellationToken.None);
                session.CheckRunner();
                dispatched = true; result.Starts++;
                session.StartInstaller(remote);
                using (InstallerMonitoring.Open(session, remote))
                using (var stdout = new MemoryStream())
                using (var stderr = new MemoryStream())
                {
                    var watch = Stopwatch.StartNew();
                    do
                    {
                        var poll = session.PollInstaller(remote, stdout.Length, stderr.Length); result.Polls++;
                        stdout.Write(poll.Stdout, 0, poll.Stdout.Length); stderr.Write(poll.Stderr, 0, poll.Stderr.Length);
                        if (stdout.Length > 1048576 || stderr.Length > 1048576) throw new InvalidDataException("Smoke 输出超出无害测试上限。");
                        if (poll.ExitCode.HasValue && poll.Stdout.Length < 65536 && poll.Stderr.Length < 65536)
                        {
                            result.Ended = true; result.ExitCode = poll.ExitCode; result.StdoutBytes = stdout.Length; result.StderrBytes = stderr.Length;
                            result.Passed = poll.ExitCode == 0 && stdout.ToArray().SequenceEqual(ExpectedStdout) && stderr.ToArray().SequenceEqual(ExpectedStderr);
                            break;
                        }
                        Thread.Sleep(25);
                    } while (watch.Elapsed < limit);
                    if (!result.Ended) throw new TimeoutException("Smoke 未确认结束；保留原 session，禁止第二次派发：" + remote.Directory);
                }
            }
            finally
            {
                if (created && (!dispatched || result.Ended)) result.CleanupSucceeded = session.Cleanup(remote);
            }
            result.Passed = result.Passed && result.CleanupSucceeded;
            return result;
        }
        internal static void RunTests(Action<string, Action> test)
        {
            foreach (var pair in new[] { "x86_64/APK", "aarch64_cortex-a53/OPKG" })
            {
                string label = pair;
                test("r2 harmless smoke uses shared Launch SFTP Poll Exit Cleanup " + label, () =>
                {
                    var fake = new Fake(); var result = Run(fake, TimeSpan.FromSeconds(2));
                    Equal(true, result.Passed); Equal(1, fake.Starts); Equal(1, fake.Cleanups); Equal(true, result.Polls >= 2);
                    Equal(result.Polls, fake.Files.Creates); Equal(result.Polls, fake.Files.Disposals);
                    Equal(ExpectedStdout.Length, (int)result.StdoutBytes); Equal(true, fake.Offsets.Contains(65536));
                });
            }
            test("r2 smoke unknown launch never dispatches twice or cleans", () =>
            {
                var fake = new Fake { LoseLaunch = true }; bool lost = false;
                try { Run(fake, TimeSpan.FromMilliseconds(10)); } catch (IOException) { lost = true; }
                Equal(true, lost); Equal(1, fake.Starts); Equal(0, fake.Cleanups);
            });
            test("r2 smoke timeout preserves active session", () =>
            {
                var fake = new Fake { NeverEnds = true }; bool timedOut = false;
                try { Run(fake, TimeSpan.Zero); } catch (TimeoutException) { timedOut = true; }
                Equal(true, timedOut); Equal(1, fake.Starts); Equal(0, fake.Cleanups);
            });
            test("r2 smoke cleanup failure cannot claim pass", () =>
            { var fake = new Fake { CleanupFails = true }; Equal(false, Run(fake, TimeSpan.FromSeconds(2)).Passed); Equal(1, fake.Starts); });
        }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
        private sealed class Fake : IPluginInstallSession
        {
            public readonly SftpPollTests.Files Files = new SftpPollTests.Files();
            public readonly List<long> Offsets = new List<long>();
            public int Starts, Cleanups;
            public bool LoseLaunch, NeverEnds, CleanupFails;
            public string Sanitize(string text) { return text; }
            public void CreateDirectory(RemoteTempSession s) { Equal(true, s.CreateCommand().Contains("umask 077")); }
            public void Upload(Stream stream, RemoteTempSession s, Action<long> progress, CancellationToken token)
            { using (var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true)) Equal(Script, reader.ReadToEnd()); }
            public void CheckRunner() { Equal(false, PluginInstallCommands.RunnerCheck.Contains("command -v")); }
            public void StartInstaller(RemoteTempSession s)
            {
                Starts++; Equal(true, PluginInstallCommands.Launch(s).Contains("trap '' HUP"));
                if (LoseLaunch) throw new IOException("lost control channel");
                Files.Data[s.Directory + "/stdout.log"] = ExpectedStdout; Files.Data[s.Directory + "/stderr.log"] = ExpectedStderr;
                if (!NeverEnds) Files.Data[s.Directory + "/exit.code"] = new byte[] { 48, 10 };
            }
            public InstallerPoll PollInstaller(RemoteTempSession s, long stdout, long stderr)
            { Offsets.Add(stdout); return new SftpInstallerPoller().Poll(Files.Create, s, stdout, stderr); }
            public bool Cleanup(RemoteTempSession s) { Cleanups++; Equal(true, s.CleanupCommand().Contains(s.Directory)); return !CleanupFails; }
            public long TmpAvailable() { throw new InvalidOperationException("Smoke must not inspect environment."); }
            public long RemoteFileSize(RemoteTempSession s) { throw new InvalidOperationException(); }
            public ProbeResult RemoteHash(RemoteTempSession s) { throw new InvalidOperationException(); }
            public ProbeResult Inspect(RemoteTempSession s, bool check) { throw new InvalidOperationException(); }
            public PackageSnapshot Snapshot(PackageManagerKind manager) { throw new InvalidOperationException("Smoke must not inspect packages."); }
            public bool PackageExists(PackageManagerKind manager, string name) { throw new InvalidOperationException(); }
        }
        internal static int RunInteractive()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("开发专用 Runner Smoke：不安装插件，不修改软件源。复用产品 Launch / SFTP Poll / Exit / Cleanup。");
            Console.Write("路由器地址："); string host = Console.ReadLine();
            Console.Write("用户名（留空 root）："); string user = Console.ReadLine(); if (string.IsNullOrEmpty(user)) user = "root";
            Console.Write("密码（不显示、不保存）："); var secret = new StringBuilder();
            for (;;) { var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; } else if (!char.IsControl(key.KeyChar)) secret.Append(key.KeyChar); }
            Console.WriteLine(); string password = secret.ToString(); secret.Clear();
            using (var service = new SshService())
            {
                try
                {
                    var connected = service.ConnectAndDetectAsync(host, user, password, new ConsoleProgress(), CancellationToken.None).GetAwaiter().GetResult();
                    if (!connected.Success) { Console.WriteLine(connected.UserMessage); return 1; }
                    Console.WriteLine("目标：" + connected.Environment.Architecture + " / " + connected.Environment.PackageManager);
                    var factoryMethod = typeof(SshService).GetMethod("CreateTrustedTransfer", BindingFlags.NonPublic | BindingFlags.Instance);
                    Func<SftpClient> factory = () => (SftpClient)factoryMethod.Invoke(service, new object[] { connected.Environment.HostKeyFingerprint });
                    var ssh = (SshClient)typeof(SshService).GetField("client", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(service);
                    var type = typeof(SshService).Assembly.GetType("iStoreOS.PluginHelper.Services.SshPluginInstallSession", true);
                    var session = (IPluginInstallSession)Activator.CreateInstance(type, ssh, factory, new Func<string, string>(text => LogHelper.RedactPreservingLines(text, password)));
                    Result result = Run(session, TimeSpan.FromSeconds(45));
                    Console.WriteLine("SMOKE " + (result.Passed ? "PASS" : "FAIL") + " | stdout=" + result.StdoutBytes + " | stderr=" + result.StderrBytes +
                        " | exit=" + result.ExitCode + " | polls=" + result.Polls + " | starts=" + result.Starts + " | cleanup=" + result.CleanupSucceeded);
                    return result.Passed ? 0 : 1;
                }
                catch (Exception error) { Console.WriteLine(LogHelper.RedactPreservingLines(error.ToString(), password)); return 1; }
            }
        }
        private sealed class ConsoleProgress : IProgress<string> { public void Report(string text) { Console.WriteLine(text); } }
    }
}
