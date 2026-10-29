using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Controls;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static partial class MaintenanceTerminalTests
    {
        static void TerminalInteractionTests()
        {
            T("Ordinary abc123 reaches remote peer without local editing", () => {
                using(var f=new TerminalFixture()) {
                    foreach(char c in "abc123") Character(f.View,c);
                    Until(() => f.Peer.Input=="abc123"); E("",f.View.Text);
                    f.Peer.Emit("abc123"); Until(() => f.View.Text=="abc123");
                }
            });
            InputCase("Enter",Keys.Enter,"\n");
            InputCase("Backspace",Keys.Back,"\u007f");
            InputCase("Tab",Keys.Tab,"\t");
            InputCase("Up",Keys.Up,"\u001b[A");
            InputCase("Down",Keys.Down,"\u001b[B");
            InputCase("Left",Keys.Left,"\u001b[D");
            InputCase("Right",Keys.Right,"\u001b[C");
            T("Ctrl+C selection copies without sending or removing text", () => {
                using(var f=new TerminalFixture()) {
                    f.Display("copy this text\r\n"); f.View.Select(0,4); Key(f.View,Keys.Control|Keys.C);
                    E("copy",f.Clipboard.Text); Pump(30); E("",f.Peer.Input); E("copy",f.View.SelectedText); C(f.View.Text,"copy this text");
                }
            });
            T("Ctrl+C without selection sends interrupt byte", () => {
                using(var f=new TerminalFixture()) {
                    f.Display("running\r\n"); Key(f.View,Keys.Control|Keys.C);
                    Until(() => f.Peer.Input=="\u0003"); E(0,f.Clipboard.Writes);
                }
            });
            T("Ctrl+V single line sends clipboard text once", () => {
                using(var f=new TerminalFixture()) {
                    f.Clipboard.Text="paste abc123"; Key(f.View,Keys.Control|Keys.V);
                    Until(() => f.Peer.Input=="paste abc123"); E(1,f.Clipboard.Reads); E("",f.View.Text);
                }
            });
            T("Ctrl+V preserves multiline CRLF LF and CR exactly", () => {
                using(var f=new TerminalFixture()) {
                    f.Clipboard.Text="first\r\nsecond\nthird\rfourth"; Key(f.View,Keys.Control|Keys.V);
                    Until(() => f.Peer.Input==f.Clipboard.Text);
                }
            });
            T("Ctrl+V empty clipboard is a no-op", () => {
                using(var f=new TerminalFixture()) {
                    f.Clipboard.Text=""; Key(f.View,Keys.Control|Keys.V); Pump(30); E("",f.Peer.Input);
                }
            });
            T("Ctrl+V nontext clipboard is a no-op", () => {
                using(var f=new TerminalFixture()) {
                    f.Clipboard.Text=null; Key(f.View,Keys.Control|Keys.V); Pump(30); E("",f.Peer.Input);
                }
            });
            T("Read-only native WM_PASTE reaches remote shell", () => {
                using(var f=new TerminalFixture()) {
                    f.Clipboard.Text="native paste"; NativeSend(f.View.Handle,0x0302,IntPtr.Zero,IntPtr.Zero);
                    Until(() => f.Peer.Input=="native paste"); E(1,f.Clipboard.Reads);
                }
            });
            T("RichEdit input-key Ctrl+V path suppresses native default", () => {
                using(var f=new TerminalFixture()) {
                    f.Clipboard.Text="keydown paste"; var args=new KeyEventArgs(Keys.Control|Keys.V);
                    typeof(TerminalView).GetMethod("OnKeyDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f.View,new object[] { args });
                    A(args.Handled && args.SuppressKeyPress); Until(() => f.Peer.Input=="keydown paste"); E(1,f.Clipboard.Reads);
                }
            });
            T("Native WM_COPY uses selected text without interrupt", () => {
                using(var f=new TerminalFixture()) {
                    f.Display("native copy\r\n"); f.View.Select(0,6); NativeSend(f.View.Handle,0x0301,IntPtr.Zero,IntPtr.Zero);
                    E("native",f.Clipboard.Text); E("",f.Peer.Input);
                }
            });
            T("History wheel pauses follow and keeps visible anchor", () => {
                using(var f=new TerminalFixture()) {
                    f.History(160); NativeSend(f.View.Handle,0x020A,new IntPtr(120<<16),IntPtr.Zero);
                    A(!f.View.FollowTail); int first=f.View.FirstVisibleLine;
                    f.Display("additional background\r\n"); E(first,f.View.FirstVisibleLine); A(!f.View.FollowTail);
                }
            });
            T("Scrollbar top pauses follow without a text selection", () => {
                using(var f=new TerminalFixture()) {
                    f.History(160); ScrollTop(f.View); E(0,f.View.SelectionLength); A(!f.View.FollowTail);
                    int first=f.View.FirstVisibleLine; f.Display("new line\r\n"); E(first,f.View.FirstVisibleLine);
                }
            });
            T("Scrolling back to bottom resumes follow", () => {
                using(var f=new TerminalFixture()) {
                    f.History(160); ScrollTop(f.View); A(!f.View.FollowTail); ScrollBottom(f.View); A(f.View.FollowTail);
                    f.Display("last line\r\n"); A(f.View.InputPositionVisible);
                }
            });
            T("Typing from selected history restores visible input immediately", () => {
                using(var f=new TerminalFixture()) {
                    f.History(160); ScrollTop(f.View); f.View.Select(0,8); Character(f.View,'x');
                    A(f.View.FollowTail); E(0,f.View.SelectionLength); A(f.View.InputPositionVisible);
                    Until(() => f.Peer.Input=="x"); f.Peer.Emit("x"); Until(() => f.View.Text.EndsWith("prompt> x",StringComparison.Ordinal));
                    A(f.View.InputPositionVisible);
                }
            });
            T("Click without selection resumes input follow", () => {
                using(var f=new TerminalFixture()) {
                    f.History(160); ScrollTop(f.View); f.View.Select(0,0);
                    var args=new MouseEventArgs(MouseButtons.Left,1,12,20,0);
                    typeof(TerminalView).GetMethod("OnMouseDown",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f.View,new object[] { args });
                    typeof(TerminalView).GetMethod("OnMouseUp",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(f.View,new object[] { args });
                    A(f.View.FollowTail); A(f.View.InputPositionVisible);
                }
            });
            T("Background output preserves selected historical text and position", () => {
                using(var f=new TerminalFixture()) {
                    f.History(160); ScrollTop(f.View); f.View.Select(0,12); int first=f.View.FirstVisibleLine; string selected=f.View.SelectedText;
                    for(int i=0;i<20;i++) f.Display("background "+i+"\r\n");
                    E(selected,f.View.SelectedText); E(first,f.View.FirstVisibleLine); A(!f.View.FollowTail);
                }
            });
            T("Copy from history does not resume follow or interrupt", () => {
                using(var f=new TerminalFixture()) {
                    f.History(160); ScrollTop(f.View); f.View.Select(0,12); int first=f.View.FirstVisibleLine;
                    Key(f.View,Keys.Control|Keys.C); E(12,f.Clipboard.Text.Length); A(!f.View.FollowTail); E(first,f.View.FirstVisibleLine); E("",f.Peer.Input);
                }
            });
            T("Paste from history resumes follow before remote echo", () => {
                using(var f=new TerminalFixture()) {
                    f.History(160); ScrollTop(f.View); f.View.Select(0,10); f.Clipboard.Text="pasted"; Key(f.View,Keys.Control|Keys.V);
                    A(f.View.FollowTail); A(f.View.InputPositionVisible); E(0,f.View.SelectionLength); Until(() => f.Peer.Input=="pasted");
                }
            });
            T("Hundreds of tiny transport reads batch into few paints", () => {
                using(var f=new TerminalFixture()) {
                    f.Peer.OneByte=true;
                    string text=string.Join("",Enumerable.Range(0,120).Select(i => "tiny "+i.ToString("D3")+"\r\n"));
                    f.Peer.Emit(text); Until(() => f.View.DisplayedCharacterCount==text.Length);
                    E(text.Replace("\r\n","\n"),f.View.Text); E(text.Length,f.View.ReceivedChunkCount);
                    E(0,f.View.PendingChunkCount); A(f.View.FlushCount<f.View.ReceivedChunkCount/10);
                    A(f.View.RedrawBatchCount==f.View.FlushCount); A(f.View.TextPatchCount<=f.View.FlushCount);
                    Console.WriteLine("METRIC tiny chunks="+f.View.ReceivedChunkCount+" flushes="+f.View.FlushCount+" patches="+f.View.TextPatchCount+" redraw batches="+f.View.RedrawBatchCount);
                }
            });
            T("scroll preserves every numbered line once and in order", () => {
                using(var f=new TerminalFixture(new LocalFakeShell())) {
                    f.View.SendInput("scroll\n"); Until(() => f.View.Text.Contains("scrolling line 180") && f.View.Text.EndsWith("fake-shell $ ",StringComparison.Ordinal));
                    var matches=Regex.Matches(f.View.Text,@"\[local\] scrolling line (\d+) · 可选择并复制日志");
                    E(180,matches.Count); for(int i=0;i<180;i++) E(i+1,int.Parse(matches[i].Groups[1].Value));
                    A(f.View.FollowTail); A(f.View.InputPositionVisible); E(0,f.View.PendingChunkCount);
                    A(f.View.FlushCount<f.View.ReceivedChunkCount/3);
                    Console.WriteLine("METRIC scroll chunks="+f.View.ReceivedChunkCount+" flushes="+f.View.FlushCount+" scrolls="+f.View.TailScrollCount);
                }
            });
            T("slow progresses in order while UI scroll copy and input work", () => {
                using(var f=new TerminalFixture(new LocalFakeShell()))
                using(var timer=new System.Windows.Forms.Timer { Interval=15 }) {
                    f.History(100); int ticks=0; timer.Tick+=(s,e) => ticks++; timer.Start();
                    f.View.SendInput("slow\n"); Until(() => f.View.Text.Contains("background output 4/30")); A(!f.View.Text.Contains("background output 30/30"));
                    ScrollTop(f.View); f.View.Select(0,12); Key(f.View,Keys.Control|Keys.C); E(12,f.Clipboard.Text.Length);
                    int first=f.View.FirstVisibleLine; Until(() => f.View.Text.Contains("background output 10/30"));
                    E(first,f.View.FirstVisibleLine); A(!f.View.FollowTail); Character(f.View,'x'); A(f.View.FollowTail); A(f.View.InputPositionVisible);
                    Until(() => f.View.Text.Contains("background output 30/30")); W(((LocalFakeShell)f.Remote).BackgroundCompletion);
                    W(Task.Delay(40)); f.View.RenderPending();
                    var matches=Regex.Matches(f.View.Text,@"\[local\] background output (\d+)/30");
                    E(30,matches.Count); for(int i=0;i<30;i++) E(i+1,int.Parse(matches[i].Groups[1].Value));
                    A(ticks>30); A(f.View.InputPositionVisible); timer.Stop();
                    Console.WriteLine("METRIC slow UI timer ticks="+ticks+" lines="+matches.Count+" flushes="+f.View.FlushCount);
                }
            });
            T("slow with no selection is interrupted by Ctrl+C", () => {
                using(var f=new TerminalFixture(new LocalFakeShell())) {
                    f.View.SendInput("slow\n"); Until(() => f.View.Text.Contains("background output 2/30")); Key(f.View,Keys.Control|Keys.C);
                    W(((LocalFakeShell)f.Remote).BackgroundCompletion); Until(() => f.View.Text.Contains("^C"));
                    A(!f.View.Text.Contains("background output 30/30"));
                }
            });
            T("disconnect drains pending output and joins fake background task", () => {
                using(var f=new TerminalFixture(new LocalFakeShell())) {
                    f.View.SendInput("slow\n"); Until(() => f.View.Text.Contains("background output 2/30"));
                    f.View.SendInput("disconnect\n"); W(f.Session.Completion); W(f.View.DrainOutputAsync(f.Session));
                    C(f.View.Text,"[local] 模拟远端断开"); E(0,f.View.PendingChunkCount);
                    E(f.View.ReceivedCharacterCount,f.View.DisplayedCharacterCount); A(!f.Session.Send("x"));
                    A(((LocalFakeShell)f.Remote).BackgroundCompletion.IsCompleted);
                }
            });
            T("EOF flushes final buffered UTF-8 output without leftover data", () => {
                using(var f=new TerminalFixture()) {
                    string text=string.Join("",Enumerable.Range(0,700).Select(i => "buffer "+i+"\r\n"))+"最后一行😀";
                    f.Peer.Emit(text); f.Peer.End(); W(f.Session.Completion); W(f.View.DrainOutputAsync(f.Session));
                    E(text.Replace("\r\n","\n"),f.View.Text); E(0,f.View.PendingChunkCount);
                    E(f.View.ReceivedCharacterCount,f.View.DisplayedCharacterCount); A(!f.Session.Send("x")); E(1,f.Peer.Disposals);
                }
            });
            T("CRLF split between flushes creates exactly one newline", () => {
                using(var f=new TerminalFixture()) {
                    f.Display("one\r"); E("one",f.View.Text); f.Display("\ntwo\r"); f.Display("\nthree\n");
                    E("one\ntwo\nthree\n",f.View.Text);
                }
            });
            T("CR overwrites current progress without transient missing history", () => {
                using(var f=new TerminalFixture()) {
                    f.Display("history\r\n下载进度 10%");
                    for(int i=20;i<=90;i+=10) {
                        f.Display("\r下载进度 "+i+"%"); E("history\n下载进度 "+i+"%",f.View.Text);
                    }
                    f.Display("\r\u001b[2K完成\r\n"); E("history\n完成\n",f.View.Text);
                }
            });
            T("Split ANSI never removes ordinary text or duplicates lines", () => {
                using(var f=new TerminalFixture()) {
                    f.Display("one\r\n\u001b[3"); f.Display("1m[WARN] two\u001b[0"); f.Display("m\r\nthree");
                    E("one\n[WARN] two\nthree",f.View.Text);
                }
            });
            T("Multiline paste creates no duplicate CRLF command in fake shell", () => {
                using(var f=new TerminalFixture(new LocalFakeShell())) {
                    f.Clipboard.Text="first\r\nsecond\nthird\rfourth"; Key(f.View,Keys.Control|Keys.V);
                    Until(() => f.View.Text.Contains("fake-shell $ fourth"));
                    E(3,Regex.Matches(f.View.Text,@"\[local\] received: ").Count);
                    C(f.View.Text,"received: first"); C(f.View.Text,"received: second"); C(f.View.Text,"received: third");
                }
            });
            T("Trimming only complete oldest lines keeps active input", () => {
                using(var f=new TerminalFixture()) {
                    f.History(3990); f.Display(string.Join("",Enumerable.Range(0,100).Select(i => "new "+i+"\r\n"))+"ACTIVE_INPUT");
                    A(f.View.Text.Split('\n').Length<=TerminalScreen.MaximumLines);
                    A(f.View.Text.EndsWith("ACTIVE_INPUT",StringComparison.Ordinal)); A(f.View.InputPositionVisible);
                }
            });
            T("Paused history keeps content anchor when oldest lines trim", () => {
                using(var f=new TerminalFixture()) {
                    f.History(3990); ScrollTop(f.View); NativeSend(f.View.Handle,0x00B6,IntPtr.Zero,new IntPtr(200));
                    string anchor=f.View.Lines[f.View.FirstVisibleLine];
                    f.View.Select(f.View.GetFirstCharIndexFromLine(210),8); string selected=f.View.SelectedText;
                    f.Display(string.Join("",Enumerable.Range(0,100).Select(i => "new "+i+"\r\n")));
                    E(anchor,f.View.Lines[f.View.FirstVisibleLine]); E(selected,f.View.SelectedText); A(!f.View.FollowTail);
                }
            });
            T("Incremental empty-tail deletion matches screen and keeps read-only input", () => {
                using(var f=new TerminalFixture()) {
                    f.Display("history\r\nabc"); f.Display("\b\u001b[K"); E("history\nab",f.View.Text); E(f.View.Screen.Text,f.View.Text); A(f.View.ReadOnly);
                }
            });
            T("Every capped output batch matches the full terminal model", () => {
                using(var f=new TerminalFixture()) {
                    for(int i=0;i<45;i++) {
                        f.Display(string.Join("",Enumerable.Range(i*100,100).Select(n => "cap "+n+"\r\n")));
                        E(f.View.Screen.Text,f.View.Text); A(f.View.ReadOnly);
                    }
                }
            });
            T("Long continuous text does not overwrite active input at a column cap", () => {
                using(var f=new TerminalFixture()) {
                    string text=new string('x',6000)+"ACTIVE_END";
                    f.Display(text); E(text,f.View.Text); A(f.View.InputPositionVisible);
                    f.Display("\u001b[DZ"); E(text.Substring(0,text.Length-1)+"Z",f.View.Text);
                }
            });
            T("Tab after a long line retains existing text", () => {
                var screen=new TerminalScreen(); screen.Feed(new string('x',6001)+"\tZ");
                E(new string('x',6001)+new string(' ',7)+"Z",screen.Text);
            });
            T("Idle timer creates no extra redraw batches", () => {
                using(var f=new TerminalFixture()) {
                    f.Display("steady\r\n"); int batches=f.View.RedrawBatchCount; Pump(100); E(batches,f.View.RedrawBatchCount);
                }
            });
        }
        static void InputCase(string name,Keys key,string expected)
        {
            T(name+" sends bytes and restores follow from history", () => {
                using(var f=new TerminalFixture()) {
                    f.History(100); ScrollTop(f.View); Key(f.View,key);
                    Until(() => f.Peer.Input==expected); A(f.View.FollowTail); A(f.View.InputPositionVisible);
                }
            });
        }
        static void ScrollTop(TerminalView view) { NativeSend(view.Handle,0x0115,new IntPtr(6),IntPtr.Zero); }
        static void ScrollBottom(TerminalView view) { NativeSend(view.Handle,0x0115,new IntPtr(7),IntPtr.Zero); }
        [DllImport("user32.dll",EntryPoint="SendMessageW")] static extern IntPtr NativeSend(IntPtr handle,int message,IntPtr param,IntPtr data);
        sealed class TestClipboard:ITerminalClipboard
        {
            public string Text; public int Reads,Writes;
            public string ReadText() { Reads++; return Text; }
            public void WriteText(string text) { Writes++; Text=text; }
        }
        sealed class TerminalFixture:IDisposable
        {
            public readonly Form Window=new Form { ClientSize=new Size(620,210) };
            public readonly TestClipboard Clipboard=new TestClipboard();
            public readonly TerminalView View;
            public readonly IShellTransport Remote;
            public readonly Peer Peer;
            public readonly InteractiveTerminalSession Session;
            public TerminalFixture():this(new Peer()) { }
            public TerminalFixture(IShellTransport remote) {
                Remote=remote; Peer=remote as Peer; View=new TerminalView(Clipboard); Window.Controls.Add(View);
                Window.Show(); Session=new InteractiveTerminalSession(remote); View.Bind(Session); Session.Start(); View.Focus();
            }
            public void Display(string text) { View.QueueOutput(text); View.RenderPending(); }
            public void History(int count) { Display(string.Join("",Enumerable.Range(0,count).Select(i => "history "+i.ToString("D4")+"\r\n"))+"prompt> "); }
            public void Dispose() { W(Session.StopAsync()); W(View.DrainOutputAsync(Session)); Window.Close(); Window.Dispose(); }
        }
    }
}

