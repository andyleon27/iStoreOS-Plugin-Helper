using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Controls
{
    // Preserve TabControl's page selection and keyboard semantics, while covering
    // its native headers with the selected page. Navigation is painted separately.
    public sealed class FlatPageTabs : TabControl
    {
        public override Rectangle DisplayRectangle { get { return ClientRectangle; } }
        public FlatPageTabs()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            SizeMode = TabSizeMode.Fixed; ItemSize = new Size(1, 1); Padding = Point.Empty;
        }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); FitPage(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); FitPage(); }
        private void FitPage() { if (SelectedTab != null && SelectedTab.Bounds != ClientRectangle) SelectedTab.Bounds = ClientRectangle; }
        protected override void OnPaint(PaintEventArgs e) { e.Graphics.Clear(UiTheme.Background); }
    }

    public sealed class SegmentedNavigation : RoundedPanel
    {
        private readonly TabControl pages;
        private readonly UiResources resources;
        private readonly FlowLayoutPanel row;
        private float fontScale = 1F;
        public SegmentedNavigation(TabControl pages, UiResources resources)
        {
            this.pages = pages; this.resources = resources;
            Name = "advancedNavigation"; Radius = 10; FillColor = UiTheme.NavigationFill;
            BorderColor = UiTheme.NavigationFill; Padding = new Padding(3); Margin = Padding.Empty;
            row = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty, BackColor = Color.Transparent };
            for (int i = 0; i < pages.TabCount; i++)
            {
                int index = i;
                var button = new SegmentButton { Name = "advancedNav" + i, Text = pages.TabPages[i].Text, Height = 32,
                    Width = TextRenderer.MeasureText(pages.TabPages[i].Text, resources.Font(9.5F, FontStyle.Bold)).Width + 32,
                    Font = resources.Font(9.5F, FontStyle.Regular), Margin = new Padding(0, 0, 2, 0), Radius = 8,
                    AccessibleRole = AccessibleRole.PageTab, AccessibleName = pages.TabPages[i].Text };
                button.Click += (sender, args) => pages.SelectedIndex = index;
                button.KeyDown += (sender, args) =>
                {
                    if (args.KeyCode != Keys.Left && args.KeyCode != Keys.Right) return;
                    pages.SelectedIndex = (pages.SelectedIndex + (args.KeyCode == Keys.Left ? pages.TabCount - 1 : 1)) % pages.TabCount;
                    row.Controls[pages.SelectedIndex].Focus(); args.Handled = true;
                };
                row.Controls.Add(button);
            }
            Controls.Add(row); pages.SelectedIndexChanged += (sender, args) => RefreshSelection(); RefreshSelection();
        }
        private void RefreshSelection()
        {
            for (int i = 0; i < row.Controls.Count; i++)
            {
                var button = (SegmentButton)row.Controls[i]; button.Selected = pages.SelectedIndex == i;
                button.Font = resources.Font(9.5F * fontScale, button.Selected ? FontStyle.Bold : FontStyle.Regular);
                button.TabStop = button.Selected; button.Invalidate();
            }
        }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
        { fontScale *= factor.Width; base.ScaleControl(factor, specified); RefreshSelection(); }
    }

    public sealed class SegmentButton : RoundedButton
    {
        private bool hovered;
        private float scale = 1F;
        public bool Selected { get; set; }
        public Color SegmentFill { get { return Selected ? UiTheme.NavigationSelected : hovered ? UiTheme.SecondaryHover : UiTheme.NavigationFill; } }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; base.OnMouseLeave(e); }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { scale *= factor.Width; base.ScaleControl(factor, specified); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundedDrawing.Path(new RectangleF(0, 0, Width, Height), Radius * scale))
            using (var brush = new SolidBrush(SegmentFill)) e.Graphics.FillPath(brush, path);
            var color = Selected || hovered ? UiTheme.LinkNormal : UiTheme.NavigationText;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -(int)(5 * scale), -(int)(5 * scale)), color, SegmentFill);
        }
    }
}
