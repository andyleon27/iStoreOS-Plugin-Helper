using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Controls;
using iStoreOS.PluginHelper.Services;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class TerminalDisplayTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public static void Run(Action<string, Action> test)
        {
            test("Terminal display block glyphs use the terminal face and match ASCII cell widths", () => {
                using (var f = new Fixture()) {
                    f.Write("MMMMMMMMMM|\r\n▀▀▀▀▀▀▀▀▀▀|\r\n▄▄▄▄▄▄▄▄▄▄|\r\n██████████|\r\n中文😀\r\n▀▄█MMMMMMM|");
                    for (int row = 0; row < 4; row++) {
                        int index = f.View.GetFirstCharIndexFromLine(row);
                        Equal(10 * f.View.CellSize.Width, f.View.GetPositionFromCharIndex(index + 10).X - f.View.GetPositionFromCharIndex(index).X);
                        f.View.Select(index, 10); Equal(f.View.Font.Name, f.View.SelectionFont.Name);
                    }
                    for (int row = 1; row < 6; row++)
                        Equal(f.View.CellSize.Height, Position(f.View, row).Y - Position(f.View, row - 1).Y);
                    int last = f.View.GetFirstCharIndexFromLine(5); f.View.Select(last, 10); Equal(f.View.Font.Name, f.View.SelectionFont.Name);
                    int emoji = f.View.Text.IndexOf("😀", StringComparison.Ordinal); f.View.Select(emoji, 2);
                    True(f.View.SelectionFont.Name.StartsWith("Segoe UI", StringComparison.Ordinal));
                    Equal("MMMMMMMMMM|\n▀▀▀▀▀▀▀▀▀▀|\n▄▄▄▄▄▄▄▄▄▄|\n██████████|\n中文😀\n▀▄█MMMMMMM|", f.View.Text);
                    Capture(f.Window, "terminal-glyph-grid.png");
                    Console.WriteLine("METRIC font=" + f.View.Font.Name + " cell=" + f.View.CellSize + " viewport=" + f.View.ViewportBounds + " grid=" + f.View.Columns + "x" + f.View.Rows);
                }
            });
            test("Terminal display representative block banner keeps its grid across split output", () => {
                using (var f = new Fixture()) {
                    string banner = "  ▄██▄  ▀█▀  █▀▀▀  ███▄  ▄██▄  █▀▀▀\r\n  █  █   █   ▀▀▀█  █  █  █  █  ▀▀▀█\r\n  ▀██▀   █   ▄▄▄█  ███▀  ▀██▀  ▄▄▄█\r\n";
                    for (int i = 0; i < banner.Length; i += 7) f.Write(banner.Substring(i, Math.Min(7, banner.Length - i)));
                    Equal(banner.Replace("\r\n", "\n"), f.View.Text);
                    for (int row = 0; row < 3; row++) { int start = f.View.GetFirstCharIndexFromLine(row); f.View.Select(start, f.View.Lines[row].Length); Equal(f.View.Font.Name, f.View.SelectionFont.Name); }
                    Capture(f.Window, "terminal-banner.png");
                }
            });
            foreach (float points in new[] { 9F, 11F, 14F, 18F }) {
                float size = points;
                test("Terminal display font size uses actual RichEdit cell metrics " + size, () => {
                    using (var f = new Fixture()) using (var font = new Font("Consolas", size)) {
                        f.View.Font = font; f.Write("MMMMMMMMMM|\r\n▀▀▀▀▀▀▀▀▀▀|\r\n中文😀\r\nMMMMMMMMMM|");
                        Equal(f.View.CellSize.Width * 10, f.View.GetPositionFromCharIndex(10).X - f.View.GetPositionFromCharIndex(0).X);
                        for (int row = 1; row < 4; row++) Equal(f.View.CellSize.Height, Position(f.View, row).Y - Position(f.View, row - 1).Y);
                        AssertGrid(f.View);
                    }
                });
            }
            test("Terminal display rejects proportional font substitution", () => {
                using (var f = new Fixture()) using (var font = new Font("Arial", 11F)) {
                    f.View.Font = font; True(f.View.Font.Name != "Arial"); f.Write("MiW ▀▄█\r\nMMMMMMM");
                    for (int i = 1; i < 7; i++) Equal(f.View.CellSize.Width, f.View.GetPositionFromCharIndex(i).X - f.View.GetPositionFromCharIndex(i - 1).X);
                }
            });
            test("Terminal display font scaling preserves existing banner and emoji font runs", () => {
                using (var f = new Fixture()) using (var font = new Font("Consolas", 16F)) {
                    f.Write("▀▄█MMMM\r\n中文😀\r\n▀▄█MMMM"); string before = f.View.Text;
                    f.View.Font = font; Pump(30); Equal(before, f.View.Text);
                    f.View.Select(0, 7); True(f.View.SelectionFont != null); Equal(f.View.Font.Name, f.View.SelectionFont.Name);
                    int emoji = f.View.Text.IndexOf("😀", StringComparison.Ordinal); f.View.Select(emoji, 2);
                    True(f.View.SelectionFont.Name.StartsWith("Segoe UI", StringComparison.Ordinal));
                    for (int row = 1; row < 3; row++) Equal(f.View.CellSize.Height, Position(f.View, row).Y - Position(f.View, row - 1).Y);
                }
            });
            test("Terminal display counts only complete cells inside native text rectangle", () => {
                using (var f = new Fixture()) {
                    f.Write(new string('M', 150) + "\r\nline\r\n"); AssertGrid(f.View);
                    int column = (int)f.View.Columns;
                    Point first = f.View.GetPositionFromCharIndex(0), last = f.View.GetPositionFromCharIndex(column);
                    True(last.X <= f.View.ViewportBounds.Right); True(last.X + f.View.CellSize.Width > f.View.ViewportBounds.Right);
                    Equal(column * f.View.CellSize.Width, last.X - first.X);
                }
            });
            test("Terminal display tiny viewport does not invent 20 columns or 6 rows", () => {
                using (var f = new Fixture()) {
                    f.View.Dock = DockStyle.None; f.View.SetBounds(0, 0, 70, 30); Pump(30);
                    True(f.View.Columns < 20); True(f.View.Rows < 6); True(f.View.Columns >= 1 && f.View.Rows >= 1); AssertGrid(f.View);
                }
            });
            test("Terminal display binding synchronizes immediately and retries until session ready", () => {
                using (var f = new Fixture()) {
                    var peer = new Session(); f.View.Bind(peer); True(peer.Attempts > 0); Equal(0, peer.Accepted);
                    f.Window.ClientSize = new Size(1040, 640); peer.Ready = true;
                    Until(() => peer.Columns == f.View.Columns && peer.Rows == f.View.Rows);
                    int accepted = peer.Accepted; Pump(80); Equal(accepted, peer.Accepted); Equal(0, peer.Sends);
                }
            });
            test("Terminal display resize and font changes update the existing session only", () => {
                using (var f = new Fixture()) {
                    var peer = new Session { Ready = true }; f.View.Bind(peer); Equal(f.View.Columns, peer.Columns); Equal(f.View.Rows, peer.Rows);
                    uint initial = peer.Columns; f.Window.ClientSize = new Size(1040, 640);
                    Until(() => peer.Columns != initial && peer.Columns == f.View.Columns && peer.Rows == f.View.Rows);
                    using (var font = new Font("Consolas", 16F)) { f.View.Font = font; Until(() => peer.Columns == f.View.Columns && peer.Rows == f.View.Rows); AssertGrid(f.View); }
                    Equal(0, peer.Sends);
                }
            });
            test("Terminal display scrollbar changes resynchronize the usable rectangle", () => {
                using (var f = new Fixture()) {
                    var peer = new Session { Ready = true }; f.View.Bind(peer); Size before = f.View.ClientSize;
                    f.Write(new string('M', 160) + string.Concat(Enumerable.Repeat("\r\nscroll", 80)));
                    Until(() => peer.Columns == f.View.Columns && peer.Rows == f.View.Rows);
                    True(f.View.ClientSize.Width < before.Width && f.View.ClientSize.Height < before.Height); AssertGrid(f.View);
                }
            });
            test("Terminal display recreated handle retains font binding and viewport synchronization", () => {
                using (var f = new Fixture()) {
                    var peer = new Session { Ready = true }; f.View.Bind(peer);
                    f.Write("▀▄█MMMM\r\n中文😀\r\n"); string before = f.View.Text;
                    typeof(Control).GetMethod("RecreateHandle", Private).Invoke(f.View, null); Pump(40);
                    Equal(before, f.View.Text); f.View.Select(0, 7); Equal(f.View.Font.Name, f.View.SelectionFont.Name);
                    f.Write("▀▄█MMMM\r\n▀▄█MMMM"); f.View.Select(0, 7); Equal(f.View.Font.Name, f.View.SelectionFont.Name);
                    Until(() => peer.Columns == f.View.Columns && peer.Rows == f.View.Rows); AssertGrid(f.View);
                }
            });
            test("Terminal display minimize retains last visible size and restore resynchronizes", () => {
                using (var f = new Fixture()) {
                    var peer = new Session { Ready = true }; f.View.Bind(peer); uint columns = peer.Columns, rows = peer.Rows;
                    f.Window.WindowState = FormWindowState.Minimized; Pump(60); Equal(columns, peer.Columns); Equal(rows, peer.Rows);
                    var snapshot = f.View.GetViewportSize(); Equal(columns, snapshot.Item1); Equal(rows, snapshot.Item2);
                    f.Window.WindowState = FormWindowState.Normal; Pump(60); Until(() => peer.Columns == f.View.Columns && peer.Rows == f.View.Rows);
                }
            });
        }
        private static void AssertGrid(TerminalView view)
        {
            var rect = new NativeRect(); SendMessage(view.Handle, 0x00B2, IntPtr.Zero, ref rect);
            Rectangle bounds = Rectangle.Intersect(view.ClientRectangle, Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            Equal(bounds, view.ViewportBounds);
            Equal((uint)Math.Max(1, Math.Min(1000, bounds.Width / view.CellSize.Width)), view.Columns);
            Equal((uint)Math.Max(1, Math.Min(1000, bounds.Height / view.CellSize.Height)), view.Rows);
        }
        private static Point Position(TerminalView view, int row) { return view.GetPositionFromCharIndex(view.GetFirstCharIndexFromLine(row)); }
        private static void Capture(Form form, string name)
        {
            DirectoryInfo root = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "iStoreOS.PluginHelper.sln"))) root = root.Parent;
            if (root == null) throw new DirectoryNotFoundException();
            string folder = Path.Combine(root.FullName, "artifacts", "terminal-display-20261028"); Directory.CreateDirectory(folder);
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(folder, name)); }
        }
        private sealed class Fixture : IDisposable
        {
            public readonly Form Window = new Form { ClientSize = new Size(800, 440), StartPosition = FormStartPosition.Manual, Location = new Point(40, 40) };
            public readonly TerminalView View = new TerminalView();
            public Fixture() { Window.Controls.Add(View); Window.Show(); Pump(20); }
            public void Write(string text) { View.QueueOutput(text); View.RenderPending(); Application.DoEvents(); }
            public void Dispose() { Window.Close(); Window.Dispose(); }
        }
        private sealed class Session : IInteractiveTerminalSession
        {
            public event Action<string> Output { add { } remove { } }
            public bool Ready; public int Attempts, Accepted, Sends; public uint Columns, Rows;
            public bool Send(string text) { Sends++; return true; }
            public bool Resize(uint columns, uint rows) { Attempts++; if (!Ready) return false; Accepted++; Columns = columns; Rows = rows; return true; }
        }
        private static void Pump(int milliseconds) { var watch = Stopwatch.StartNew(); while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(1); } }
        private static void Until(Func<bool> done) { var watch = Stopwatch.StartNew(); while (!done()) { if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("Display synchronization timed out."); Application.DoEvents(); Thread.Sleep(1); } }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception("Expected " + expected + "; actual " + actual); }
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr param, ref NativeRect rect);
    }
}
