using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace iStoreOS.PluginHelper.Utilities
{
    public sealed class UiResources : IDisposable
    {
        private const string Prefix = "iStoreOS.PluginHelper.Resources.Icons.";
        private readonly Dictionary<string, Image> images = new Dictionary<string, Image>();
        private readonly List<Icon> icons = new List<Icon>();
        private readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        public readonly List<string> Warnings = new List<string>();
        public Font Font(float size, FontStyle style)
        { return Font(UiTheme.FontFamily, size, style); }
        public Font Font(string family, float size, FontStyle style)
        {
            string key = family + ":" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + style;
            Font font;
            if (!fonts.TryGetValue(key, out font)) { font = new Font(family, size, style); fonts.Add(key, font); }
            return font;
        }
        public Image Image(string name, int size)
        {
            string key = name + ":" + size;
            Image cached;
            if (images.TryGetValue(key, out cached)) return cached;
            try
            {
                using (var stream = typeof(UiResources).Assembly.GetManifestResourceStream(Prefix + name + ".png"))
                {
                    if (stream == null) throw new InvalidOperationException("资源不存在");
                    using (var source = System.Drawing.Image.FromStream(stream))
                    {
                        var scaled = new Bitmap(size, size);
                        using (var graphics = Graphics.FromImage(scaled))
                        {
                            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            graphics.DrawImage(source, new Rectangle(0, 0, size, size));
                        }
                        images[key] = scaled; return scaled;
                    }
                }
            }
            catch (Exception) { Warnings.Add("图标加载失败：" + name + "；已保留文字界面。"); images[key] = null; return null; }
        }
        public Icon WindowIcon()
        {
            try
            {
                using (var stream = typeof(UiResources).Assembly.GetManifestResourceStream(Prefix + "app-logo.ico"))
                using (var source = new Icon(stream))
                { var icon = (Icon)source.Clone(); icons.Add(icon); return icon; }
            }
            catch (Exception) { Warnings.Add("窗口图标加载失败；使用文字界面。"); return null; }
        }
        public Image Image(string name, int size, Color tint)
        {
            string key = name + ":" + size + ":" + tint.ToArgb(); Image cached;
            if (images.TryGetValue(key, out cached)) return cached;
            var original = Image(name, size); if (original == null) return null;
            var toned = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(toned))
            using (var attributes = new ImageAttributes())
            {
                // Preserve white highlights and alpha while recoloring the existing icon.
                float r = (1 - tint.R / 255F) / 0.6F, g = (1 - tint.G / 255F) / 0.6F, b = (1 - tint.B / 255F) / 0.6F;
                attributes.SetColorMatrix(new ColorMatrix(new[] {
                    new[] { .299F * r, .299F * g, .299F * b, 0F, 0F },
                    new[] { .587F * r, .587F * g, .587F * b, 0F, 0F },
                    new[] { .114F * r, .114F * g, .114F * b, 0F, 0F },
                    new[] { 0F, 0F, 0F, 1F, 0F }, new[] { 1-r, 1-g, 1-b, 0F, 1F } }));
                graphics.DrawImage(original, new Rectangle(0, 0, size, size), 0, 0, size, size, GraphicsUnit.Pixel, attributes);
            }
            images[key] = toned; return toned;
        }
        public void Dispose()
        {
            foreach (Image image in images.Values) if (image != null) image.Dispose();
            foreach (Icon icon in icons) icon.Dispose();
            foreach (Font font in fonts.Values) font.Dispose();
            fonts.Clear();
            images.Clear(); icons.Clear();
        }
    }
    public enum UiTone { Neutral, Success, Warning, Error }
    public static class UiTheme
    {
        public static readonly string FontFamily = FindFontFamily();
        private static string FindFontFamily()
        { try { using (var family = new FontFamily("Microsoft YaHei UI")) return family.Name; } catch (ArgumentException) { return SystemFonts.MessageBoxFont.FontFamily.Name; } }
        public static readonly Color Background = ColorTranslator.FromHtml("#EEF3F8");
        public static readonly Color Ink = ColorTranslator.FromHtml("#16324A");
        public static readonly Color BodyText = ColorTranslator.FromHtml("#20384F");
        public static readonly Color HeadingText = ColorTranslator.FromHtml("#183B56");
        public static readonly Color SecondaryText = ColorTranslator.FromHtml("#586F84");
        public static readonly Color FieldText = ColorTranslator.FromHtml("#35536D");
        public static readonly Color Muted = ColorTranslator.FromHtml("#7D90A3");
        public static readonly Color Subtitle = ColorTranslator.FromHtml("#6F8397");
        public static readonly Color VersionText = ColorTranslator.FromHtml("#60758A");
        public static readonly Color PluginEmptyText = ColorTranslator.FromHtml("#667C91");
        public static readonly Color Blue = ColorTranslator.FromHtml("#1677FF");
        public static readonly Color PrimaryHover = ColorTranslator.FromHtml("#2E89FF");
        public static readonly Color PrimaryPressed = ColorTranslator.FromHtml("#0F68E4");
        public static readonly Color Light = ColorTranslator.FromHtml("#F2F7FD");
        public static readonly Color Border = ColorTranslator.FromHtml("#DCE5EE");
        public static readonly Color CardBorder = ColorTranslator.FromHtml("#D5E1EC");
        public static readonly Color InputFill = ColorTranslator.FromHtml("#F8FBFE");
        public static readonly Color InputBorder = ColorTranslator.FromHtml("#C7D6E5");
        public static readonly Color InputHoverBorder = ColorTranslator.FromHtml("#AFC8E1");
        public static readonly Color FocusBorder = ColorTranslator.FromHtml("#3182F6");
        public static readonly Color Disabled = ColorTranslator.FromHtml("#DCE6F1");
        public static readonly Color DisabledText = ColorTranslator.FromHtml("#8A9CAE");
        public static readonly Color PrimaryDisabled = ColorTranslator.FromHtml("#D3DFEC");
        public static readonly Color PrimaryDisabledText = ColorTranslator.FromHtml("#74889B");
        public static readonly Color SecondaryBlue = ColorTranslator.FromHtml("#1267D6");
        public static readonly Color SecondaryFill = ColorTranslator.FromHtml("#F5F9FF");
        public static readonly Color SecondaryBorder = ColorTranslator.FromHtml("#7FAFF0");
        public static readonly Color SecondaryHover = ColorTranslator.FromHtml("#EAF3FF");
        public static readonly Color SecondaryHoverBorder = ColorTranslator.FromHtml("#4A91EC");
        public static readonly Color SecondaryPressed = ColorTranslator.FromHtml("#DBEBFF");
        public static readonly Color ButtonText = ColorTranslator.FromHtml("#205FAD");
        public static readonly Color ButtonBorder = ColorTranslator.FromHtml("#A9C8EA");
        public static readonly Color ButtonHover = ColorTranslator.FromHtml("#EDF6FF");
        public static readonly Color ButtonHoverBorder = ColorTranslator.FromHtml("#6EA9EC");
        public static readonly Color ButtonPressed = ColorTranslator.FromHtml("#DFEEFF");
        public static readonly Color IconFill = ColorTranslator.FromHtml("#F4F8FC");
        public static readonly Color IconHover = ColorTranslator.FromHtml("#EAF4FF");
        public static readonly Color IconHoverBorder = ColorTranslator.FromHtml("#8AB9F7");
        public static readonly Color IconPressed = ColorTranslator.FromHtml("#DDEEFF");
        public static readonly Color DropFill = ColorTranslator.FromHtml("#F5F9FF");
        public static readonly Color DropBorder = ColorTranslator.FromHtml("#83B5F5");
        public static readonly Color DropHover = ColorTranslator.FromHtml("#EAF4FF");
        public static readonly Color DropHoverBorder = ColorTranslator.FromHtml("#2F80ED");
        public static readonly Color CaptionBlue = ColorTranslator.FromHtml("#6688AA");
        public static readonly Color IconAccent = ColorTranslator.FromHtml("#EAF4FF");
        public static readonly Color UploadAccent = ColorTranslator.FromHtml("#E5F2FF");
        public static readonly Color UploadIcon = ColorTranslator.FromHtml("#1681F7");
        public static readonly Color FileIcon = ColorTranslator.FromHtml("#89A0B6");
        public static readonly Color Waiting = ColorTranslator.FromHtml("#8FA2B5");
        public static readonly Color WaitingText = ColorTranslator.FromHtml("#61778B");
        public static readonly Color ConnectionSuccess = ColorTranslator.FromHtml("#188946");
        public static readonly Color CompatibleText = ColorTranslator.FromHtml("#238A4B");
        public static readonly Color StatusText = ColorTranslator.FromHtml("#405B73");
        public static readonly Color StatusHelp = ColorTranslator.FromHtml("#71869B");
        public static readonly Color StatusSuccess = ColorTranslator.FromHtml("#198F46");
        public static readonly Color StatusWarning = ColorTranslator.FromHtml("#DB8B12");
        public static readonly Color LinkNormal = ColorTranslator.FromHtml("#1267D6");
        public static readonly Color LinkHover = ColorTranslator.FromHtml("#075CBF");
        public static readonly Color Success = ColorTranslator.FromHtml("#20A34A");
        public static readonly Color Warning = ColorTranslator.FromHtml("#E89A16");
        public static readonly Color Error = ColorTranslator.FromHtml("#D64545");
        public static readonly Color NavigationFill = ColorTranslator.FromHtml("#F5F8FC");
        public static readonly Color NavigationText = ColorTranslator.FromHtml("#5B7085");
        public static readonly Color NavigationSelected = ColorTranslator.FromHtml("#EAF4FF");
        public static readonly Color EmptyHelp = ColorTranslator.FromHtml("#8193A6");
        public static readonly Color RowSeparator = ColorTranslator.FromHtml("#EEF2F6");
        public static readonly Color GridAlternate = ColorTranslator.FromHtml("#FAFCFE");
        public static readonly Color GridLine = ColorTranslator.FromHtml("#E7EDF3");
        public static readonly Color LogBackground = ColorTranslator.FromHtml("#111827");
        public static readonly Color LogForeground = ColorTranslator.FromHtml("#D5E2EE");
        public static Color Fore(UiTone tone)
        { return tone == UiTone.Success ? Success : tone == UiTone.Warning ? Warning : tone == UiTone.Error ? Error : SecondaryText; }
        public static Color Fill(UiTone tone)
        { return tone == UiTone.Success ? Color.FromArgb(233, 247, 239) : tone == UiTone.Warning ? Color.FromArgb(255, 247, 228) : tone == UiTone.Error ? Color.FromArgb(255, 238, 238) : Light; }
        public static void Button(System.Windows.Forms.Button button, bool primary)
        {
            button.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(207, 218, 232);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(42, 129, 244) : Color.FromArgb(235, 243, 254);
            var font = new Font(FontFamily, 9F, primary ? FontStyle.Bold : FontStyle.Regular);
            button.Font = font; button.Disposed += (sender, args) => font.Dispose();
            button.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            button.ImageAlign = ContentAlignment.MiddleLeft;
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Padding = new System.Windows.Forms.Padding(6, 0, 6, 0);
            Action refresh = () =>
            {
                button.BackColor = button.Enabled ? primary ? Blue : Color.White : Color.FromArgb(232, 238, 246);
                button.ForeColor = button.Enabled ? primary ? Color.White : Ink : Color.FromArgb(139, 152, 169);
            };
            button.EnabledChanged += (sender, args) => refresh(); refresh();
        }
    }
}
