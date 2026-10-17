using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class RunnerCompatibilityTests
    {
        public static void Run(Action<string, Action> test)
        {
            test("V1.0.2 runner does not require optional nohup or setsid", () =>
            {
                string commands = PluginInstallCommands.RunnerCheck + "\n" + PluginInstallCommands.Launch(RemoteTempSession.Create());
                Equal(false, Regex.IsMatch(commands, @"\b(nohup|setsid)\b"));
            });
            test("V1.0.2 runner no longer probes optional transport tools", () =>
            {
                string check = PluginInstallCommands.RunnerCheck;
                Equal(false, check.Contains("command -v"));
                Equal(false, Regex.IsMatch(check, @"\b(stat|nohup|base64|dd)\b"));
                Contains(check, "ISTORE_RUNNER_READY");
            });
            test("V1.0.2 runner capability probe rejects an unsupported HUP trap", () =>
            {
                string check = PluginInstallCommands.RunnerCheck;
                Contains(check, "if ! (trap '' HUP);");
                Contains(check, "Cannot ignore SIGHUP with shell trap");
                Contains(check, " >&2; exit 1; fi;");
                Before(check, "trap '' HUP", "ISTORE_RUNNER_READY");
            });
            test("V1.0.2 launch ignores HUP before consuming launch once or forking", () =>
            {
                string launch = PluginInstallCommands.Launch(RemoteTempSession.Create());
                Before(launch, "trap '' HUP && ", "launch.once");
                Before(launch, "launch.once", "&& { sh -c ");
                Before(launch, "trap '' HUP", " 2>&1 & }");
            });
            test("V1.0.2 background launch closes all SSH descriptors and separates installer logs", () =>
            {
                var session = RemoteTempSession.Create();
                string launch = PluginInstallCommands.Launch(session), runner = RunnerScript(launch);
                Equal(true, launch.EndsWith(" </dev/null >/dev/null 2>&1 & }", StringComparison.Ordinal));
                Contains(runner, " > " + Path(session, "stdout.log") + " 2> " + Path(session, "stderr.log"));
                Equal(false, runner.Contains("2>&1"));
            });
            test("V1.0.2 launch uses one exclusive marker and one installer invocation", () =>
            {
                var session = RemoteTempSession.Create();
                string launch = PluginInstallCommands.Launch(session), runner = RunnerScript(launch);
                Equal(1, Regex.Matches(launch, "launch\\.once").Count);
                Contains(launch, "mkdir -- " + Path(session, "launch.once") + " && { sh -c ");
                Equal(false, launch.Contains("mkdir -p"));
                Equal(1, Regex.Matches(runner, Regex.Escape("sh " + Path(session, "package.run"))).Count);
                Equal(false, Regex.IsMatch(runner, @"\b(while|until|retry)\b"));
            });
            test("V1.0.2 launch preserves symlink gates and the private session working directory", () =>
            {
                var session = RemoteTempSession.Create(); string launch = PluginInstallCommands.Launch(session);
                Before(launch, "test ! -L " + ShellArgument.Quote(RemoteTempSession.Root), "launch.once");
                Before(launch, "test ! -L " + ShellArgument.Quote(session.Directory), "launch.once");
                string runner = RunnerScript(launch);
                Equal(true, runner.StartsWith("cd " + ShellArgument.Quote(session.Directory) + " || exit 125; ", StringComparison.Ordinal));
                Contains(runner, "LC_ALL=C TMPDIR=" + ShellArgument.Quote(session.Directory) + " sh ");
            });
            test("V1.0.2 runner captures the installer exit immediately and publishes it atomically", () =>
            {
                string runner = RunnerScript(PluginInstallCommands.Launch(RemoteTempSession.Create()));
                Contains(runner, "; rc=$?; printf '%s\\n' \"$rc\" > exit.tmp && mv -- exit.tmp exit.code");
                Equal(1, Regex.Matches(runner, @"\brc=\$\?").Count);
                Equal(false, runner.Contains("> exit.code"));
            });
            test("V1.0.2 exit polling waits for the final marker rather than temporary output", () =>
            {
                var files = new SftpPollTests.Files(); files.Put("exit.tmp", new byte[] { 48 });
                Equal(null, SftpPollTests.Poll(files).ExitCode);
                files.Put("exit.code", new byte[] { 48 }); Equal((int?)0, SftpPollTests.Poll(files).ExitCode);
            });
            test("V1.0.2 both log streams retain bounded byte offsets and direct raw transport", () =>
            {
                CultureInfo original = Thread.CurrentThread.CurrentCulture;
                try
                {
                    Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                    var files = new SftpPollTests.Files();
                    byte[] bytes = new byte[70000]; files.Put("stdout.log", bytes); files.Put("stderr.log", bytes);
                    foreach (bool stderr in new[] { false, true })
                    {
                        var poll = SftpPollTests.Poll(files, stderr ? 0 : 1234, stderr ? 1234 : 0);
                        Equal(65536, stderr ? poll.Stderr.Length : poll.Stdout.Length);
                        Equal(1234L, files.Seeks[stderr ? "stderr.log" : "stdout.log"]);
                    }
                }
                finally { Thread.CurrentThread.CurrentCulture = original; }
            });
            test("V1.0.2 log polling rejects negative offsets for either stream", () =>
            {
                foreach (bool stderr in new[] { false, true })
                {
                    bool rejected = false;
                    try { SftpPollTests.Poll(new SftpPollTests.Files(), stderr ? 0 : -1, stderr ? -1 : 0); }
                    catch (ArgumentException) { rejected = true; }
                    Equal(true, rejected);
                }
            });
            test("V1.0.2 runner capability exception preserves its context exit and missing name", () =>
            {
                IOException error = RequireFailure(new ProbeResult { ExitStatus = 127, Error = "Missing required runner command(s): dd\n" },
                    "后台安装能力检查（sh / base64 / dd / HUP）");
                Contains(error.Message, "后台安装能力检查"); Contains(error.Message, "exit 127");
                Contains(error.Message, "Missing required runner command(s): dd");
                Equal(false, error.Message.Contains("软件包快照"));
            });
            test("V1.0.2 OPKG snapshot exception remains distinct from runner capability failure", () =>
            {
                IOException error = RequireFailure(new ProbeResult { ExitStatus = 127, Error = "opkg: command not found" }, "读取 OPKG 软件包快照");
                Contains(error.Message, "读取 OPKG 软件包快照"); Contains(error.Message, "exit 127"); Contains(error.Message, "opkg: command not found");
                Equal(false, error.Message.Contains("后台安装能力检查"));
            });
            test("V1.0.2 successful remote checks preserve their original probe result", () =>
            {
                var result = new ProbeResult { ExitStatus = 0, Output = "ISTORE_RUNNER_READY\n", Error = "diagnostic" };
                Equal(true, object.ReferenceEquals(result, RequireMethod().Invoke(null, new object[] { result, "后台安装能力检查" })));
            });
        }

        private static string Path(RemoteTempSession session, string name)
        { return ShellArgument.Quote(session.Directory + "/" + name); }
        private static string RunnerScript(string launch)
        {
            const string start = "&& { sh -c ", end = " </dev/null >/dev/null 2>&1 & }";
            int index = launch.IndexOf(start, StringComparison.Ordinal);
            Equal(true, index >= 0 && launch.EndsWith(end, StringComparison.Ordinal));
            string word = launch.Substring(index + start.Length, launch.Length - index - start.Length - end.Length);
            Equal(true, word.StartsWith("'", StringComparison.Ordinal) && word.EndsWith("'", StringComparison.Ordinal));
            return word.Substring(1, word.Length - 2).Replace("'\"'\"'", "'");
        }
        private static MethodInfo RequireMethod()
        {
            Type type = typeof(PluginInstallCommands).Assembly.GetType("iStoreOS.PluginHelper.Services.SshPluginInstallSession", true);
            MethodInfo method = type.GetMethod("Require", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(ProbeResult), typeof(string) }, null);
            if (method == null) throw new Exception("Remote Require operation context overload is missing.");
            return method;
        }
        private static IOException RequireFailure(ProbeResult result, string operation)
        {
            try { RequireMethod().Invoke(null, new object[] { result, operation }); }
            catch (TargetInvocationException error)
            {
                var failure = error.InnerException as IOException;
                if (failure != null) return failure;
                throw;
            }
            throw new Exception("Expected remote operation to reject exit " + result.ExitStatus);
        }
        private static void Contains(string text, string value)
        { if (text.IndexOf(value, StringComparison.Ordinal) < 0) throw new Exception("Missing required fragment: " + value); }
        private static void Before(string text, string first, string second)
        {
            int a = text.IndexOf(first, StringComparison.Ordinal), b = text.IndexOf(second, StringComparison.Ordinal);
            if (a < 0 || b <= a) throw new Exception("Expected '" + first + "' before '" + second + "'.");
        }
        private static void Equal<T>(T expected, T actual)
        { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    }
}
