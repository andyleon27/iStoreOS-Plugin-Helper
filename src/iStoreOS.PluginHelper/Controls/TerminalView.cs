using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Controls
{
    public static class TerminalKeyboard
    {
        public static string Encode(Keys keys)
        {
            Keys code = keys & Keys.KeyCode;
            Keys modifiers = keys & Keys.Modifiers;
            if (modifiers == Keys.Control && code >= Keys.A && code <= Keys.Z)
                return ((char)(code - Keys.A + 1)).ToString();
            string value;
            switch (code)
            {
                case Keys.Enter: value = "\n"; break;
                case Keys.Back: value = "\u007f"; break;
                case Keys.Tab: value = modifiers == Keys.Shift ? "\u001b[Z" : "\t"; break;
                case Keys.Up: value = "\u001b[A"; break;
                case Keys.Down: value = "\u001b[B"; break;
                case Keys.Right: value = "\u001b[C"; break;
                case Keys.Left: value = "\u001b[D"; break;
                case Keys.Home: value = "\u001b[H"; break;
                case Keys.End: value = "\u001b[F"; break;
                case Keys.Delete: value = "\u001b[3~"; break;
                case Keys.Insert: value = "\u001b[2~"; break;
                case Keys.PageUp: value = "\u001b[5~"; break;
                case Keys.PageDown: value = "\u001b[6~"; break;
                case Keys.Escape: value = "\u001b"; break;
                case Keys.F1: value = "\u001bOP"; break;
                case Keys.F2: value = "\u001bOQ"; break;
                case Keys.F3: value = "\u001bOR"; break;
                case Keys.F4: value = "\u001bOS"; break;
                case Keys.F5: value = "\u001b[15~"; break;
                case Keys.F6: value = "\u001b[17~"; break;
                case Keys.F7: value = "\u001b[18~"; break;
                case Keys.F8: value = "\u001b[19~"; break;
                case Keys.F9: value = "\u001b[20~"; break;
                case Keys.F10: value = "\u001b[21~"; break;
                case Keys.F11: value = "\u001b[23~"; break;
                case Keys.F12: value = "\u001b[24~"; break;
                default: return null;
            }
            return (modifiers & Keys.Alt) != 0 ? "\u001b" + value : value;
        }
    }

    public interface ITerminalClipboard
    {
        string ReadText();
        void WriteText(string text);
    }
    public sealed class WindowsTerminalClipboard : ITerminalClipboard
    {
        public string ReadText()
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (ExternalException) { return null; }
        }
        public void WriteText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch (ExternalException) { }
        }
    }

    public sealed class TerminalView : RichTextBox
    {
        public const int FlushIntervalMilliseconds = 24;
        public const int MaximumFlushCharacters = 65536;
        private readonly BlockingCollection<string> pending = new BlockingCollection<string>(1024);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer flushTimer = new System.Windows.Forms.Timer { Interval = FlushIntervalMilliseconds };
        private readonly TerminalScreen screen = new TerminalScreen();
        private readonly ITerminalClipboard clipboard;
        private IInteractiveTerminalSession session;
        private Action<string> outputHandler;
        private string rendered = "";
        private long receivedChunks, receivedCharacters, displayedCharacters;
        private long trimmedCharacters, trimmedLines;
        private bool followTail = true, applyingOutput, pointerDown;
        private Font ownedFont;
        private Font supplementaryFont;
        private Size cellSize;
        private bool changingFont;
        private uint sentColumns, sentRows;
        private uint visibleColumns, visibleRows;
        public event Action<string> InputRejected;
        public TerminalScreen Screen { get { return screen; } }
        public bool FollowTail { get { return followTail; } }
        public int PendingChunkCount { get { return pending.Count; } }
        public long ReceivedChunkCount { get { return Interlocked.Read(ref receivedChunks); } }
        public long ReceivedCharacterCount { get { return Interlocked.Read(ref receivedCharacters); } }
        public long DisplayedCharacterCount { get { return displayedCharacters; } }
        public int FlushCount { get; private set; }
        public int TextPatchCount { get; private set; }
        public int RedrawBatchCount { get; private set; }
        public int TailScrollCount { get; private set; }
        public Size CellSize { get { if (cellSize.IsEmpty) cellSize = MeasureFont(Font, IsHandleCreated ? Handle : IntPtr.Zero, false); return cellSize; } }
        public Rectangle ViewportBounds
        {
            get {
                if (!IsHandleCreated) return ClientRectangle;
                var rect = new NativeRect(); SendMessage(Handle, 0x00B2, IntPtr.Zero, ref rect); // EM_GETRECT includes native text insets.
                return Rectangle.Intersect(ClientRectangle, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            }
        }
        public uint Columns { get { return (uint)Math.Max(1, Math.Min(1000, ViewportBounds.Width / Math.Max(1, CellSize.Width))); } }
        public uint Rows { get { return (uint)Math.Max(1, Math.Min(1000, ViewportBounds.Height / Math.Max(1, CellSize.Height))); } }
        public int FirstVisibleLine { get { return IsHandleCreated ? (int)SendMessage(Handle, 0x00CE, IntPtr.Zero, IntPtr.Zero) : 0; } }
        public bool InputPositionVisible
        {
            get
            {
                if (!IsHandleCreated) return false;
                Point point = GetPositionFromCharIndex(Math.Min(screen.CursorOffset, TextLength));
                Rectangle viewport = ViewportBounds;
                return point.Y >= viewport.Top && point.Y + CellSize.Height <= viewport.Bottom + 2 && point.X >= viewport.Left && point.X < viewport.Right;
            }
        }

        public TerminalView() : this(new WindowsTerminalClipboard()) { }
        public TerminalView(ITerminalClipboard clipboard)
        {
            this.clipboard = clipboard;
            Name = "InteractiveTerminal"; AccessibleName = "交互终端";
            Dock = DockStyle.Fill; ReadOnly = true; WordWrap = false; DetectUrls = false;
            ownedFont = CreateTerminalFont(11F); Font = ownedFont;
            BackColor = Color.FromArgb(18, 23, 33); ForeColor = Color.FromArgb(220, 230, 240);
            BorderStyle = BorderStyle.None; HideSelection = false; ShortcutsEnabled = false; AcceptsTab = true; MaxLength = int.MaxValue;
            ScrollBars = RichTextBoxScrollBars.Both;
            var menu = new ContextMenuStrip();
            menu.Items.Add("复制选中文字  Ctrl+C", null, (sender, args) => CopySelection());
            menu.Items.Add("复制全部日志", null, (sender, args) => clipboard.WriteText(Text));
            menu.Items.Add("粘贴到终端  Ctrl+V", null, (sender, args) => PasteToShell());
            ContextMenuStrip = menu;
            flushTimer.Tick += (sender, args) => { RenderPending(); SynchronizeViewport(); }; flushTimer.Start();
        }
        protected override void OnHandleCreated(EventArgs args)
        {
            DisableAutomaticFontBinding(); // Before RichTextBox restores existing RTF on handle recreation.
            base.OnHandleCreated(args);
            SendMessage(Handle, 0x0452, IntPtr.Zero, IntPtr.Zero); // EM_SETUNDOLIMIT: terminal output has no undo history.
            DisableAutomaticFontBinding();
            cellSize = Size.Empty; sentColumns = sentRows = 0;
            ApplyLineHeight();
            SynchronizeViewport();
        }
        private void DisableAutomaticFontBinding()
        {
            int language = (int)SendMessage(Handle, 0x0479, IntPtr.Zero, IntPtr.Zero); // EM_GETLANGOPTIONS
            // IMF_AUTOFONT | IMF_AUTOFONTSIZEADJUST | IMF_DUALFONT can bind block glyphs
            // to proportional Segoe UI Symbol despite their presence in Consolas.
            SendMessage(Handle, 0x0478, IntPtr.Zero, new IntPtr(language & ~0x0092));
        }
        public void Bind(IInteractiveTerminalSession value)
        {
            if (session != null && outputHandler != null) session.Output -= outputHandler;
            session = value;
            sentColumns = sentRows = 0;
            string discarded; while (pending.TryTake(out discarded)) { }
            applyingOutput = true;
            try { screen.Clear(); Clear(); rendered = ""; followTail = true; }
            finally { applyingOutput = false; }
            receivedChunks = receivedCharacters = displayedCharacters = trimmedCharacters = trimmedLines = 0;
            FlushCount = TextPatchCount = RedrawBatchCount = TailScrollCount = 0;
            if (value != null) { outputHandler = QueueOutput; value.Output += outputHandler; SynchronizeViewport(); }
        }
        public void QueueOutput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                pending.Add(text, lifetime.Token);
                Interlocked.Increment(ref receivedChunks); Interlocked.Add(ref receivedCharacters, text.Length);
            }
            catch (OperationCanceledException) { }
            catch (InvalidOperationException) { }
        }
        public void RenderPending()
        {
            if (applyingOutput || IsDisposed) return;
            string part; var batch = new StringBuilder();
            while (batch.Length < MaximumFlushCharacters && pending.TryTake(out part)) batch.Append(part);
            if (batch.Length == 0) return;
            FlushCount++; displayedCharacters += batch.Length;
            screen.ViewRows = (int)Rows; screen.Feed(batch.ToString());
            string next = screen.Text;
            int removedHead = (int)Math.Min(rendered.Length, screen.TrimmedCharacters - trimmedCharacters);
            long removedLines = screen.TrimmedLines - trimmedLines;
            NativePoint scroll = GetScrollPosition();
            int lineHeight = ActualLineHeight();
            int start = Math.Max(0, SelectionStart - removedHead);
            int end = Math.Max(0, SelectionStart + SelectionLength - removedHead);
            bool restoreReadOnly = ReadOnly;
            bool retainView = !followTail || pointerDown || SelectionLength > 0;
            string previous = removedHead == 0 ? rendered : rendered.Substring(removedHead);
            int prefix = 0, limit = Math.Min(previous.Length, next.Length);
            while (prefix < limit && previous[prefix] == next[prefix]) prefix++;
            if (prefix > 0 && char.IsHighSurrogate(previous[prefix - 1])) prefix--;
            int suffix = 0;
            while (suffix < previous.Length - prefix && suffix < next.Length - prefix &&
                previous[previous.Length - suffix - 1] == next[next.Length - suffix - 1]) suffix++;
            int deleteCount = previous.Length - prefix - suffix;
            string insert = next.Substring(prefix, next.Length - prefix - suffix);

            applyingOutput = true;
            SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW: hide intermediate edits, selection and scrolling.
            try
            {
                // Empty EM_REPLACESEL deletions are rejected by read-only RichEdit.
                // Editing is enabled only inside this synchronous redraw-suppressed batch.
                ReadOnly = false;
                if (removedHead > 0) { Select(0, removedHead); SelectedText = ""; TextPatchCount++; }
                if (deleteCount != 0 || insert.Length != 0)
                {
                    InsertTerminalText(prefix, deleteCount, insert); TextPatchCount++;
                }
                rendered = next; trimmedCharacters = screen.TrimmedCharacters; trimmedLines = screen.TrimmedLines;
                if (retainView)
                {
                    int mappedStart = MapOffset(start, prefix, deleteCount, insert.Length);
                    int mappedEnd = MapOffset(end, prefix, deleteCount, insert.Length);
                    Select(Math.Min(mappedStart, TextLength), Math.Max(0, Math.Min(mappedEnd, TextLength) - Math.Min(mappedStart, TextLength)));
                    scroll.Y = (int)Math.Max(0, scroll.Y - removedLines * lineHeight);
                    SendMessage(Handle, 0x04DE, IntPtr.Zero, ref scroll);
                }
                else PositionInputCaret();
            }
            finally
            {
                ReadOnly = restoreReadOnly;
                SendMessage(Handle, 0x000B, new IntPtr(1), IntPtr.Zero);
                applyingOutput = false;
                // One scheduled paint for the complete batch, without a background erase or synchronous Refresh.
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0020 | 0x0400);
                RedrawBatchCount++;
            }
        }
        private static int MapOffset(int value, int start, int removed, int inserted)
        {
            if (value <= start) return value;
            if (value >= start + removed) return value + inserted - removed;
            return start + Math.Min(value - start, inserted);
        }
        private void InsertTerminalText(int offset, int removed, string text)
        {
            Select(offset, removed); SelectionFont = Font;
            if (text.Length == 0) SelectedText = "";
            for (int start = 0; start < text.Length;) {
                bool supplementary = char.IsSurrogate(text[start]); int end = start + 1;
                while (end < text.Length && char.IsSurrogate(text[end]) == supplementary) end++;
                if (start > 0) Select(offset + start, 0);
                // Keep unsupported supplementary glyph fallback local to that run.
                // Automatic font binding would also change preceding block/ASCII runs.
                SelectionFont = supplementary ? SupplementaryFont() : Font;
                SelectedText = text.Substring(start, end - start); start = end;
            }
            Select(offset, text.Length); ApplyLineHeight();
        }
        private Font SupplementaryFont()
        {
            if (supplementaryFont == null) {
                supplementaryFont = new Font("Segoe UI Emoji", Font.SizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
                if (supplementaryFont.Name != "Segoe UI Emoji") {
                    supplementaryFont.Dispose(); supplementaryFont = new Font("Segoe UI Symbol", Font.SizeInPoints, FontStyle.Regular, GraphicsUnit.Point);
                }
            }
            return supplementaryFont;
        }
        private void ApplyLineHeight()
        {
            if (!IsHandleCreated) return;
            using (var graphics = CreateGraphics()) {
                var format = new ParagraphFormat { Size = 188, Mask = 0x100, LineSpacing = (int)Math.Round(CellSize.Height * 1440.0 / graphics.DpiY), Rule = 4 };
                SendMessage(Handle, 0x0447, IntPtr.Zero, ref format); // PFM_LINESPACING, exact cell height in twips.
            }
        }
        private int ActualLineHeight()
        {
            int first = FirstVisibleLine;
            int a = GetFirstCharIndexFromLine(first), b = GetFirstCharIndexFromLine(first + 1);
            if (a >= 0 && b >= 0)
            {
                int height = GetPositionFromCharIndex(b).Y - GetPositionFromCharIndex(a).Y;
                if (height > 0) return height;
            }
            return CellSize.Height;
        }
        private NativePoint GetScrollPosition()
        {
            NativePoint point = new NativePoint();
            if (IsHandleCreated) SendMessage(Handle, 0x04DD, IntPtr.Zero, ref point);
            return point;
        }
        private bool NearBottom()
        {
            if (!IsHandleCreated) return true;
            var info = new ScrollInfo { Size = (uint)Marshal.SizeOf(typeof(ScrollInfo)), Mask = 0x17 };
            if (!GetScrollInfo(Handle, 1, ref info)) return InputPositionVisible;
            return info.Maximum - Math.Max(0, (int)info.Page - 1) - info.Position <= CellSize.Height;
        }
        private bool HasVerticalHistory()
        {
            var info = new ScrollInfo { Size = (uint)Marshal.SizeOf(typeof(ScrollInfo)), Mask = 0x17 };
            return GetScrollInfo(Handle, 1, ref info) && info.Maximum - info.Minimum + 1 > info.Page;
        }
        private void PositionInputCaret()
        {
            int cursor = Math.Min(screen.CursorOffset, TextLength);
            if (SelectionStart != cursor || SelectionLength != 0) Select(cursor, 0);
            if (!InputPositionVisible) { ScrollToCaret(); TailScrollCount++; }
        }
        private void ResumeFollowing()
        {
            followTail = true; applyingOutput = true;
            try { PositionInputCaret(); } finally { applyingOutput = false; }
        }
        public bool SendInput(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            ResumeFollowing();
            bool sent = session != null && session.Send(text);
            if (!sent) { var rejected = InputRejected; if (rejected != null) rejected("终端尚未连接，或发送队列已满。"); }
            return sent;
        }
        private void CopySelection() { if (SelectionLength > 0) clipboard.WriteText(SelectedText); }
        private void PasteToShell()
        {
            string text = clipboard.ReadText();
            if (!string.IsNullOrEmpty(text)) SendInput(text); // Preserve the clipboard's bytes/newline sequence.
        }
        private bool HandleTerminalKey(Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.C))
            { if (SelectionLength > 0) CopySelection(); else SendInput("\u0003"); return true; }
            if (keyData == (Keys.Control | Keys.V)) { PasteToShell(); return true; }
            string encoded = TerminalKeyboard.Encode(keyData);
            if (encoded == null) return false;
            SendInput(encoded); return true;
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        { return HandleTerminalKey(keyData) || base.ProcessCmdKey(ref message, keyData); }
        protected override void OnKeyDown(KeyEventArgs args)
        {
            // RichEdit can classify clipboard chords as input keys, bypassing ProcessCmdKey.
            if (HandleTerminalKey(args.KeyData)) { args.Handled = true; args.SuppressKeyPress = true; return; }
            base.OnKeyDown(args);
        }
        protected override void OnKeyPress(KeyPressEventArgs args)
        {
            args.Handled = true;
            if (!char.IsControl(args.KeyChar)) SendInput(((ModifierKeys & Keys.Alt) != 0 ? "\u001b" : "") + args.KeyChar);
        }
        protected override void OnSelectionChanged(EventArgs args)
        {
            base.OnSelectionChanged(args);
            if (!applyingOutput && SelectionLength > 0) followTail = false;
        }
        protected override void OnMouseDown(MouseEventArgs args)
        {
            if (args.Button == MouseButtons.Left) pointerDown = true;
            base.OnMouseDown(args);
        }
        protected override void OnMouseUp(MouseEventArgs args)
        {
            base.OnMouseUp(args);
            if (args.Button != MouseButtons.Left) return;
            pointerDown = false;
            if (SelectionLength > 0) followTail = false;
            else ResumeFollowing();
        }
        protected override void OnVScroll(EventArgs args)
        {
            base.OnVScroll(args);
            if (!applyingOutput && !pointerDown) followTail = NearBottom();
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0302) { PasteToShell(); return; } // WM_PASTE: never let a read-only native control swallow it.
            if (message.Msg == 0x0301) { CopySelection(); return; }
            bool userScroll = !applyingOutput && (message.Msg == 0x0115 || message.Msg == 0x020A);
            bool wheelUp = message.Msg == 0x020A && (short)((long)message.WParam >> 16) > 0;
            base.WndProc(ref message);
            if (userScroll) followTail = wheelUp && HasVerticalHistory() ? false : NearBottom();
            if (message.Msg == 0x02E0 || message.Msg == 0x02E3) { cellSize = Size.Empty; SynchronizeViewport(); } // DPI / parent DPI changed.
        }
        protected override void OnResize(EventArgs args)
        {
            base.OnResize(args);
            SynchronizeViewport();
        }
        protected override void OnFontChanged(EventArgs args)
        {
            if (screen != null && !changingFont && MeasureFont(Font, IntPtr.Zero, true).IsEmpty)
            {
                changingFont = true; Font previous = ownedFont;
                try { ownedFont = CreateTerminalFont(Font.SizeInPoints); Font = ownedFont; }
                finally { changingFont = false; if (previous != null) previous.Dispose(); }
                return;
            }
            base.OnFontChanged(args); cellSize = Size.Empty;
            if (supplementaryFont != null) { supplementaryFont.Dispose(); supplementaryFont = null; }
            if (screen != null && IsHandleCreated) {
                bool previous = applyingOutput; int start = SelectionStart, length = SelectionLength; applyingOutput = true;
                try { SelectAll(); ApplyLineHeight(); Select(start, length); }
                finally { applyingOutput = previous; }
                SynchronizeViewport();
            }
        }
        public void SynchronizeViewport()
        {
            if (screen == null || IsDisposed || !IsHandleCreated || !Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            var window = FindForm(); if (window != null && window.WindowState == FormWindowState.Minimized) return;
            uint columns = Columns, rows = Rows; screen.ViewRows = (int)rows;
            visibleColumns = columns; visibleRows = rows;
            // A controller bound before Start rejects Resize. Retry on the UI timer
            // until accepted, and only remember successfully queued dimensions.
            if (session != null && (columns != sentColumns || rows != sentRows) && session.Resize(columns, rows))
            { sentColumns = columns; sentRows = rows; }
        }
        public Tuple<uint, uint> GetViewportSize()
        {
            SynchronizeViewport();
            return Tuple.Create(visibleColumns == 0 ? Columns : visibleColumns, visibleRows == 0 ? Rows : visibleRows);
        }
        private static Font CreateTerminalFont(float points)
        {
            foreach (string name in new[] { "Consolas", "Lucida Console", "Courier New" }) {
                var candidate = new Font(name, points, FontStyle.Regular, GraphicsUnit.Point);
                if (candidate.Name == name && !MeasureFont(candidate, IntPtr.Zero, true).IsEmpty) return candidate;
                candidate.Dispose();
            }
            throw new InvalidOperationException("没有可用的等宽终端字体（需包含 ▀▄█ 字形）。");
        }
        private static Size MeasureFont(Font font, IntPtr window, bool verify)
        {
            if (font == null) return Size.Empty;
            using (var graphics = Graphics.FromHwnd(window)) {
                IntPtr dc = graphics.GetHdc(), handle = font.ToHfont(), previous = SelectObject(dc, handle);
                try {
                    NativeSize cell;
                    if (!GetTextExtentPoint32W(dc, "M", 1, out cell) || cell.Width < 1 || cell.Height < 1) return Size.Empty;
                    if (verify) {
                        const string glyphs = " MiW▀▄█"; var indices = new ushort[glyphs.Length];
                        if (GetGlyphIndicesW(dc, glyphs, glyphs.Length, indices, 1) == uint.MaxValue) return Size.Empty;
                        for (int i = 0; i < glyphs.Length; i++) {
                            NativeSize size;
                            if (indices[i] == 0xffff || !GetTextExtentPoint32W(dc, glyphs.Substring(i, 1), 1, out size) || size.Width != cell.Width) return Size.Empty;
                        }
                    }
                    return new Size(cell.Width, cell.Height);
                }
                finally { SelectObject(dc, previous); DeleteObject(handle); graphics.ReleaseHdc(dc); }
            }
        }
        public async Task DrainOutputAsync(InteractiveTerminalSession owner)
        {
            await owner.Completion;
            while (session == owner && !IsDisposed && pending.Count > 0)
            {
                RenderPending();
                if (pending.Count > 0) await Task.Delay(1); // Yield between bounded final batches.
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (session != null && outputHandler != null) session.Output -= outputHandler;
                lifetime.Cancel(); flushTimer.Dispose(); if (ContextMenuStrip != null) ContextMenuStrip.Dispose();
            }
            base.Dispose(disposing);
            if (disposing && ownedFont != null) { ownedFont.Dispose(); ownedFont = null; }
            if (disposing && supplementaryFont != null) { supplementaryFont.Dispose(); supplementaryFont = null; }
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
        // Win32 PARAFORMAT2 has no pointers and the same 188-byte layout on x86/x64.
        [StructLayout(LayoutKind.Explicit, Size = 188)] private struct ParagraphFormat
        { [FieldOffset(0)] public uint Size; [FieldOffset(4)] public uint Mask; [FieldOffset(164)] public int LineSpacing; [FieldOffset(170)] public byte Rule; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct ScrollInfo
        { public uint Size, Mask; public int Minimum, Maximum; public uint Page; public int Position, TrackPosition; }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr param, ref NativePoint point);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr param, ref NativeRect rect);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr param, ref ParagraphFormat format);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr param, IntPtr data);
        [DllImport("user32.dll")] private static extern bool GetScrollInfo(IntPtr handle, int bar, ref ScrollInfo info);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr handle, IntPtr rect, IntPtr region, uint flags);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int length, out NativeSize size);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern uint GetGlyphIndicesW(IntPtr dc, string text, int length, [Out] ushort[] indices, uint flags);
    }
}
