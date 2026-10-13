using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Controls
{
    public sealed class InformationRow : TableLayoutPanel
    {
        public InformationRow(string title, Control value)
        {
            Dock = DockStyle.Fill; ColumnCount = 2; RowCount = 1; Margin = Padding.Empty;
            ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.PluginEmptyText, AutoEllipsis = true, Margin = Padding.Empty }, 0, 0);
            Controls.Add(value, 1, 0);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); using (var pen = new Pen(UiTheme.RowSeparator)) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }

    // These are the original readonly TextBox values, with only visual wrapping
    // and automatic vertical scrolling. Their Text / selection remain available.
    public sealed class AdaptiveDetailTextBox : TextBox
    {
        private bool adjusting;
        public AdaptiveDetailTextBox()
        {
            ReadOnly = true; Multiline = true; WordWrap = true; ScrollBars = ScrollBars.None;
            BorderStyle = BorderStyle.None; BackColor = Color.White; ForeColor = UiTheme.Ink;
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); UpdateScroll(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateScroll(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateScroll(); }
        private void UpdateScroll()
        {
            if (adjusting || ClientSize.Width < 1 || ClientSize.Height < 1) return;
            int fullWidth = ClientSize.Width + (ScrollBars == ScrollBars.Vertical ? SystemInformation.VerticalScrollBarWidth : 0);
            int measured = TextRenderer.MeasureText(Text + " ", Font, new Size(Math.Max(1, fullWidth - 4), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding).Height;
            var desired = measured > ClientSize.Height ? ScrollBars.Vertical : ScrollBars.None;
            if (desired == ScrollBars) return;
            adjusting = true;
            try { int start = SelectionStart, length = SelectionLength; ScrollBars = desired; Select(Math.Min(start, TextLength), Math.Min(length, Math.Max(0, TextLength - start))); }
            finally { adjusting = false; }
        }
    }

    public sealed class EmptyStateView : Panel
    {
        private readonly TableLayoutPanel row;
        private float scale = 1F;
        public EmptyStateView(string name, string title, string help, Image icon, UiResources resources)
        {
            Name = name; Dock = DockStyle.Fill; Margin = Padding.Empty; BackColor = Color.White;
            row = new TableLayoutPanel { Height = 90, ColumnCount = 2, RowCount = 2,
                Padding = new Padding(12, 20, 12, 20), Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); row.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            var picture = new PictureBox { Image = icon, Size = new Size(28, 28), SizeMode = PictureBoxSizeMode.Zoom, Anchor = AnchorStyles.None, Margin = Padding.Empty };
            row.Controls.Add(picture, 0, 0); row.SetRowSpan(picture, 2);
            row.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.SecondaryText, Font = resources.Font(10F, FontStyle.Regular), AutoEllipsis = true, Margin = Padding.Empty }, 1, 0);
            row.Controls.Add(new Label { Text = help, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = UiTheme.EmptyHelp, Font = resources.Font(8.5F, FontStyle.Regular), AutoEllipsis = true, Margin = Padding.Empty }, 1, 1);
            Controls.Add(row);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); if (row == null) return;
            int width = Math.Max(0, Math.Min(ClientSize.Width - (int)(24 * scale), (int)(420 * scale)));
            row.Bounds = new Rectangle((ClientSize.Width - width) / 2, Math.Max(0, (ClientSize.Height - row.Height) / 2), width, row.Height);
        }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { scale *= factor.Width; base.ScaleControl(factor, specified); }
        public void UseDarkSurface()
        {
            BackColor = UiTheme.LogBackground; row.BackColor = UiTheme.LogBackground;
            foreach (Control control in row.Controls)
            {
                control.BackColor = Color.Transparent;
                if (control is Label && row.GetRow(control) == 0) control.ForeColor = UiTheme.LogForeground;
            }
        }
    }

    // A readonly visual projection of the existing FormatDiff text. The original
    // textbox remains the unmodified record used by the existing display methods.
    public sealed class PackageDiffView : Panel
    {
        private readonly TextBox record;
        private readonly DataGridView grid;
        private readonly Control empty;
        public PackageDiffView(TextBox record, UiResources resources)
        {
            this.record = record; Name = "packageDiffView"; Dock = DockStyle.Fill; Margin = Padding.Empty;
            grid = new DataGridView { Name = "packageDiffGrid", Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
                BackgroundColor = Color.White, GridColor = UiTheme.GridLine, RowHeadersVisible = false, EnableHeadersVisualStyles = false,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, AutoGenerateColumns = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 32, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, Margin = Padding.Empty };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = UiTheme.NavigationFill, ForeColor = UiTheme.StatusText,
                Font = resources.Font(9F, FontStyle.Bold), Padding = new Padding(8, 0, 8, 0), SelectionBackColor = UiTheme.NavigationFill, SelectionForeColor = UiTheme.StatusText };
            grid.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.White, ForeColor = UiTheme.Ink, Font = resources.Font(9F, FontStyle.Regular),
                Padding = new Padding(8, 5, 8, 5), WrapMode = DataGridViewTriState.True, SelectionBackColor = UiTheme.SecondaryHover, SelectionForeColor = UiTheme.Ink };
            grid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.GridAlternate;
            foreach (string title in new[] { "轮次", "变化", "软件包", "原版本", "当前版本" }) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable });
            grid.Columns[0].FillWeight = 12; grid.Columns[1].FillWeight = 16; grid.Columns[2].FillWeight = 34; grid.Columns[3].FillWeight = 19; grid.Columns[4].FillWeight = 19;
            empty = new EmptyStateView("diffEmptyState", "尚无软件包变化记录", "安装前后的软件包变化将在这里显示。", resources.Image("package-manager", 56), resources);
            record.Visible = false; Controls.Add(record); Controls.Add(grid); Controls.Add(empty);
            record.TextChanged += (sender, args) => RefreshRows(); RefreshRows();
        }
        private void RefreshRows()
        {
            grid.Rows.Clear();
            string attempt = "本轮", attemptDetail = "本轮软件包变化";
            foreach (string line in record.Lines)
            {
                var round = Regex.Match(line, @"^Attempt (\d+)\b"); if (round.Success) { attempt = round.Groups[1].Value; attemptDetail = line; continue; }
                var match = Regex.Match(line, @"^(新增|变化|删除)\s{2}(\S+)\s{2}(.*)$"); if (!match.Success) continue;
                string kind = match.Groups[1].Value, value = match.Groups[3].Value, before = "—", after = "—";
                if (kind == "变化") { string[] versions = value.Split(new[] { " → " }, StringSplitOptions.None); before = versions[0]; after = versions.Length > 1 ? versions[1] : "—"; }
                else if (kind == "新增") after = value; else before = value;
                int index = grid.Rows.Add(attempt, kind, match.Groups[2].Value, before, after);
                grid.Rows[index].Cells[0].ToolTipText = attemptDetail;
                grid.Rows[index].Cells[1].Style.ForeColor = kind == "新增" ? UiTheme.Success : kind == "删除" ? UiTheme.Error : UiTheme.Warning;
            }
            bool hasRows = grid.Rows.Count > 0; grid.Visible = hasRows; empty.Visible = !hasRows;
            if (hasRows) grid.BringToFront(); else empty.BringToFront();
        }
    }
}
