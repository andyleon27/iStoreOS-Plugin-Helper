using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Controls;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class MaintenanceMenuRuntimeTests
    {
        private static readonly Guid TaskId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        private static readonly Guid RunId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        private const string Values = "123|456|1793000000";
        private static string Wire { get { return "ISTORE_MENU_V1|" + TaskId.ToString("N") + "|" + RunId.ToString("N"); } }
        private static RemoteMaintenanceWorkspace Workspace() { return RemoteMaintenanceWorkspace.CreateMenu(TaskId, true); }
        private static MaintenanceMenuProtocol Protocol() { var protocol = new MaintenanceMenuProtocol(Workspace(), RunId); protocol.Begin(); return protocol; }
        private static string Marker(string kind, string values = Values, string end = "\a") { return "\u001b]777;" + Wire + "|" + kind + "|" + values + end; }
        private static ProbeResult State(string state, string values = Values, int code = 0)
        { return new ProbeResult { ExitStatus = 0, Output = Wire + "|STATE|" + state + (state == "EXITED" ? "|" + values + "|" + code : state == "RUNNING" ? "|" + values : "") + "\n" }; }
        private static ProbeResult Cleaned() { return new ProbeResult { ExitStatus = 0, Output = Wire + "|CLEANED\n" }; }

        public static void Run(Action<string, Action> test)
        {
            test("Menu runtime short start is one bounded exec command and one LF", () => {
                string command = MaintenanceMenuCommands.StartCommand(Workspace());
                Equal("exec sh '" + Workspace().FilePath("helper-launcher.sh") + "'", command);
                True(Encoding.UTF8.GetByteCount(command) <= 256);
                Equal(command + "\n", MaintenanceMenuCommands.StartPayload(Workspace()));
                foreach (string bad in new[] { "curl", "wget", "base64", "sh -c", ";", "\r", "\n" }) Equal(false, command.Contains(bad));
            });
            test("Menu runtime launcher is independent LF UTF8 script and preserves menu invocation", () => {
                byte[] bytes = MaintenanceMenuCommands.LauncherBytes(Workspace(), RunId);
                MaintenanceScriptIntegrity.ValidateScript(bytes);
                string text = new UTF8Encoding(false, true).GetString(bytes);
                True(text.StartsWith("#!/bin/sh\n", StringComparison.Ordinal)); Equal(false, text.Contains("\r"));
                True(text.Contains("sh \"$menu\"\nrc=$?\nmenu_returned=1")); True(text.Contains("set -C"));
                True(text.Contains("menu.pid")); True(text.Contains("menu.start")); True(text.Contains("menu.exit.tmp"));
                True(text.Contains("mv -- \"$exit_tmp\" \"$exit_file\"")); True(text.Contains("process_identity $$"));
                True(text.Contains("interrupted=1")); Equal(false, text.Contains("nohup")); Equal(false, text.Contains("curl "));
                Equal(false, text.Contains("/etc/hosts")); Equal(false, text.Contains("rm -rf"));
            });
            test("Menu runtime rejects workspace missing launcher and empty run IDs", () => {
                foreach (var space in new[] { RemoteMaintenanceWorkspace.CreateMenu(Guid.NewGuid()) })
                {
                    Throws<ArgumentException>(() => MaintenanceMenuCommands.StartCommand(space));
                    Throws<ArgumentException>(() => new MaintenanceMenuProtocol(space, RunId));
                }
                Throws<ArgumentNullException>(() => MaintenanceMenuCommands.StartCommand(null));
                Throws<ArgumentException>(() => MaintenanceMenuCommands.LauncherBytes(Workspace(), Guid.Empty));
                Throws<ArgumentException>(() => MaintenanceMenuCommands.ReadStateCommand(Workspace(), Guid.Empty));
                Throws<ArgumentException>(() => new MaintenanceMenuProtocol(Workspace(), Guid.Empty));
            });
            test("Menu runtime normal exit waits for channel close and remote exit proof", () => {
                var protocol = Protocol();
                Equal(MaintenanceMenuRunStatus.Starting, protocol.Snapshot.Status);
                protocol.Feed(Marker("START")); Equal(MaintenanceMenuRunStatus.Running, protocol.Snapshot.Status);
                Equal<int?>(123, protocol.Snapshot.ProcessId); Equal<long?>(456, protocol.Snapshot.ProcessStartTicks);
                Equal<long?>(1793000000, protocol.Snapshot.StartedAtUnixSeconds);
                protocol.Feed(Marker("EXIT", Values + "|0"));
                Equal(MaintenanceMenuRunStatus.AwaitingClose, protocol.Snapshot.Status); Equal(false, protocol.Snapshot.CanCleanup);
                protocol.Complete(); Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status);
                Equal(false, protocol.Snapshot.CanCleanup);
                True(protocol.ApplyRemoteState(State("EXITED"))); True(protocol.Snapshot.CanCleanup);
                True(protocol.ConfirmCleanup(Cleaned())); True(protocol.Snapshot.CleanupCompleted); Equal(false, protocol.Snapshot.CanCleanup);
            });
            foreach (int code in new[] { 1, 7, 65, 129, 130, 143, 255 })
            {
                int value = code;
                test("Menu runtime nonzero foreground return " + code + " is abnormal and can clean after proof", () => {
                    var protocol = Protocol(); protocol.Feed("SUCCESS completed\n" + Marker("START") + Marker("EXIT", Values + "|" + value));
                    protocol.Complete(); Equal(MaintenanceMenuRunStatus.ExitedWithError, protocol.Snapshot.Status);
                    Equal<int?>(value, protocol.Snapshot.ExitCode); Equal(false, protocol.Snapshot.CanCleanup);
                    True(protocol.ApplyRemoteState(State("EXITED", Values, value))); True(protocol.Snapshot.CanCleanup);
                });
            }
            test("Menu runtime text prompts and success words cannot supply state", () => {
                var protocol = Protocol(); protocol.Feed("root@router:~# SUCCESS\nERROR\n请输入选项\n");
                Equal(MaintenanceMenuRunStatus.Starting, protocol.Snapshot.Status);
                protocol.Complete(); Unknown(protocol);
            });
            test("Menu runtime EOF without exit evidence stays unknown", () => {
                var protocol = Protocol(); protocol.Feed(Marker("START") + "last prompt> "); protocol.Complete(); Unknown(protocol);
                True(protocol.ApplyRemoteState(State("UNKNOWN"))); Unknown(protocol);
            });
            test("Menu runtime exit without matching start is a sticky conflict", () => {
                var protocol = Protocol(); protocol.Feed(Marker("EXIT", Values + "|0")); protocol.Complete();
                Unknown(protocol); Equal(false, protocol.ApplyRemoteState(State("EXITED"))); Unknown(protocol);
            });
            test("Menu runtime every two-part split and single-character frame chunk preserves state", () => {
                string text = "中文😀" + Marker("START") + "native menu\n" + Marker("EXIT", Values + "|0", "\u001b\\");
                for (int split = 0; split <= text.Length; split++)
                {
                    var protocol = Protocol(); protocol.Feed(text.Substring(0, split)); protocol.Feed(text.Substring(split)); protocol.Complete();
                    Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status);
                    True(protocol.ApplyRemoteState(State("EXITED"))); True(protocol.Snapshot.CanCleanup);
                }
                var chars = Protocol(); foreach (char c in text) chars.Feed(c.ToString()); chars.Complete();
                Equal(MaintenanceMenuRunStatus.ExitedNormally, chars.Snapshot.Status); Equal(0, chars.BufferedCharacters);
            });
            test("Menu runtime duplicate markers and duplicate cleanup ACK are idempotent", () => {
                var protocol = Protocol(); protocol.Feed(Marker("START") + Marker("START") + Marker("EXIT", Values + "|0") + Marker("EXIT", Values + "|0"));
                protocol.Complete(); protocol.Complete(new IOException("late duplicate close"));
                Equal(2, protocol.Snapshot.DuplicateMarkers); Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status);
                True(protocol.ApplyRemoteState(State("EXITED"))); True(protocol.ConfirmCleanup(Cleaned())); True(protocol.ConfirmCleanup(Cleaned()));
                Equal(false, protocol.ApplyRemoteState(State("RUNNING"))); True(protocol.Snapshot.CleanupCompleted);
                Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status);
            });
            test("Menu runtime wrong task and run markers are ignored", () => {
                var protocol = Protocol();
                protocol.Feed(Marker("START").Replace(TaskId.ToString("N"), new string('a', 32)));
                protocol.Feed(Marker("EXIT", Values + "|99").Replace(RunId.ToString("N"), new string('b', 32)));
                Equal(2, protocol.Snapshot.IgnoredMarkers); Equal(MaintenanceMenuRunStatus.Starting, protocol.Snapshot.Status);
                protocol.Feed(Marker("START") + Marker("EXIT", Values + "|0")); protocol.Complete();
                Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status);
            });
            foreach (string bad in new[] { "START|124|456|1793000000", "START|123|457|1793000000", "START|123|456|1793000001", "EXIT|123|456|1793000000|7" })
            {
                string record = bad;
                test("Menu runtime conflicting duplicate " + record + " cannot authorize cleanup", () => {
                    var protocol = Protocol(); protocol.Feed(Marker("START") + Marker("EXIT", Values + "|0"));
                    protocol.Feed("\u001b]777;" + Wire + "|" + record + "\a"); protocol.Complete(); Unknown(protocol);
                    Equal(false, protocol.ApplyRemoteState(State("EXITED"))); Unknown(protocol);
                });
            }
            foreach (string bad in new[] { "START|0|456|1", "START|-1|456|1", "START|2147483648|456|1", "START|123|-1|1",
                "START|123|456|-1", "START|0123|456|1", "START|123|9223372036854775808|1", "START|123|456|1|extra",
                "EXIT|123|456|1793000000|256", "EXIT|123|456|1793000000|-1", "EXIT|123|456|1793000000|00", "EXIT|123|456|1793000000|0\nother", "OTHER|123|456|1" })
            {
                string record = bad;
                test("Menu runtime malformed owned record " + record.Replace("\n", "\\n"), () => {
                    var protocol = Protocol(); protocol.Feed(Marker("START") + "\u001b]777;" + Wire + "|" + record + "\a");
                    protocol.Complete(); Unknown(protocol); Equal(false, protocol.ApplyRemoteState(State("EXITED")));
                });
            }
            test("Menu runtime unrelated OSC and oversized input keep bounded parser memory", () => {
                var protocol = Protocol();
                protocol.Feed("\u001b]0;" + new string('x', 100000) + "\u001b\\");
                Equal(0, protocol.BufferedCharacters); Equal(MaintenanceMenuRunStatus.Starting, protocol.Snapshot.Status);
                protocol.Feed(Marker("START") + Marker("EXIT", Values + "|0")); protocol.Complete();
                Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status);
                var owned = Protocol(); owned.Feed("\u001b]777;" + Wire + "|START|" + new string('9', 100000));
                True(owned.BufferedCharacters <= MaintenanceMenuProtocol.MaximumFrameCharacters);
                owned.Feed("\a"); owned.Complete(); Unknown(owned);
            });
            test("Menu runtime partial exit at EOF and transport loss can reconcile durable exit", () => {
                foreach (bool partial in new[] { false, true })
                {
                    var protocol = Protocol(); protocol.Feed(Marker("START"));
                    if (partial) protocol.Feed(Marker("EXIT", Values + "|0").TrimEnd('\a'));
                    else protocol.Feed(Marker("EXIT", Values + "|0"));
                    protocol.Complete(partial ? null : new IOException("transport interrupted")); Unknown(protocol);
                    True(protocol.ApplyRemoteState(State("EXITED"))); Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status);
                    True(protocol.Snapshot.CanCleanup);
                }
            });
            test("Menu runtime early remote exit does not bypass output completion barrier", () => {
                var protocol = Protocol();
                True(protocol.ApplyRemoteState(State("EXITED"))); Equal(MaintenanceMenuRunStatus.AwaitingClose, protocol.Snapshot.Status);
                Equal(false, protocol.Snapshot.CanCleanup); Equal(false, protocol.ConfirmCleanup(Cleaned()));
                protocol.Complete(); True(protocol.Snapshot.CanCleanup);
            });
            test("Menu runtime alive process after EOF prevents cleanup until rechecked exited", () => {
                var protocol = Protocol(); protocol.Feed(Marker("START") + Marker("EXIT", Values + "|0")); protocol.Complete();
                True(protocol.ApplyRemoteState(State("RUNNING"))); Unknown(protocol);
                True(protocol.ApplyRemoteState(State("EXITED"))); True(protocol.Snapshot.CanCleanup);
            });
            foreach (string mode in new[] { "task", "run", "version", "extra", "oversize", "failed", "missing" })
            {
                string kind = mode;
                test("Menu runtime invalid state reply " + mode + " cannot authorize cleanup", () => {
                    var protocol = Protocol(); protocol.Feed(Marker("START") + Marker("EXIT", Values + "|0")); protocol.Complete();
                    var reply = State("EXITED");
                    if (kind == "task") reply.Output = reply.Output.Replace(TaskId.ToString("N"), new string('a', 32));
                    if (kind == "run") reply.Output = reply.Output.Replace(RunId.ToString("N"), new string('a', 32));
                    if (kind == "version") reply.Output = reply.Output.Replace("ISTORE_MENU_V1", "ISTORE_MENU_V2");
                    if (kind == "extra") reply.Output += "extra\n";
                    if (kind == "oversize") reply.Output = new string('x', 513);
                    if (kind == "failed") reply.ExitStatus = 76;
                    if (kind == "missing") reply = null;
                    Equal(false, protocol.ApplyRemoteState(reply)); Unknown(protocol);
                    True(protocol.ApplyRemoteState(State("EXITED"))); True(protocol.Snapshot.CanCleanup);
                });
            }
            test("Menu runtime remote identity and exit contradictions stay unknown", () => {
                foreach (var reply in new[] { State("EXITED", "123|457|1793000000"), State("EXITED", Values, 7) })
                {
                    var protocol = Protocol(); protocol.Feed(Marker("START") + Marker("EXIT", Values + "|0")); protocol.Complete();
                    Equal(false, protocol.ApplyRemoteState(reply)); Unknown(protocol);
                    Equal(false, protocol.ApplyRemoteState(State("EXITED")));
                }
            });
            test("Menu runtime cannot be resurrected by output after completion", () => {
                var protocol = Protocol(); protocol.Complete(); protocol.Feed(Marker("START") + Marker("EXIT", Values + "|0")); Unknown(protocol);
                Throws<InvalidOperationException>(() => protocol.Begin());
                var fresh = new MaintenanceMenuProtocol(Workspace(), RunId); fresh.Feed(Marker("START"));
                Equal(MaintenanceMenuRunStatus.NotStarted, fresh.Snapshot.Status); fresh.Begin();
                Equal(MaintenanceMenuRunStatus.Starting, fresh.Snapshot.Status);
            });
            test("Menu runtime cleanup ACK is task bound and detached snapshots do not mutate", () => {
                var protocol = Protocol(); var before = protocol.Snapshot;
                protocol.Feed(Marker("START") + Marker("EXIT", Values + "|0")); protocol.Complete(); True(protocol.ApplyRemoteState(State("EXITED")));
                var bad = Cleaned(); bad.Output = bad.Output.Replace(RunId.ToString("N"), new string('a', 32));
                Equal(false, protocol.ConfirmCleanup(bad)); Equal(false, protocol.Snapshot.CleanupCompleted);
                Equal(MaintenanceMenuRunStatus.Starting, before.Status); True(protocol.ConfirmCleanup(Cleaned())); Equal(false, before.CleanupCompleted);
            });
            test("Menu runtime one-byte UTF8 transport preserves raw output and consumes marker chunks", () => {
                var peer = new WirePeer(); var session = new InteractiveTerminalSession(peer); var protocol = Protocol(); var raw = new StringBuilder();
                string expected = "中文😀" + Marker("START") + "last prompt> " + Marker("EXIT", Values + "|0");
                peer.OnWrite = text => { Equal(MaintenanceMenuCommands.StartPayload(Workspace()), text); peer.Emit(expected); peer.End(); };
                session.Output += text => { raw.Append(text); protocol.Feed(text); }; session.Closed += error => protocol.Complete(error);
                try
                {
                    session.Start(); True(session.Send(MaintenanceMenuCommands.StartPayload(Workspace()))); Wait(session.Completion);
                    Equal(expected, raw.ToString()); Equal(1, peer.Writes); Equal(1, peer.Disposals);
                    Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status); Equal(false, protocol.Snapshot.CanCleanup);
                    var screen = new TerminalScreen(); screen.Feed(raw.ToString()); Equal("中文😀last prompt> ", screen.Text);
                    True(protocol.ApplyRemoteState(State("EXITED"))); True(protocol.Snapshot.CanCleanup);
                }
                finally { Wait(session.StopAsync()); }
            });
        }

        public static void RunShell(Action<string, Action> test)
        {
            Shell(test, "normal short launch records identity and cleans only after EOF plus state proof", f => {
                f.Start("printf '中文😀 final prompt> '\nexit 0\n", 1); f.WaitEnded();
                Equal(MaintenanceMenuRunStatus.ExitedNormally, f.Protocol.Snapshot.Status);
                Equal(false, f.Protocol.Snapshot.CanCleanup); True(f.Output.Contains("中文😀 final prompt> "));
                True(f.Protocol.Snapshot.ProcessId > 0); True(f.Protocol.Snapshot.ProcessStartTicks >= 0); True(f.Protocol.Snapshot.StartedAtUnixSeconds >= 0);
                Equal(f.Protocol.Snapshot.ProcessId.ToString(), File.ReadAllText(f.PathOf("menu.pid")).Trim());
                True(f.Protocol.ApplyRemoteState(f.State())); True(f.Protocol.Snapshot.CanCleanup);
                f.CleanVerified();
            });
            Shell(test, "nonzero menu result and success text remain an abnormal exit", f => {
                f.Start("printf 'SUCCESS words are not a status\\n'\nexit 7\n"); f.WaitEnded();
                Equal(MaintenanceMenuRunStatus.ExitedWithError, f.Protocol.Snapshot.Status); Equal<int?>(7, f.Protocol.Snapshot.ExitCode);
                True(f.Protocol.ApplyRemoteState(f.State())); f.CleanVerified();
            });
            Shell(test, "error text with zero menu return stays normal", f => {
                f.Start("printf 'ERROR fixture text\\n'\nexit 0\n"); f.WaitEnded();
                Equal(MaintenanceMenuRunStatus.ExitedNormally, f.Protocol.Snapshot.Status); True(f.Protocol.ApplyRemoteState(f.State())); f.CleanVerified();
            });
            Shell(test, "foreground stdin survives short exec and cleanup is refused while active", f => {
                f.Start("printf 'INPUT_READY'\nIFS= read -r answer || exit 9\nprintf 'received:%s\\n' \"$answer\"\nexit 0\n");
                Until(() => f.Output.Contains("INPUT_READY"));
                True(f.Protocol.ApplyRemoteState(f.State())); Equal(MaintenanceMenuRunStatus.Running, f.Protocol.Snapshot.Status);
                Equal(false, f.Protocol.Snapshot.CanCleanup); True(f.Cleanup().ExitStatus != 0); True(File.Exists(f.PathOf(".owner")));
                True(f.Session.Send("选项123\n")); f.WaitEnded(); True(f.Output.Contains("received:选项123"));
                True(f.Protocol.ApplyRemoteState(f.State())); f.CleanVerified();
            });
            Shell(test, "large chunked final output is observed before exit and cleanup eligibility", f => {
                f.Start("i=0\nwhile test \"$i\" -lt 1400; do printf 'xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx'; i=$((i+1)); done\nprintf '\\n最后一行😀'\nexit 0\n", 17);
                f.WaitEnded(); True(f.Output.Contains("\n最后一行😀")); Equal(89600, f.Output.Count(c => c == 'x'));
                True(f.Protocol.Snapshot.ChannelClosed); Equal(false, f.Protocol.Snapshot.CanCleanup);
                True(f.Protocol.ApplyRemoteState(f.State())); f.CleanVerified();
            });
            Shell(test, "killed wrapper has no durable exit and never cleans task", f => {
                string script = "kill -KILL \"$(cat " + Q(f.PathOf("menu.pid")) + ")\"\nexit 0\n";
                f.Start(script); f.WaitEnded(); Unknown(f.Protocol); Equal(false, File.Exists(f.PathOf("menu.exit")));
                True(f.Protocol.ApplyRemoteState(f.State())); Unknown(f.Protocol); True(f.Cleanup().ExitStatus != 0); True(File.Exists(f.PathOf(".owner")));
            });
            Shell(test, "TERM interruption never emits proof that foreground menu returned", f => {
                f.Start("kill -TERM \"$(cat " + Q(f.PathOf("menu.pid")) + ")\"\nexit 0\n"); f.WaitEnded();
                Unknown(f.Protocol); Equal(false, File.Exists(f.PathOf("menu.exit")));
                True(f.Protocol.ApplyRemoteState(f.State())); Unknown(f.Protocol);
                True(f.Cleanup().ExitStatus != 0); True(File.Exists(f.PathOf("menu.sh")));
            });
            Shell(test, "second launch cannot replace active PID or run a second menu", f => {
                f.Start("printf 'READY'\nIFS= read -r answer || exit 9\nexit 0\n"); Until(() => f.Output.Contains("READY"));
                string pid = File.ReadAllText(f.PathOf("menu.pid"));
                True(f.Exec(f.Rebase(MaintenanceMenuCommands.StartCommand(f.Workspace))).ExitStatus != 0);
                Equal(pid, File.ReadAllText(f.PathOf("menu.pid"))); True(f.Session.Send("exit\n")); f.WaitEnded();
                True(f.Protocol.ApplyRemoteState(f.State())); f.CleanVerified();
            });
            Shell(test, "concurrent launches have exactly one atomic PID claimant", f => {
                f.PrepareMenu("printf 'WINNER\\n'\nexit 0\n");
                string command = f.Rebase(MaintenanceMenuCommands.StartCommand(f.Workspace));
                var first = Task.Run(() => f.Exec(command)); var second = Task.Run(() => f.Exec(command)); Wait(Task.WhenAll(first, second));
                Equal(1, new[] { first.Result, second.Result }.Count(r => r.ExitStatus == 0));
                var protocol = new MaintenanceMenuProtocol(f.Workspace, f.Run); protocol.Begin(); protocol.Complete();
                True(protocol.ApplyRemoteState(f.State())); Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status);
                True(protocol.Snapshot.CanCleanup);
                var cleaned = f.Cleanup(); Equal(0, cleaned.ExitStatus); True(protocol.ConfirmCleanup(cleaned));
            });
            Shell(test, "prelaunch task is not eligible for finished cleanup", f => {
                f.PrepareMenu("exit 0\n");
                var protocol = new MaintenanceMenuProtocol(f.Workspace, f.Run); protocol.Begin(); protocol.Complete();
                True(protocol.ApplyRemoteState(f.State())); Unknown(protocol); True(f.Cleanup().ExitStatus != 0); True(File.Exists(f.PathOf("menu.sh")));
            });
            Shell(test, "invalid menu syntax never claims PID or manufactures exit proof", f => {
                f.Start("if then\nexit 0\n"); f.WaitEnded(); Unknown(f.Protocol);
                Equal(false, File.Exists(f.PathOf("menu.pid"))); Equal(false, File.Exists(f.PathOf("menu.exit")));
                True(f.Protocol.ApplyRemoteState(f.State())); Unknown(f.Protocol); True(f.Cleanup().ExitStatus != 0);
            });
            foreach (string fault in new[] { "missing", "partial", "wrong-task", "wrong-run", "wrong-owner", "temporary", "symlink" })
            {
                string kind = fault;
                Shell(test, "exit evidence " + fault + " blocks cleanup", f => {
                    f.Start("exit 0\n"); f.WaitEnded();
                    if (kind == "missing") File.Delete(f.PathOf("menu.exit"));
                    if (kind == "partial") File.WriteAllText(f.PathOf("menu.exit"), "partial");
                    if (kind == "wrong-task") File.WriteAllText(f.PathOf("menu.exit"), File.ReadAllText(f.PathOf("menu.exit")).Replace(f.Workspace.OperationId.ToString("N"), new string('a', 32)));
                    if (kind == "wrong-run") File.WriteAllText(f.PathOf("menu.exit"), File.ReadAllText(f.PathOf("menu.exit")).Replace(f.Run.ToString("N"), new string('a', 32)));
                    if (kind == "wrong-owner") File.WriteAllText(f.PathOf(".owner"), new string('a', 32));
                    if (kind == "temporary") File.WriteAllText(f.PathOf("menu.exit.tmp"), "partial");
                    if (kind == "symlink")
                    {
                        string outside = System.IO.Path.Combine(f.Root, "exit-outside");
                        File.WriteAllText(outside, File.ReadAllText(f.PathOf("menu.exit"))); File.Delete(f.PathOf("menu.exit"));
                        Equal(0, f.Exec("ln -s " + Q(outside.Replace('\\', '/')) + " " + Q(f.PathOf("menu.exit")) + "\ntest -L " + Q(f.PathOf("menu.exit"))).ExitStatus);
                    }
                    f.Protocol.ApplyRemoteState(f.State()); Unknown(f.Protocol);
                    True(f.Cleanup().ExitStatus != 0); True(File.Exists(f.PathOf(".owner"))); True(File.Exists(f.PathOf("menu.sh")));
                });
            }
            foreach (string fault in new[] { "unknown", "hidden", "payload-link" })
            {
                string kind = fault;
                Shell(test, "cleanup guard retains all evidence with " + fault, f => {
                    f.Start("exit 0\n"); f.WaitEnded(); True(f.Protocol.ApplyRemoteState(f.State())); True(f.Protocol.Snapshot.CanCleanup);
                    if (kind == "unknown") File.WriteAllText(f.PathOf("keep.txt"), "sentinel");
                    if (kind == "hidden") File.WriteAllText(f.PathOf("..keep"), "sentinel");
                    if (kind == "payload-link")
                    {
                        string outside = System.IO.Path.Combine(f.Root, "outside"); File.WriteAllText(outside, "sentinel"); File.Delete(f.PathOf("menu.sh"));
                        Equal(0, f.Exec("ln -s " + Q(outside.Replace('\\', '/')) + " " + Q(f.PathOf("menu.sh")) + "\ntest -L " + Q(f.PathOf("menu.sh"))).ExitStatus);
                    }
                    var cleanup = f.Cleanup(); True(cleanup.ExitStatus != 0); Equal(false, f.Protocol.ConfirmCleanup(cleanup));
                    True(File.Exists(f.PathOf(".owner"))); True(File.Exists(f.PathOf("menu.exit")));
                });
            }
            Shell(test, "live matching PID prevents cleanup despite an exit file", f => {
                f.PrepareMenu("exit 0\n");
                string command = f.Rebase(MaintenanceMenuCommands.ReadStateCommand(f.Workspace, f.Run));
                int cut = command.IndexOf("\nload_state || exit 76\n", StringComparison.Ordinal);
                string seed = "\npid=$$\nticks=$(process_identity $$ | cut -d '|' -f 2)\nstarted=1\n" + SeedState() +
                    "load_state\ntest \"$state\" = RUNNING || exit 98\n";
                string body = command.Substring(0, cut) + seed + "if (\n" + f.Rebase(MaintenanceMenuCommands.CleanupFinishedCommand(f.Workspace, f.Run)) +
                    "\n); then exit 97; fi\ntest -f \"$owner\"\n";
                Equal(0, f.Exec(body).ExitStatus);
            });
            Shell(test, "unavailable proc evidence remains unknown despite complete exit files", f => {
                f.Start("exit 0\n"); f.WaitEnded();
                string command = f.Rebase(MaintenanceMenuCommands.ReadStateCommand(f.Workspace, f.Run));
                int cut = command.IndexOf("\nload_state || exit 76\n", StringComparison.Ordinal);
                True(f.Protocol.ApplyRemoteState(f.Exec(command.Insert(cut, "\nprocess_identity() { return 1; }\n"))));
                Unknown(f.Protocol);
                command = f.Rebase(MaintenanceMenuCommands.CleanupFinishedCommand(f.Workspace, f.Run));
                cut = command.IndexOf("\nload_state || exit 76\n", StringComparison.Ordinal);
                True(f.Exec(command.Insert(cut, "\nprocess_identity() { return 1; }\n")).ExitStatus != 0);
                True(File.Exists(f.PathOf(".owner"))); True(File.Exists(f.PathOf("menu.exit")));
            });
            Shell(test, "reused PID with different start ticks is not the old active process", f => {
                f.PrepareMenu("exit 0\n");
                string command = f.Rebase(MaintenanceMenuCommands.ReadStateCommand(f.Workspace, f.Run));
                int cut = command.IndexOf("\nload_state || exit 76\n", StringComparison.Ordinal);
                string seed = "\npid=$$\nticks=0\nstarted=1\n" + SeedState();
                var reply = f.Exec(command.Insert(cut, seed));
                var protocol = new MaintenanceMenuProtocol(f.Workspace, f.Run); protocol.Begin(); protocol.Complete();
                True(protocol.ApplyRemoteState(reply)); Equal(MaintenanceMenuRunStatus.ExitedNormally, protocol.Snapshot.Status);
                True(protocol.Snapshot.CanCleanup); var cleanup = f.Cleanup(); Equal(0, cleanup.ExitStatus); True(protocol.ConfirmCleanup(cleanup));
            });
            Shell(test, "neighbor tasks remain intact after verified cleanup", f => {
                f.Start("exit 0\n"); f.WaitEnded(); True(f.Protocol.ApplyRemoteState(f.State()));
                string neighbor = System.IO.Path.Combine(f.Root, "remote", "maintenance-" + new string('b', 32));
                Directory.CreateDirectory(neighbor); File.WriteAllText(System.IO.Path.Combine(neighbor, ".owner"), "sentinel");
                f.CleanVerified(); Equal("sentinel", File.ReadAllText(System.IO.Path.Combine(neighbor, ".owner")));
            });
        }

        private static string SeedState()
        {
            return "printf '%s\\n' \"$pid\" > \"$pid_file\"\nprintf '%s|START|%s|%s|%s\\n' \"$wire\" \"$pid\" \"$ticks\" \"$started\" > \"$start_file\"\n" +
                "printf '%s|EXIT|%s|%s|%s|0\\n' \"$wire\" \"$pid\" \"$ticks\" \"$started\" > \"$exit_file\"\n";
        }
        private static void Shell(Action<string, Action> test, string name, Action<ShellFixture> action)
        { test("Menu runtime shell " + name, () => { using (var fixture = new ShellFixture()) action(fixture); }); }
        internal static void RunNetworkShell(Action<string, Action> test)
        {
            foreach (var path in new[] { MaintenanceNetworkPath.Direct, MaintenanceNetworkPath.ComputerProxy }) { var mode = path; test("Menu shell network environment reaches child only: " + mode, () => {
                using (var fixture = new ShellFixture(mode)) {
                    fixture.PrepareMenu("printf 'MENU=%s|%s|%s|%s|%s\\n' \"${http_proxy-}\" \"${https_proxy-}\" \"${HTTP_PROXY-}\" \"${HTTPS_PROXY-}\" \"${NO_PROXY-}\"\nsh -c 'printf \"CHILD=%s|%s|%s|%s|%s\\n\" \"${http_proxy-}\" \"${https_proxy-}\" \"${HTTP_PROXY-}\" \"${HTTPS_PROXY-}\" \"${NO_PROXY-}\"'\nexit 0\n");
                    var result = fixture.Exec("http_proxy='parent-sentinel'; export http_proxy\nsh " + Q(fixture.PathOf("helper-launcher.sh")) + "\nprintf 'PARENT=%s\\n' \"$http_proxy\"\n");
                    Equal(0, result.ExitStatus); string values = mode == MaintenanceNetworkPath.ComputerProxy ? "http://127.0.0.1:17890|http://127.0.0.1:17890|http://127.0.0.1:17890|http://127.0.0.1:17890|" : "||||*";
                    True(result.Output.Contains("MENU=" + values)); True(result.Output.Contains("CHILD=" + values)); True(result.Output.Contains("PARENT=parent-sentinel"));
                }
            }); }
        }
        private sealed class ShellFixture : IDisposable
        {
            public readonly string Root, LocalBase, DirectoryPath;
            public readonly RemoteMaintenanceWorkspace Workspace = RemoteMaintenanceWorkspace.CreateMenu(Guid.NewGuid(), true);
            public readonly Guid Run = Guid.NewGuid();
            public MaintenanceMenuProtocol Protocol;
            public InteractiveTerminalSession Session;
            private readonly object outputLock = new object();
            private readonly StringBuilder output = new StringBuilder();
            private string menuHash;
            private ProcessTransport transport;
            public string Output { get { lock (outputLock) return output.ToString(); } }
            public ShellFixture(MaintenanceNetworkPath? networkPath = null)
            {
                string repository = Repository();
                string allowed = System.IO.Path.GetFullPath(System.IO.Path.Combine(repository, "artifacts", "menu-runtime-phase3", "shell-fixtures")) + System.IO.Path.DirectorySeparatorChar;
                Root = System.IO.Path.GetFullPath(System.IO.Path.Combine(allowed, Guid.NewGuid().ToString("N")));
                if (!Root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture escaped workspace.");
                System.IO.Directory.CreateDirectory(Root); LocalBase = System.IO.Path.Combine(Root, "remote").Replace('\\', '/');
                DirectoryPath = Rebase(Workspace.Directory);
                Equal(0, Exec(Rebase(Workspace.CreateCommand())).ExitStatus);
                File.WriteAllText(PathOf("LICENSE"), "inert fixture"); File.WriteAllText(PathOf("NOTICE.txt"), "inert fixture");
                byte[] launcher = Encoding.UTF8.GetBytes(Rebase(new UTF8Encoding(false, true).GetString(MaintenanceMenuCommands.LauncherBytes(Workspace, Run, networkPath))));
                File.WriteAllBytes(PathOf("helper-launcher.sh"), launcher);
            }
            public string PathOf(string file) { return DirectoryPath + "/" + file; }
            public string Rebase(string value) { return value.Replace(RemoteMaintenanceWorkspace.BaseDirectory, LocalBase); }
            public void PrepareMenu(string body)
            {
                File.WriteAllText(PathOf("menu.sh"), "#!/bin/sh\n" + body, new UTF8Encoding(false));
                menuHash = MaintenanceScriptIntegrity.HashFile(PathOf("menu.sh"));
            }
            public void Start(string body, int maximumRead = 8192)
            {
                PrepareMenu(body); Protocol = new MaintenanceMenuProtocol(Workspace, Run); Protocol.Begin();
                transport = new ProcessTransport(Root, maximumRead);
                Session = new InteractiveTerminalSession(transport);
                Session.Output += text => { lock (outputLock) output.Append(text); Protocol.Feed(text); };
                Session.Closed += error => Protocol.Complete(error);
                Session.Start(); True(Session.Send(Rebase(MaintenanceMenuCommands.StartPayload(Workspace))));
            }
            public void WaitEnded()
            {
                Wait(Session.Completion);
                File.WriteAllText(System.IO.Path.Combine(Root, "terminal.txt"), Output, new UTF8Encoding(false));
                Equal(menuHash, MaintenanceScriptIntegrity.HashFile(PathOf("menu.sh")));
                Equal(Rebase(MaintenanceMenuCommands.StartPayload(Workspace)), transport.FirstWrite);
            }
            public ProbeResult State() { return Exec(Rebase(MaintenanceMenuCommands.ReadStateCommand(Workspace, Run))); }
            public ProbeResult Cleanup() { return Exec(Rebase(MaintenanceMenuCommands.CleanupFinishedCommand(Workspace, Run))); }
            public void CleanVerified()
            {
                True(Protocol.Snapshot.ChannelClosed && Protocol.Snapshot.CanCleanup);
                var cleaned = Cleanup(); Equal(0, cleaned.ExitStatus); True(Protocol.ConfirmCleanup(cleaned));
                Equal(false, System.IO.Directory.Exists(DirectoryPath)); True(Protocol.Snapshot.CleanupCompleted);
            }
            public ProbeResult Exec(string body)
            {
                if (body.Contains(RemoteMaintenanceWorkspace.BaseDirectory)) throw new InvalidOperationException("Unrebased device path.");
                string script = System.IO.Path.Combine(Root, "command-" + Guid.NewGuid().ToString("N") + ".sh");
                File.WriteAllText(script, "#!/bin/sh\n" + body, new UTF8Encoding(false));
                var start = StartInfo("\"" + script.Replace('\\', '/') + "\"", Root);
                using (var process = Process.Start(start))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Local command timed out."); }
                    var result = new ProbeResult { ExitStatus = process.ExitCode, Output = stdout.GetAwaiter().GetResult(), Error = stderr.GetAwaiter().GetResult() };
                    File.WriteAllText(script + ".stdout", result.Output, new UTF8Encoding(false)); File.WriteAllText(script + ".stderr", result.Error, new UTF8Encoding(false));
                    return result;
                }
            }
            public void Dispose() { if (Session != null) Wait(Session.StopAsync()); }
        }
        private sealed class ProcessTransport : IShellTransport
        {
            private readonly Process process;
            private readonly Stream stdout, stdin;
            private readonly Task<string> stderr;
            private readonly int maximumRead;
            private int disposed, writes;
            public int Writes { get { return Volatile.Read(ref writes); } }
            public string FirstWrite { get; private set; }
            public ProcessTransport(string root, int maximumRead)
            {
                this.maximumRead = maximumRead;
                var start = StartInfo("", root); start.RedirectStandardInput = true; process = Process.Start(start);
                stdout = process.StandardOutput.BaseStream; stdin = process.StandardInput.BaseStream; stderr = process.StandardError.ReadToEndAsync();
            }
            public int Read(byte[] buffer) { return stdout.Read(buffer, 0, Math.Min(buffer.Length, maximumRead)); }
            public void Write(byte[] buffer) { if (Interlocked.Increment(ref writes) == 1) FirstWrite = Encoding.UTF8.GetString(buffer); stdin.Write(buffer, 0, buffer.Length); stdin.Flush(); }
            public void Resize(uint columns, uint rows) { }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                try { stdin.Dispose(); } catch (IOException) { }
                if (!process.WaitForExit(2000)) { process.Kill(); process.WaitForExit(); }
                stderr.GetAwaiter().GetResult(); stdout.Dispose(); process.Dispose();
            }
        }
        private static ProcessStartInfo StartInfo(string arguments, string root)
        {
            string bash = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
            if (!File.Exists(bash)) throw new FileNotFoundException("Shell fixtures require Git Bash.", bash);
            var start = new ProcessStartInfo(bash, "--noprofile --norc " + arguments)
            { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.EnvironmentVariables["MSYS"] = "winsymlinks:nativestrict"; return start;
        }
        private sealed class WirePeer : IShellTransport
        {
            private readonly BlockingCollection<byte[]> output = new BlockingCollection<byte[]>();
            private byte[] current; private int offset;
            public int Writes, Disposals; public Action<string> OnWrite;
            public void Emit(string text) { output.Add(Encoding.UTF8.GetBytes(text)); }
            public void End() { output.CompleteAdding(); }
            public int Read(byte[] buffer)
            {
                if (current == null || offset == current.Length)
                { try { current = output.Take(); offset = 0; } catch (InvalidOperationException) { return 0; } }
                buffer[0] = current[offset++]; return 1;
            }
            public void Write(byte[] bytes) { Interlocked.Increment(ref Writes); OnWrite(Encoding.UTF8.GetString(bytes)); }
            public void Resize(uint columns, uint rows) { }
            public void Dispose() { if (Interlocked.Increment(ref Disposals) == 1) output.CompleteAdding(); }
        }
        private static string Repository()
        {
            for (var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); directory != null; directory = directory.Parent)
                if (File.Exists(System.IO.Path.Combine(directory.FullName, "iStoreOS.PluginHelper.sln"))) return directory.FullName;
            throw new DirectoryNotFoundException("V1 root not found.");
        }
        private static string Q(string value) { return ShellArgument.Quote(value); }
        private static void Unknown(MaintenanceMenuProtocol protocol)
        { Equal(MaintenanceMenuRunStatus.Unknown, protocol.Snapshot.Status); Equal(false, protocol.Snapshot.CanCleanup); Equal<int?>(null, protocol.Snapshot.ExitCode); }
        private static void Wait(Task task)
        { if (!task.Wait(15000)) throw new TimeoutException("Local fixture completion timed out."); task.GetAwaiter().GetResult(); }
        private static void Until(Func<bool> condition)
        { var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.ElapsedMilliseconds > 10000) throw new TimeoutException("Local fixture checkpoint timed out."); Thread.Sleep(5); } }
        private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
        private static void True(bool value) { Equal(true, value); }
        private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    }
}

