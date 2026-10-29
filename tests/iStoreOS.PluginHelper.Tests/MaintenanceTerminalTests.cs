using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Controls;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    // Ported terminal-only V2 regressions. Every transport is in memory.
    internal static partial class MaintenanceTerminalTests
    {
        private static Action<string, Action> run;
        private static readonly List<Exception> uiErrors = new List<Exception>();
        public static void Run(Action<string, Action> test)
        {
            run = test;
            ThreadExceptionEventHandler handler = (sender, args) => uiErrors.Add(args.Exception);
            Application.ThreadException += handler;
            try { CoreTests(); TerminalInteractionTests(); EdgeCases(); }
            finally { Application.ThreadException -= handler; run = null; uiErrors.Clear(); }
        }
        private static void T(string name, Action action)
        {
            run("Terminal " + name, () => {
                try
                {
                    action(); Application.DoEvents();
                    if (uiErrors.Count > 0) throw new AggregateException("Asynchronous terminal UI failure", uiErrors.ToArray());
                }
                finally { uiErrors.Clear(); }
            });
        }
        private static void EdgeCases()
        {
            T("UTF-8 outbound text and emoji reach peer unchanged", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer);
                try
                {
                    shell.Start(); A(shell.Send("中文输入😀\n"));
                    Until(() => peer.Input == "中文输入😀\n");
                }
                finally { W(shell.StopAsync()); }
            });
            T("UTF-8 one-byte reads finish at EOF with no error", () => {
                var peer = new Peer { OneByte = true }; var shell = new InteractiveTerminalSession(peer);
                var output = new StringBuilder(); Exception failure = null;
                shell.Output += value => output.Append(value); shell.Closed += error => failure = error;
                try
                {
                    shell.Start(); peer.Emit("分块终端😀\r\n最后无换行"); peer.End(); W(shell.Completion);
                    E("分块终端😀\r\n最后无换行", output.ToString()); A(failure == null); E(1, peer.Disposals);
                }
                finally { W(shell.StopAsync()); }
            });
            T("Truncated UTF-8 sequence is flushed once at EOF", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer); var output = new StringBuilder();
                shell.Output += value => output.Append(value);
                try
                {
                    shell.Start(); peer.Emit("保留前文"); peer.EmitBytes(new byte[] { 0xe4, 0xb8 }); peer.End(); W(shell.Completion);
                    E("保留前文\ufffd", output.ToString()); E(1, peer.Disposals);
                }
                finally { W(shell.StopAsync()); }
            });
            T("Malformed UTF-8 prefix before a full read cannot overflow decoder buffer", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer); var output = new StringBuilder(); Exception failure = null;
                shell.Output += value => output.Append(value); shell.Closed += error => failure = error;
                try
                {
                    shell.Start(); peer.EmitBytes(new byte[] { 0xe4 }); peer.Emit(new string('A', 8192)); peer.End(); W(shell.Completion);
                    if (failure != null) throw new Exception("UTF-8 stream failed instead of replacing malformed prefix: " + failure.GetType().Name + ": " + failure.Message, failure);
                    E("\ufffd" + new string('A', 8192), output.ToString());
                }
                finally { W(shell.StopAsync()); }
            });
            T("Typed Chinese and Enter execute once in offline fake shell", () => {
                using (var f = new TerminalFixture(new LocalFakeShell()))
                {
                    Until(() => f.View.Text.EndsWith("fake-shell $ ", StringComparison.Ordinal));
                    foreach (char c in "中文A123") Character(f.View, c);
                    Key(f.View, Keys.Enter);
                    Until(() => f.View.Text.Contains("[local] received: 中文A123") && f.View.Text.EndsWith("fake-shell $ ", StringComparison.Ordinal));
                    E(1, System.Text.RegularExpressions.Regex.Matches(f.View.Text, @"\[local\] received:").Count);
                    A(f.View.ReadOnly);
                }
            });
            T("CRLF split across fake writes executes one command", () => {
                using (var f = new TerminalFixture(new LocalFakeShell()))
                {
                    f.View.SendInput("first\r"); f.View.SendInput("\nsecond\n");
                    Until(() => f.View.Text.Contains("[local] received: second") && f.View.Text.EndsWith("fake-shell $ ", StringComparison.Ordinal));
                    E(2, System.Text.RegularExpressions.Regex.Matches(f.View.Text, @"\[local\] received:").Count);
                    C(f.View.Text, "[local] received: first");
                }
            });
            T("EOF drain handles multiple capped batches and unterminated final prompt", () => {
                using (var f = new TerminalFixture())
                {
                    // Freeze automatic paints so DrainOutputAsync itself must consume several batches.
                    var timer = (System.Windows.Forms.Timer)typeof(TerminalView).GetField("flushTimer", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(f.View);
                    timer.Stop();
                    string text = string.Concat(Enumerable.Range(0, 1000).Select(i => i.ToString("D4") + new string('x', 180) + "\r\n")) + "最终提示😀> ";
                    f.Peer.Emit(text); f.Peer.End(); W(f.Session.Completion);
                    E(0L, f.View.DisplayedCharacterCount); A(f.View.PendingChunkCount > 1);
                    W(f.View.DrainOutputAsync(f.Session));
                    E(text.Replace("\r\n", "\n"), f.View.Text); E(0, f.View.PendingChunkCount);
                    E((long)text.Length, f.View.ReceivedCharacterCount); E(f.View.ReceivedCharacterCount, f.View.DisplayedCharacterCount);
                    A(f.View.FlushCount >= 2); A(f.View.InputPositionVisible);
                }
            });
            T("Native resize reaches active transport dimensions", () => {
                using (var f = new TerminalFixture())
                {
                    f.Window.ClientSize = new System.Drawing.Size(800, 300);
                    uint columns = f.View.Columns, rows = f.View.Rows;
                    Until(() => f.Peer.Columns == columns && f.Peer.Rows == rows);
                }
            });
            T("Send before Start and after EOF is rejected", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer);
                try
                {
                    A(!shell.Send("before")); A(!shell.Resize(80, 24)); shell.Start(); peer.End(); W(shell.Completion);
                    A(!shell.Send("after")); A(!shell.Resize(80, 24)); E("", peer.Input);
                }
                finally { W(shell.StopAsync()); }
            });
            T("Concurrent Stop joins completion and disposes once", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer); shell.Start();
                W(Task.WhenAll(shell.StopAsync(), shell.StopAsync(), shell.StopAsync()));
                E(1, peer.Disposals); A(shell.Completion.IsCompleted);
            });
            T("Bounded input queue rejects overflow without blocking caller", () => {
                var peer = new Peer { BlockWrite = true }; var shell = new InteractiveTerminalSession(peer); shell.Start();
                try
                {
                    bool rejected = false;
                    for (int i = 0; i < 2048; i++) if (!shell.Send("x")) { rejected = true; break; }
                    A(rejected);
                }
                finally { W(shell.StopAsync()); }
            });
        }
        static void CoreTests()
        {
            T("Shell forwards original output and arbitrary future menu prompts", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer); var text = new StringBuilder();
                shell.Output += s => { lock(text) text.Append(s); }; shell.Start();
                string raw = "==> native\r\n[WARN] raw\r\n[ERROR] raw\r\n请输入任意新菜单选项:"; peer.Emit(raw);
                Until(() => { lock(text) return text.ToString() == raw; }); W(shell.StopAsync());
            });
            T("UTF-8 output survives one-byte read boundaries", () => {
                var peer = new Peer { OneByte = true }; var shell = new InteractiveTerminalSession(peer); var text = new StringBuilder();
                shell.Output += s => { lock(text) text.Append(s); }; shell.Start(); peer.Emit("中文终端😀");
                Until(() => { lock(text) return text.ToString() == "中文终端😀"; }); W(shell.StopAsync());
            });
            T("Input ordering preserves Enter / Backspace / Ctrl+C bytes", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer); shell.Start();
                foreach(string s in new[] { "aZ09", "\n", "\u007f", "\u0003", "\t", "\u001b[A" }) A(shell.Send(s));
                Until(() => peer.Input == "aZ09\n\u007f\u0003\t\u001b[A"); W(shell.StopAsync());
            });
            T("PTY resize uses background command queue", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer); shell.Start(); A(shell.Resize(120,40));
                Until(() => peer.Columns == 120); E(40U, peer.Rows); W(shell.StopAsync());
            });
            T("Blocked write never blocks UI caller", () => {
                var peer = new Peer { BlockWrite = true }; var shell = new InteractiveTerminalSession(peer); shell.Start();
                var watch = Stopwatch.StartNew(); A(shell.Send("x")); A(watch.ElapsedMilliseconds < 100); W(shell.StopAsync());
            });
            T("EOF closes transport exactly once", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer); int closed = 0;
                shell.Closed += error => Interlocked.Increment(ref closed); shell.Start(); peer.End(); W(shell.Completion);
                E(1, closed); E(1, peer.Disposals); A(!shell.Send("x"));
            });
            T("Read failure reports disconnect", () => {
                var peer = new Peer { FailRead = true }; var shell = new InteractiveTerminalSession(peer); Exception failure = null;
                shell.Closed += error => failure = error; shell.Start(); W(shell.Completion); A(failure is IOException); E(1, peer.Disposals);
            });
            T("Write failure releases blocked reader", () => {
                var peer = new Peer { FailWrite = true }; var shell = new InteractiveTerminalSession(peer); Exception failure = null;
                shell.Closed += error => failure = error; shell.Start(); shell.Send("x"); W(shell.Completion); A(failure is IOException);
            });
            T("Stop unblocks read and is idempotent", () => {
                var peer = new Peer(); var shell = new InteractiveTerminalSession(peer); shell.Start();
                W(shell.StopAsync()); W(shell.StopAsync()); E(1, peer.Disposals); A(shell.Completion.IsCompleted);
            });
            T("Start twice is rejected", () => { var shell = new InteractiveTerminalSession(new Peer()); shell.Start(); X<InvalidOperationException>(() => shell.Start()); W(shell.StopAsync()); });
            T("Stop before start disposes transport", () => { var peer = new Peer(); var shell = new InteractiveTerminalSession(peer); W(shell.StopAsync()); E(1, peer.Disposals); X<ObjectDisposedException>(() => shell.Start()); });
            T("Enter produces LF", () => E("\n", TerminalKeyboard.Encode(Keys.Enter)));
            T("Backspace produces DEL", () => E("\u007f", TerminalKeyboard.Encode(Keys.Back)));
            T("Ctrl+C produces interrupt", () => E("\u0003", TerminalKeyboard.Encode(Keys.Control | Keys.C)));
            T("Arrows and Tab produce native escape bytes", () => { E("\u001b[A", TerminalKeyboard.Encode(Keys.Up)); E("\u001b[B", TerminalKeyboard.Encode(Keys.Down)); E("\u001b[C", TerminalKeyboard.Encode(Keys.Right)); E("\u001b[D", TerminalKeyboard.Encode(Keys.Left)); E("\t", TerminalKeyboard.Encode(Keys.Tab)); });
            T("Additional control keys remain available", () => { E("\u0004", TerminalKeyboard.Encode(Keys.Control | Keys.D)); E("\u001b[3~", TerminalKeyboard.Encode(Keys.Delete)); E("\u001bOP", TerminalKeyboard.Encode(Keys.F1)); });
            T("Split ANSI SGR is safely filtered", () => { var s = new TerminalScreen(); s.Feed("\u001b[3"); s.Feed("1m[WARN]\u001b[0m"); E("[WARN]", s.Text); });
            T("CR overwrites progress", () => { var s = new TerminalScreen(); s.Feed("Downloading 10%\rDownloading 90%"); E("Downloading 90%", s.Text); });
            T("Backspace and erase line render remote echo", () => { var s = new TerminalScreen(); s.Feed("abc\b \b"); E(2,s.CursorOffset); s.Feed("\r\u001b[2KabZ"); E("abZ",s.Text); });
            T("Cursor movement supports shell editing", () => { var s = new TerminalScreen(); s.Feed("abc\u001b[2DZ"); E("aZc",s.Text); });
            T("OSC title is filtered across chunks", () => { var s = new TerminalScreen(); s.Feed("a\u001b]0;title"); s.Feed("\u001b\\b"); E("ab",s.Text); });
            T("Clear and home redraw menu", () => { var s = new TerminalScreen(); s.Feed("old\r\nold\u001b[2J\u001b[Hnew"); E("new",s.Text); });
            T("Bounded scrollback retains newest lines", () => { var s = new TerminalScreen(); for(int i=0;i<4500;i++) s.Feed("line "+i+"\r\n"); A(s.Text.Split('\n').Length <= TerminalScreen.MaximumLines); C(s.Text,"line 4499"); });
        }

        static void Key(TerminalView view,Keys key) { var message=Message.Create(view.Handle,0x100,IntPtr.Zero,IntPtr.Zero); typeof(TerminalView).GetMethod("ProcessCmdKey",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(view,new object[] { message,key }); }
        static void Character(TerminalView view,char c) { typeof(TerminalView).GetMethod("OnKeyPress",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(view,new object[] { new KeyPressEventArgs(c) }); }
        static T R<T>(Task<T> task) { W(task); return task.GetAwaiter().GetResult(); }
        static void W(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        static void Until(Func<bool> condition) { var watch=Stopwatch.StartNew(); while(!condition()) { if(watch.ElapsedMilliseconds>5000) throw new Exception("Timed out"); Application.DoEvents(); Thread.Sleep(3); } }
        static void Pump(int ms) { var watch=Stopwatch.StartNew(); while(watch.ElapsedMilliseconds<ms) { Application.DoEvents(); Thread.Sleep(3); } }
        static void E<T>(T expected,T actual) { if(!object.Equals(expected,actual)) throw new Exception("Expected "+expected+", actual "+actual); }
        static void A(bool value) { if(!value) throw new Exception("Expected true"); }
        static void C(string value,string part) { if(!value.Contains(part)) throw new Exception("Missing: "+part); }
        static void X<T>(Action action) where T:Exception { try { action(); } catch(T) { return; } throw new Exception("Expected "+typeof(T).Name); }

        sealed class Peer:IShellTransport
        {
            readonly BlockingCollection<byte[]> output=new BlockingCollection<byte[]>(); readonly StringBuilder input=new StringBuilder(); readonly ManualResetEventSlim release=new ManualResetEventSlim();
            byte[] current; int offset,disposals; public bool OneByte,BlockWrite,FailRead,FailWrite; public uint Columns,Rows;
            public int Disposals { get { return disposals; } } public string Input { get { lock(input) return input.ToString(); } }
            public void Emit(string text) { EmitBytes(Encoding.UTF8.GetBytes(text)); } public void EmitBytes(byte[] bytes) { output.Add(bytes); } public void End() { output.CompleteAdding(); }
            public int Read(byte[] buffer) {
                if(FailRead) throw new IOException("fake read failure");
                if(current==null || current.Length==offset) { try { current=output.Take(); offset=0; } catch(InvalidOperationException) { return 0; } }
                int count=Math.Min(OneByte?1:buffer.Length,current.Length-offset); Buffer.BlockCopy(current,offset,buffer,0,count); offset+=count; return count;
            }
            public void Write(byte[] bytes) { if(BlockWrite) release.Wait(); if(FailWrite) throw new IOException("fake write failure"); lock(input) input.Append(Encoding.UTF8.GetString(bytes)); }
            public void Resize(uint c,uint r) { Columns=c; Rows=r; }
            public void Dispose() { if(Interlocked.Increment(ref disposals)==1) { output.CompleteAdding(); release.Set(); } }
        }

    }
}
