using System.Drawing;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper
{
    // Modeless, owned by MainForm. Closing this view never cancels router work.
    public sealed class AdvancedInfoForm : Form
    {
        public AdvancedInfoForm(Control content, Icon icon)
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = content.Font;
            Text = "高级信息";
            Name = "AdvancedInfoForm";
            ClientSize = new Size(800, 600);
            MinimumSize = new Size(640, 480);
            BackColor = UiTheme.Background;
            ForeColor = UiTheme.Ink;
            Padding = new Padding(16);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            if (icon != null) Icon = icon;
            content.Dock = DockStyle.Fill;
            Controls.Add(content);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }
        public void SelectLogs()
        {
            var tabs = Controls.Find("advancedTabs", true);
            if (tabs.Length > 0) ((TabControl)tabs[0]).SelectedIndex = 4;
        }
    }
}
