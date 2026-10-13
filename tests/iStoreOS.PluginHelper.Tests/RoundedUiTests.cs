using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Controls;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class RoundedUiTests
    {
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flag);
        public static void Run(Action<string, Action> test)
        {
            test("V0.3.2.2 rounded text forwards both directions and TextChanged once", () =>
            {
                using (var input = new RoundedTextBox())
                {
                    int changes = 0; input.TextChanged += (sender, args) => changes++;
                    input.Text = "外层值"; Equal("外层值", input.Editor.Text); Equal(1, changes);
                    input.Editor.Text = "编辑值"; Equal("编辑值", input.Text); Equal(2, changes);
                    input.Text = "编辑值"; Equal(2, changes); Equal(BorderStyle.None, input.Editor.BorderStyle);
                }
            });
            test("V0.3.2.2 rounded password retains native editor semantics", () =>
            {
                using (var input = new RoundedTextBox())
                {
                    input.PasswordChar = '●'; input.Text = "password-fixture"; Equal('●', input.Editor.PasswordChar);
                    input.UseSystemPasswordChar = true; True(input.Editor.UseSystemPasswordChar);
                    input.UseSystemPasswordChar = false; input.PasswordChar = '\0'; Equal('\0', input.Editor.PasswordChar); Equal("password-fixture", input.Text);
                    input.Editor.SelectionStart = 3; Equal(3, input.Editor.SelectionStart); input.Editor.Clear(); Equal("", input.Text);
                }
            });
            test("V0.3.2.2 native KeyDown also reaches rounded wrapper without duplicate", () =>
            {
                using (var input = new RoundedTextBox())
                {
                    int inner = 0, outer = 0; input.Editor.KeyDown += (sender, args) => inner++; input.KeyDown += (sender, args) => outer++;
                    Raise(input.Editor, "OnKeyDown", new KeyEventArgs(Keys.A)); Equal(1, inner); Equal(1, outer);
                }
            });
            test("V0.3.2.2 main password eye still changes same native editor only", () =>
            {
                using (var form = new MainForm())
                {
                    form.Show(); Application.DoEvents();
                    var wrapper = (RoundedTextBox)Control(form, "passwordInput"); var editor = (TextBox)Control(form, "passwordBox"); Equal(wrapper.Editor, editor); editor.Text = "visual-fixture-secret";
                    ((Button)Control(form, "passwordToggleButton")).PerformClick(); Equal('\0', editor.PasswordChar); Equal("visual-fixture-secret", wrapper.Text);
                    ((Button)Control(form, "passwordToggleButton")).PerformClick(); Equal('●', editor.PasswordChar); Equal(false, Control(form, "logBox").Text.Contains("visual-fixture-secret"));
                    UiTests.Invoke(form, "SetConnectionInputs", false); Equal(false, editor.Enabled); Equal(false, Control(form, "passwordToggleButton").Enabled);
                    form.Close();
                }
            });
            test("V0.3.2.2 rounded button keeps PerformClick enable and AcceptButton", () =>
            {
                using (var host = new Form())
                using (var button = new RoundedButton { Text = "fixture", Kind = RoundedButtonKind.Primary })
                {
                    int clicks = 0; button.Click += (sender, args) => clicks++; host.Controls.Add(button); host.AcceptButton = button;
                    host.Show(); Application.DoEvents();
                    button.Enabled = false; button.PerformClick(); Equal(0, clicks); Equal(UiTheme.PrimaryDisabled, button.DisplayFill); Equal(UiTheme.PrimaryDisabledText, button.DisplayText);
                    button.Enabled = true; button.PerformClick(); Equal(1, clicks); Equal(UiTheme.Blue, button.DisplayFill); Equal(Color.White, button.DisplayText);
                    Raise(host, "ProcessDialogKey", Keys.Enter); Equal(2, clicks);
                    host.Close();
                }
            });
            test("V0.3.2.2 primary gate follows compatibility busy and retry states", () =>
            {
                using (var form = new MainForm())
                {
                    var button = (RoundedButton)Control(form, "installButton"); Equal(RoundedButtonKind.Primary, button.Kind); Equal(false, button.Enabled);
                    UiTests.Set(form, "router", Router()); UiTests.Set(form, "plugin", new PluginPackageAnalyzer().AnalyzeFileName("PassWall_1_x86_64_all_sdk_25.12.run")); UiTests.Invoke(form, "RefreshCompatibility"); True(button.Enabled); Equal(UiTheme.Blue, button.DisplayFill);
                    UiTests.Set(form, "isInstallBusy", true); UiTests.Invoke(form, "RefreshInstallButton"); Equal(false, button.Enabled); Equal("正在安装…", button.Text); Equal(UiTheme.PrimaryDisabled, button.DisplayFill);
                    UiTests.Set(form, "isInstallBusy", false); UiTests.Invoke(form, "DisplayWorkflowResult", new InstallationWorkflowResult { State = InstallationState.RetryableFailure, Failure = InstallFailureClassifier.Failure(InstallationFailureKind.MirrorsExhausted) }); Equal("再次尝试", button.Text); True(button.Enabled);
                }
            });
            test("V0.3.2.2 button hover press release and disable use same palette", () =>
            {
                using (var button = new RoundedButton { Kind = RoundedButtonKind.Primary })
                {
                    Raise(button, "OnMouseEnter", EventArgs.Empty); Equal(UiTheme.PrimaryHover, button.DisplayFill);
                    Raise(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0)); Equal(UiTheme.PrimaryPressed, button.DisplayFill);
                    Raise(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0)); Equal(UiTheme.PrimaryHover, button.DisplayFill);
                    button.Enabled = false; Equal(UiTheme.PrimaryDisabled, button.DisplayFill); button.Enabled = true; Equal(UiTheme.Blue, button.DisplayFill);
                }
            });
            test("V0.3.2.2 rounded drag area accepts only original run file rule", () => Fixture(path =>
            {
                using (var form = new MainForm())
                {
                    var zone = (RoundedDropZone)Control(form, "dropArea"); var args = Drag(path); Raise(zone, "OnDragEnter", args); Equal(DragDropEffects.Copy, args.Effect); True(zone.Highlighted);
                    Raise(zone, "OnDragDrop", args); Equal(false, zone.Highlighted); Equal(path, Control(form, "filePathBox").Text); True(Control(form, "pluginNameLabel").Text.Contains("PassWall"));
                    var bad = Drag("invalid-fixture.txt"); Raise(zone, "OnDragEnter", bad); Equal(DragDropEffects.None, bad.Effect); Equal(false, zone.Highlighted);
                    UiTests.Set(form, "isInstallBusy", true); var busy = Drag(path); Raise(zone, "OnDragEnter", busy); Equal(DragDropEffects.None, busy.Effect); UiTests.Set(form, "isInstallBusy", false);
                }
            }));
            test("V0.3.2.2 drop caption retains child drag feedback and clears leave", () => Fixture(path =>
            {
                using (var form = new MainForm())
                {
                    var zone = (RoundedDropZone)Control(form, "dropArea"); var args = Drag(path); Raise(Control(form, "dropLabel"), "OnDragEnter", args); Equal(DragDropEffects.Copy, args.Effect); True(zone.Highlighted);
                    Raise(Control(form, "dropLabel"), "OnDragLeave", EventArgs.Empty); Equal(false, zone.Highlighted);
                }
            }));
            test("V0.3.2.2 file selection retains metadata and compatible enable gate", () => Fixture(path =>
            {
                using (var form = new MainForm())
                {
                    UiTests.Set(form, "router", Router()); UiTests.Invoke(form, "SelectPlugin", path);
                    Equal(Path.GetFileName(path), Control(form, "fileNameBox").Text); Equal(path, Control(form, "filePathBox").Text); True(Control(form, "compatibilityLabel").Text.Contains("当前设备兼容")); True(Control(form, "installButton").Enabled);
                    Equal(0, form.Controls.Find("filePathBox", true).Length);
                }
            }));
            test("V0.3.2.2 advanced link opens existing independent rounded view", () =>
            {
                using (var form = new MainForm())
                {
                    var link = (LinkLabel)Control(form, "toggleLogButton"); Equal(LinkBehavior.HoverUnderline, link.LinkBehavior);
                    Raise(link, "OnLinkClicked", new LinkLabelLinkClickedEventArgs(link.Links[0])); var view = UiTests.Field<AdvancedInfoForm>(form, "advancedInfoForm"); True(view.Visible);
                    Equal(12, ((RoundedPanel)Control(form, "systemTabGroup")).Radius); Equal(new Size(800, 600), view.ClientSize);
                }
            });
            test("V0.3.2.2 design geometry matches compact reference sizes", () =>
            {
                using (var form = new MainForm())
                {
                    form.Show(); Application.DoEvents(); Equal(new Size(760, 500), form.ClientSize); Equal(UiTheme.Background, form.BackColor);
                    Equal(new Size(720, 118), Control(form, "connectionCard").Size); Equal(new Size(720, 164), Control(form, "pluginCard").Size); Equal(new Size(688, 72), Control(form, "dropArea").Size);
                    Equal(new Size(200, 40), Control(form, "installButton").Size); Equal(15F, Control(form, "mainTitleLabel").Font.Size); Equal(new Size(34, 32), Control(form, "passwordToggleButton").Size);
                    Equal(new Size(124, 32), Control(form, "connectButton").Size); Equal(new Size(180, 32), Control(form, "hostInput").Size); form.Close();
                }
            });
            test("V0.3.2.2 all user facing main input and buttons have native rounded hosts", () =>
            {
                using (var form = new MainForm())
                {
                    foreach (string name in new[] { "hostInput", "usernameInput", "passwordInput" }) True(Control(form, name) is RoundedTextBox);
                    foreach (string name in new[] { "connectButton", "chooseButton", "passwordToggleButton", "installButton" }) True(Control(form, name) is RoundedButton);
                    Equal(14, ((RoundedPanel)Control(form, "connectionCard")).Radius); Equal(12, ((RoundedDropZone)Control(form, "dropArea")).Radius); Equal(12, ((RoundedButton)Control(form, "installButton")).Radius);
                }
            });
            test("V0.3.2.2 status uses real painted dot and no circle character", () =>
            {
                using (var form = new MainForm())
                {
                    var dot = (StatusDot)Control(form, "connectionDot"); Equal(UiTheme.Waiting, dot.DotColor); Equal(false, Control(form, "connectionLabel").Text.Contains("●"));
                    UiTests.Set(form, "router", Router()); UiTests.Invoke(form, "DisplayEnvironment"); Equal(UiTheme.Success, dot.DotColor);
                    UiTests.Invoke(form, "SetStatusAppearance", Control(form, "connectionLabel"), UiTone.Error); Equal(UiTheme.Error, dot.DotColor); Equal(new Size(8, 8), dot.Size);
                }
            });
            test("V0.3.2.2 state changes preserve all visual bounds", () =>
            {
                using (var form = new MainForm())
                {
                    form.Show(); Application.DoEvents(); var bounds = new Dictionary<string, Rectangle>();
                    foreach (string name in new[] { "connectionCard", "pluginCard", "hostInput", "dropArea", "installButton", "installCard", "toggleLogButton" }) bounds.Add(name, Control(form, name).Bounds);
                    UiTests.Set(form, "router", Router()); UiTests.Invoke(form, "DisplayEnvironment"); UiTests.Invoke(form, "SetInstallSummary", "long-fixture-result", "first line\r\nsecond line", UiTone.Warning, true); Application.DoEvents();
                    foreach (var item in bounds) Equal(item.Value, Control(form, item.Key).Bounds); Equal(new Size(760, 500), form.ClientSize); form.Close();
                }
            });
            test("V0.3.2.2 rounded controls resize and paint zero tiny normal sizes safely", () =>
            {
                using (var panel = new RoundedPanel()) using (var button = new RoundedButton()) using (var input = new RoundedTextBox()) using (var zone = new RoundedDropZone())
                {
                    foreach (var control in new Control[] { panel, button, input, zone })
                    foreach (var size in new[] { Size.Empty, new Size(1, 1), new Size(4, 3), new Size(180, 40), new Size(360, 80) })
                    {
                        control.Size = size;
                        if (size.Width > 0 && size.Height > 0) using (var bitmap = new Bitmap(size.Width, size.Height)) control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
                    }
                    Equal(false, panel.Region.IsVisible(0, 0)); True(panel.Region.IsVisible(panel.Width / 2, panel.Height / 2));
                }
            });
            test("V0.3.2.2 rounded paths clamp radius and region refreshes on resize", () =>
            {
                using (var path = RoundedDrawing.Path(new RectangleF(0, 0, 8, 6), 100))
                { var bounds = path.GetBounds(); True(Math.Abs(bounds.X) < 0.001F && Math.Abs(bounds.Y) < 0.001F && Math.Abs(bounds.Width - 8) < 0.001F && Math.Abs(bounds.Height - 6) < 0.001F); }
                using (var control = new RoundedPanel { Size = new Size(120, 60) })
                { Equal(false, control.Region.IsVisible(0, 0)); True(control.Region.IsVisible(119, 30)); control.Width = 240; True(control.Region.IsVisible(239, 30)); Equal(false, control.Region.IsVisible(239, 0)); }
            });
            test("V0.3.2.2 repeated rounded region paint disposal does not leak GDI handles", () =>
            {
                CycleControls(8); GC.Collect(); GC.WaitForPendingFinalizers(); int before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                CycleControls(160); GC.Collect(); GC.WaitForPendingFinalizers(); int after = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
                if (after - before > 8) throw new Exception("GDI handles grew " + (after - before));
            });
            test("V0.3.2.2 missing icon still paints button and retains click", () =>
            {
                using (var resources = new UiResources()) using (var button = new RoundedButton { Text = "无图标仍可用", Size = new Size(160, 40) })
                {
                    button.Image = resources.Image("rounded-missing-fixture", 36); Equal(null, button.Image); int clicks = 0; button.Click += (sender, args) => clicks++;
                    using (var bitmap = new Bitmap(160, 40)) button.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); button.PerformClick(); Equal(1, clicks); Equal(1, resources.Warnings.Count);
                }
            });
            test("V0.3.2.3 native editor and rounded background render the same input layer", () =>
            {
                using (var input = new RoundedTextBox()) using (var bitmap = new Bitmap(input.Width, input.Height))
                {
                    input.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    Equal(input.Editor.BackColor, bitmap.GetPixel(2, input.Height / 2));
                    Equal(input.Editor.BackColor, bitmap.GetPixel(input.Width / 2, input.Height / 2));
                    Equal(false, input.Editor.BackColor == Color.White);
                }
            });
            test("V0.3.2.3 input hover focus and disable preserve text and geometry", () =>
            {
                using (var host = new Form()) using (var input = new RoundedTextBox { Text = "input-state-fixture" })
                {
                    host.Controls.Add(input); var bounds = input.Bounds;
                    Raise(input, "OnMouseEnter", EventArgs.Empty); Equal(UiTheme.InputHoverBorder, input.DisplayBorder);
                    host.Show(); input.Editor.Focus(); Application.DoEvents(); True(input.Editor.Focused); Equal(UiTheme.FocusBorder, input.DisplayBorder);
                    using (var bitmap = new Bitmap(input.Width, input.Height))
                    { input.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); var pixel = bitmap.GetPixel(1, input.Height / 2); True(Math.Abs(pixel.R - UiTheme.FocusBorder.R) < 4 && Math.Abs(pixel.G - UiTheme.FocusBorder.G) < 4 && Math.Abs(pixel.B - UiTheme.FocusBorder.B) < 4); }
                    input.Editor.Enabled = false; Equal(UiTheme.InputBorder, input.DisplayBorder); Equal("input-state-fixture", input.Text); Equal(bounds, input.Bounds); host.Close();
                }
            });
            test("V0.3.2.3 secondary icon and file buttons render distinct interaction states", () =>
            {
                foreach (var kind in new[] { RoundedButtonKind.Secondary, RoundedButtonKind.Icon, RoundedButtonKind.Neutral })
                using (var button = new RoundedButton { Kind = kind, Size = new Size(124, 32), Text = "" })
                using (var bitmap = new Bitmap(124, 32))
                {
                    button.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); Color normal = bitmap.GetPixel(62, 16); var bounds = button.Bounds;
                    Raise(button, "OnMouseEnter", EventArgs.Empty); button.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); Color hover = bitmap.GetPixel(62, 16); Equal(false, normal == hover);
                    Raise(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0)); button.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); Color pressed = bitmap.GetPixel(62, 16); Equal(false, hover == pressed);
                    button.Enabled = false; button.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); Equal(UiTheme.Disabled, bitmap.GetPixel(62, 16)); Equal(bounds, button.Bounds);
                }
            });
            test("V0.3.2.7 rounded roles cover prefilled edge pixels at all scales", () =>
            {
                foreach (float scale in new[] { 1F, 1.25F, 1.5F, 2F })
                using (var host = new Form { ClientSize = new Size(480, 180), BackColor = UiTheme.Background, Opacity = 0 })
                using (var surface = new RoundedPanel { Dock = DockStyle.Fill })
                {
                    host.Controls.Add(surface); host.Show();
                    foreach (var button in new RoundedButton[] {
                        new RoundedButton { Kind = RoundedButtonKind.Primary, Radius = 12 },
                        new RoundedButton { Kind = RoundedButtonKind.Secondary }, new RoundedButton { Kind = RoundedButtonKind.Neutral },
                        new RoundedIconButton(), new SegmentButton { Selected = true } })
                    using (button)
                    {
                        button.Size = new Size(200, 40); button.Location = new Point(16, 16); button.Text = "";
                        surface.Controls.Add(button); if (scale != 1F) button.Scale(new SizeF(scale, scale)); Application.DoEvents();
                        AssertFreshSurface(button); Raise(button, "OnMouseEnter", EventArgs.Empty); AssertFreshSurface(button);
                        Raise(button, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0)); AssertFreshSurface(button);
                        Raise(button, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 8, 8, 0));
                        button.Enabled = false; AssertFreshSurface(button); surface.Controls.Remove(button);
                    }
                    host.Close();
                }
            });
            test("V0.3.2.7 install enable disable and partial invalidation leave clean edges", () =>
            {
                using (var form = new MainForm())
                {
                    form.Show(); Application.DoEvents(); var button = (RoundedButton)Control(form, "installButton"); var bounds = button.Bounds;
                    AssertFreshSurface(button);
                    for (int i = 0; i < 8; i++)
                    {
                        button.Enabled = true; button.Focus(); AssertFreshSurface(button);
                        button.Enabled = false; button.Invalidate(new Rectangle(0, 0, button.Width, 3)); button.Update(); AssertFreshSurface(button);
                        Raise(button, "OnMouseEnter", EventArgs.Empty); AssertFreshSurface(button);
                        Raise(button, "OnMouseLeave", EventArgs.Empty); Equal(bounds, button.Bounds);
                    }
                    Equal(false, UiTests.Field<bool>(form, "isInstallBusy")); Equal(new Size(760, 500), form.ClientSize); form.Close();
                }
            });
        }
        private static void AssertFreshSurface(Control control)
        {
            using (var bitmap = new Bitmap(control.Width, control.Height))
            {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.Magenta);
                control.DrawToBitmap(bitmap, control.ClientRectangle);
                for (int y = 0; y < bitmap.Height; y++) for (int x = 0; x < bitmap.Width; x++)
                {
                    if (control.Region != null && !control.Region.IsVisible(x + .5F, y + .5F)) continue;
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.R == 255 && pixel.G == 0 && pixel.B == 255) throw new Exception(control.GetType().Name + " left background untouched at " + x + "," + y);
                    bool edge = x < 3 || y < 3 || x >= bitmap.Width - 3 || y >= bitmap.Height - 3;
                    if (edge && pixel.R < 64 && pixel.G < 64 && pixel.B < 64) throw new Exception(control.GetType().Name + " left a dark edge at " + x + "," + y);
                }
            }
        }
        private static void CycleControls(int count)
        {
            for (int i = 0; i < count; i++) using (var panel = new RoundedPanel()) using (var button = new RoundedButton()) using (var input = new RoundedTextBox()) using (var zone = new RoundedDropZone())
            foreach (var control in new Control[] { panel, button, input, zone })
            {
                control.Size = new Size(180, 40); control.CreateControl(); control.Size = new Size(200 + i % 10, 48);
                using (var bitmap = new Bitmap(control.Width, control.Height)) control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
            }
        }
        private static Control Control(MainForm form, string name) { return UiTests.Control(form, name); }
        private static object Raise(object control, string name, object args) { return control.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(control, new[] { args }); }
        private static DragEventArgs Drag(string path) { var data = new DataObject(); data.SetData(DataFormats.FileDrop, new[] { path }); return new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None); }
        private static RouterEnvironment Router() { return new RouterEnvironment { IsConnected = true, Version = "25.12.5", SdkSeries = "25.12", Architecture = "x86_64", PackageManager = PackageManagerKind.APK }; }
        private static void Fixture(Action<string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "rounded-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); string path = Path.Combine(directory, "PassWall_1_x86_64_all_sdk_25.12.run");
            try { File.WriteAllText(path, "metadata fixture only; never execute"); check(path); } finally { File.Delete(path); Directory.Delete(directory); }
        }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    }
}
