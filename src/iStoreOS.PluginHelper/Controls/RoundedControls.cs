using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Controls
{
    public static class RoundedDrawing
    {
        public static GraphicsPath Path(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            if (bounds.Width <= 0 || bounds.Height <= 0) return path;
            float diameter = Math.Min(Math.Max(0, radius) * 2, Math.Min(bounds.Width, bounds.Height));
            if (diameter < 1) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure(); return path;
        }
        public static void Region(Control control, float radius)
        {
            Region previous = control.Region;
            using (var path = Path(new RectangleF(0, 0, control.Width, control.Height), radius))
                control.Region = control.Width > 0 && control.Height > 0 ? new Region(path) : null;
            if (previous != null) previous.Dispose();
        }
        public static void ReleaseRegion(Control control)
        { Region previous = control.Region; control.Region = null; if (previous != null) previous.Dispose(); }
    }

    public class RoundedPanel : Panel
    {
        private float metricScale = 1F;
        private int radius = 14;
        private Color fillColor = Color.White, borderColor = UiTheme.CardBorder;
        public int Radius { get { return radius; } set { radius = Math.Max(0, value); RefreshRegion(); Invalidate(); } }
        public Color FillColor { get { return fillColor; } set { fillColor = value; Invalidate(true); } }
        public Color BorderColor { get { return borderColor; } set { borderColor = value; Invalidate(); } }
        public DashStyle BorderDashStyle { get; set; }
        public float BorderWidth { get; set; }
        protected float MetricScale { get { return metricScale; } }
        public RoundedPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true; BackColor = Color.Transparent; BorderDashStyle = DashStyle.Solid; BorderWidth = 1F; RefreshRegion();
        }
        private void RefreshRegion() { RoundedDrawing.Region(this, radius * metricScale); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RefreshRegion(); }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        { metricScale *= factor.Width; base.ScaleControl(factor, specified); RefreshRegion(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float stroke = BorderWidth * metricScale, inset = stroke / 2F;
            using (var path = RoundedDrawing.Path(new RectangleF(inset, inset, Width - stroke, Height - stroke), radius * metricScale))
            using (var brush = new SolidBrush(fillColor))
            using (var pen = new Pen(borderColor, stroke) { DashStyle = BorderDashStyle })
            { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
        }
        protected override void Dispose(bool disposing)
        { if (disposing) RoundedDrawing.ReleaseRegion(this); base.Dispose(disposing); }
    }

    public enum RoundedButtonKind { Primary, Secondary, Neutral, Icon }
    public class RoundedButton : Button
    {
        private bool hot, pressed;
        private float metricScale = 1F;
        private int radius = 9;
        private RoundedButtonKind kind;
        public int Radius { get { return radius; } set { radius = Math.Max(0, value); RefreshRegion(); Invalidate(); } }
        public RoundedButtonKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        public int IconSize { get; set; }
        public Color DisplayFill { get {
            if (!Enabled) return kind == RoundedButtonKind.Primary ? UiTheme.PrimaryDisabled : UiTheme.Disabled;
            if (kind == RoundedButtonKind.Primary) return pressed ? UiTheme.PrimaryPressed : hot ? UiTheme.PrimaryHover : UiTheme.Blue;
            if (kind == RoundedButtonKind.Secondary) return pressed ? UiTheme.SecondaryPressed : hot ? UiTheme.SecondaryHover : UiTheme.SecondaryFill;
            if (kind == RoundedButtonKind.Icon) return pressed ? UiTheme.IconPressed : hot ? UiTheme.IconHover : UiTheme.IconFill;
            return pressed ? UiTheme.ButtonPressed : hot ? UiTheme.ButtonHover : Color.White;
        } }
        public Color DisplayBorder { get {
            if (!Enabled) return UiTheme.Border;
            if (kind == RoundedButtonKind.Secondary) return hot || pressed ? UiTheme.SecondaryHoverBorder : UiTheme.SecondaryBorder;
            if (kind == RoundedButtonKind.Icon) return hot || pressed ? UiTheme.IconHoverBorder : UiTheme.InputBorder;
            return hot || pressed ? UiTheme.ButtonHoverBorder : UiTheme.ButtonBorder;
        } }
        public Color DisplayText { get { return !Enabled ? kind == RoundedButtonKind.Primary ? UiTheme.PrimaryDisabledText : UiTheme.DisabledText : kind == RoundedButtonKind.Primary ? Color.White : kind == RoundedButtonKind.Secondary ? UiTheme.SecondaryBlue : UiTheme.ButtonText; } }
        public RoundedButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            BackColor = Color.Transparent; UseVisualStyleBackColor = false; IconSize = 18; Cursor = Cursors.Hand; RefreshRegion();
            // Rounded foreground leaves edge pixels for the parent background to paint.
            SetStyle(ControlStyles.Opaque, false);
        }
        private void RefreshRegion() { RoundedDrawing.Region(this, radius * metricScale); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); RefreshRegion(); }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        { metricScale *= factor.Width; base.ScaleControl(factor, specified); RefreshRegion(); }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hot = false; pressed = false; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = metricScale / 2F;
            using (var path = RoundedDrawing.Path(new RectangleF(inset, inset, Width - metricScale, Height - metricScale), radius * metricScale))
            using (var brush = new SolidBrush(DisplayFill))
            {
                e.Graphics.FillPath(brush, path);
                if (kind != RoundedButtonKind.Primary)
                {
                    using (var pen = new Pen(DisplayBorder, metricScale)) e.Graphics.DrawPath(pen, path);
                }
            }
            int iconSize = Image == null ? 0 : (int)Math.Round(IconSize * metricScale);
            var flags = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            int textWidth = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(e.Graphics, Text, Font, new Size(int.MaxValue, int.MaxValue), flags & ~TextFormatFlags.EndEllipsis).Width + (int)Math.Ceiling(2 * metricScale);
            int gap = iconSize > 0 && textWidth > 0 ? (int)(8 * metricScale) : 0;
            int groupWidth = Math.Max(0, Math.Min(Width - (int)(16 * metricScale), iconSize + gap + textWidth));
            int left = (Width - groupWidth) / 2;
            if (Image != null)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var iconBounds = new Rectangle(left, (Height - iconSize) / 2, iconSize, iconSize);
                if (Enabled) e.Graphics.DrawImage(Image, iconBounds);
                else using (var attributes = new ImageAttributes())
                { attributes.SetColorMatrix(new ColorMatrix { Matrix33 = 0.4F }); e.Graphics.DrawImage(Image, iconBounds, 0, 0, Image.Width, Image.Height, GraphicsUnit.Pixel, attributes); }
                left += iconSize + gap;
            }
            if (textWidth > 0) TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(left, 0, Math.Max(1, (Width + groupWidth) / 2 - left), Height), DisplayText, flags);
            if (Focused && ShowFocusCues && Width > 16 * metricScale && Height > 16 * metricScale) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -(int)(6 * metricScale), -(int)(6 * metricScale)), DisplayText, DisplayFill);
        }
        protected override void Dispose(bool disposing)
        { if (disposing) RoundedDrawing.ReleaseRegion(this); base.Dispose(disposing); }
    }
    public sealed class RoundedIconButton : RoundedButton
    { public RoundedIconButton() { Kind = RoundedButtonKind.Icon; Radius = 8; IconSize = 18; } }

    public sealed class RoundedTextBox : UserControl
    {
        private readonly TextBox editor;
        private float metricScale = 1F;
        private bool hot;
        public TextBox Editor { get { return editor; } }
        public int Radius { get { return 8; } }
        public Color DisplayBorder { get { return !editor.Enabled ? UiTheme.InputBorder : editor.Focused ? UiTheme.FocusBorder : hot ? UiTheme.InputHoverBorder : UiTheme.InputBorder; } }
        public override string Text { get { return editor == null ? base.Text : editor.Text; } set { if (editor == null) base.Text = value; else editor.Text = value; } }
        public char PasswordChar { get { return editor.PasswordChar; } set { editor.PasswordChar = value; } }
        public bool UseSystemPasswordChar { get { return editor.UseSystemPasswordChar; } set { editor.UseSystemPasswordChar = value; } }
        public RoundedTextBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            DoubleBuffered = true; BackColor = Color.Transparent; TabStop = false; Size = new Size(180, 32);
            editor = new TextBox { BorderStyle = BorderStyle.None, BackColor = UiTheme.InputFill, ForeColor = UiTheme.Ink, Margin = Padding.Empty };
            Controls.Add(editor);
            editor.TextChanged += (sender, args) => OnTextChanged(EventArgs.Empty);
            editor.GotFocus += (sender, args) => Invalidate(); editor.LostFocus += (sender, args) => Invalidate();
            editor.EnabledChanged += (sender, args) => { editor.BackColor = editor.Enabled ? UiTheme.InputFill : UiTheme.Disabled; if (!editor.Enabled) hot = false; Invalidate(); };
            editor.MouseEnter += (sender, args) => { hot = true; Invalidate(); };
            editor.MouseLeave += (sender, args) => { if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) { hot = false; Invalidate(); } };
            editor.KeyDown += (sender, args) => OnKeyDown(args);
            ArrangeEditor();
        }
        private void ArrangeEditor()
        {
            if (editor == null) return;
            int padding = (int)Math.Round(10 * metricScale);
            editor.SetBounds(padding, Math.Max(0, (Height - editor.PreferredHeight) / 2), Math.Max(1, Width - padding * 2), editor.PreferredHeight);
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); if (editor != null) editor.Font = Font; ArrangeEditor(); }
        protected override bool ScaleChildren { get { return false; } }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); ArrangeEditor(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ArrangeEditor(); RoundedDrawing.Region(this, 8 * metricScale); }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        { metricScale *= factor.Width; base.ScaleControl(factor, specified); ArrangeEditor(); RoundedDrawing.Region(this, 8 * metricScale); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); editor.Focus(); }
        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) { hot = false; Invalidate(); } base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float stroke = editor.Focused && editor.Enabled ? 2F * metricScale : metricScale;
            float inset = stroke / 2;
            using (var path = RoundedDrawing.Path(new RectangleF(inset, inset, Width - stroke, Height - stroke), 8 * metricScale))
            using (var brush = new SolidBrush(editor.Enabled ? UiTheme.InputFill : UiTheme.Disabled))
            using (var pen = new Pen(DisplayBorder, stroke))
            { e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(pen, path); }
        }
        protected override void Dispose(bool disposing)
        { if (disposing) RoundedDrawing.ReleaseRegion(this); base.Dispose(disposing); }
    }

    public sealed class RoundedDropZone : RoundedPanel
    {
        public bool Highlighted { get; private set; }
        public RoundedDropZone() { Radius = 12; BorderDashStyle = DashStyle.Dash; BorderWidth = 1.25F; FillColor = UiTheme.DropFill; BorderColor = UiTheme.DropBorder; AllowDrop = true; }
        public void Highlight(bool value)
        { Highlighted = value && Enabled; FillColor = Highlighted ? UiTheme.DropHover : UiTheme.DropFill; BorderColor = Highlighted ? UiTheme.DropHoverBorder : UiTheme.DropBorder; }
        protected override void OnMouseEnter(EventArgs e) { Highlight(true); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) Highlight(false); base.OnMouseLeave(e); }
        protected override void OnDragEnter(DragEventArgs e) { base.OnDragEnter(e); Highlight(e.Effect != DragDropEffects.None); }
        protected override void OnDragLeave(EventArgs e) { Highlight(false); base.OnDragLeave(e); }
        protected override void OnDragDrop(DragEventArgs e) { Highlight(false); base.OnDragDrop(e); }
        public void TrackHover(Control child)
        {
            child.MouseEnter += (sender, args) => Highlight(true);
            child.MouseLeave += (sender, args) => { if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) Highlight(false); };
            child.DragEnter += (sender, args) => Highlight(args.Effect != DragDropEffects.None);
            child.DragLeave += (sender, args) => Highlight(false); child.DragDrop += (sender, args) => Highlight(false);
        }
    }
    public sealed class DropCaption : Label
    {
        public DropCaption() { DoubleBuffered = true; BackColor = Color.Transparent; Cursor = Cursors.Hand; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var lines = Text.Split('\n'); string first = lines.Length > 0 ? lines[0] : "", second = lines.Length > 1 ? lines[1] : "";
            using (var auxiliary = new Font(Font.FontFamily, Font.Size * 0.9F, FontStyle.Regular))
            {
                int firstHeight = TextRenderer.MeasureText(e.Graphics, first, Font).Height;
                int secondHeight = TextRenderer.MeasureText(e.Graphics, second, auxiliary).Height;
                int top = (Height - firstHeight - secondHeight - 2) / 2;
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                TextRenderer.DrawText(e.Graphics, first, Font, new Rectangle(0, top, Width, firstHeight), UiTheme.SecondaryBlue, flags);
                TextRenderer.DrawText(e.Graphics, second, auxiliary, new Rectangle(0, top + firstHeight + 2, Width, secondHeight), UiTheme.CaptionBlue, flags);
            }
        }
    }
    public sealed class StatusDot : Control
    {
        private Color color = UiTheme.Waiting;
        public Color DotColor { get { return color; } set { color = value; Invalidate(); } }
        public StatusDot() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); BackColor = Color.Transparent; Size = new Size(8, 8); TabStop = false; }
        protected override void OnPaint(PaintEventArgs e)
        { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using (var brush = new SolidBrush(color)) e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1); }
    }
}
