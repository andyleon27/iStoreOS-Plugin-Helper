using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    // Development-only in-memory peer. It never creates a socket or starts a process.
    // Its line editing belongs to the fake remote peer, never to the terminal UI.
    internal sealed class LocalFakeShell : IShellTransport
    {
        private readonly BlockingCollection<byte[]> output = new BlockingCollection<byte[]>();
        private readonly StringBuilder line = new StringBuilder();
        private readonly Decoder inputDecoder = Encoding.UTF8.GetDecoder();
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private byte[] current;
        private int offset;
        private int disposed;
        private int cursor;
        private int escapeState;
        private int workGeneration;
        private bool previousCarriageReturn;
        private readonly object backgroundSync = new object();
        private Task background = Task.FromResult(0);
        public Task BackgroundCompletion { get { lock (backgroundSync) return background; } }
        public LocalFakeShell()
        {
            Emit("iStoreOS Plugin Helper · 本地模拟终端\r\n");
            Emit("这是本地 fake shell，未连接路由器，未运行第三方脚本。\r\n\r\n");
            Emit("输入任意文字后按 Enter；Tab / 方向键 / Backspace 可测试编辑。\r\n");
            Emit("输入 scroll 测试滚动，slow 测试后台输出，disconnect 测试断线。\r\n");
            Prompt();
        }
        public void Emit(string text)
        {
            try { if (!output.IsAddingCompleted) output.Add(Encoding.UTF8.GetBytes(text)); }
            catch (InvalidOperationException) { }
        }
        private void Prompt() { Emit("\r\nfake-shell $ "); }
        private void Redraw() { Emit("\r\u001b[2Kfake-shell $ " + line + (line.Length > cursor ? "\u001b[" + (line.Length - cursor) + "D" : "")); }
        public int Read(byte[] buffer)
        {
            if (current == null || offset == current.Length)
            {
                try { current = output.Take(); offset = 0; } catch (InvalidOperationException) { return 0; }
            }
            int count = Math.Min(buffer.Length, current.Length - offset);
            Buffer.BlockCopy(current, offset, buffer, 0, count); offset += count; return count;
        }
        public void Write(byte[] bytes)
        {
            if (disposed != 0) throw new ObjectDisposedException("LocalFakeShell");
            char[] chars = new char[bytes.Length];
            int length = inputDecoder.GetChars(bytes, 0, bytes.Length, chars, 0);
            for (int i = 0; i < length; i++)
            {
                char c = chars[i];
                if (c == '\n' && previousCarriageReturn) { previousCarriageReturn = false; continue; }
                previousCarriageReturn = false;
                if (escapeState == 1) { escapeState = c == '[' ? 2 : 0; continue; }
                if (escapeState == 2)
                {
                    escapeState = 0;
                    if (c == 'D') cursor = Math.Max(0, cursor - 1);
                    if (c == 'C') cursor = Math.Min(line.Length, cursor + 1);
                    if (c == 'A') Emit("\r\n[local] Up received\r\n");
                    if (c == 'B') Emit("\r\n[local] Down received\r\n");
                    Redraw(); continue;
                }
                if (c == '\u001b') { escapeState = 1; continue; }
                if (c == '\u0003') { Interlocked.Increment(ref workGeneration); line.Clear(); cursor = 0; Emit("^C\r\n"); Prompt(); continue; }
                if (c == '\u007f' || c == '\b')
                { if (cursor > 0) { line.Remove(--cursor, 1); Redraw(); } continue; }
                if (c == '\t') { line.Insert(cursor, "    "); cursor += 4; Redraw(); continue; }
                if (c == '\n' || c == '\r')
                { previousCarriageReturn = c == '\r'; string command = line.ToString(); line.Clear(); cursor = 0; Emit("\r\n"); Execute(command); if (stopping.IsCancellationRequested) break; continue; }
                if (!char.IsControl(c)) { line.Insert(cursor++, c); Redraw(); }
            }
        }
        private void Execute(string command)
        {
            if (command == "disconnect") { Interlocked.Increment(ref workGeneration); stopping.Cancel(); Emit("[local] 模拟远端断开\r\n"); output.CompleteAdding(); return; }
            if (command == "scroll")
            { for (int i = 1; i <= 180; i++) Emit("[local] scrolling line " + i + " · 可选择并复制日志\r\n"); Prompt(); return; }
            if (command == "slow")
            {
                int generation = Interlocked.Increment(ref workGeneration);
                lock (backgroundSync) background = Task.Run(async () =>
                {
                    for (int i = 1; i <= 30 && generation == workGeneration && !stopping.IsCancellationRequested; i++)
                    { Emit("[local] background output " + i + "/30\r\n"); try { await Task.Delay(150, stopping.Token); } catch (OperationCanceledException) { return; } }
                    if (generation == workGeneration) Prompt();
                });
                return;
            }
            Emit("[local] received: " + command + "\r\n"); Prompt();
        }
        public void Resize(uint columns, uint rows) { }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopping.Cancel(); output.CompleteAdding();
            try { BackgroundCompletion.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        }
    }
}
