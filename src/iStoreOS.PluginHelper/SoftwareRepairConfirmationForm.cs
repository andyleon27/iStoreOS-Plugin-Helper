using System.Drawing;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper
{
    internal sealed class SoftwareRepairConfirmationForm : Form
    {
        public SoftwareRepairConfirmationForm(SoftwareRepairPlan plan)
        {
            Text = "确认自动修复软件源"; Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White; ForeColor = UiTheme.Ink; ShowIcon = false;
            FormBorderStyle = FormBorderStyle.FixedDialog; MinimizeBox = false; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(540, 235);
            var text = new Label { Dock = DockStyle.Fill, Padding = new Padding(18), Text =
                "当前软件源不可用，是否自动切换到可用软件源？\n\n候选：" + plan.ProposedSource + "\n" + plan.ProposedSource.Url +
                "\n只读探测：" + plan.Probe + "\n\n将先备份官方配置，再尝试 FULL 候选。修复未成功会恢复原文。" };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 90 };
            var repair = new Button { Text = "自动修复", DialogResult = DialogResult.OK, Width = 100 };
            UiTheme.Button(repair, true); UiTheme.Button(cancel, false);
            buttons.Controls.Add(cancel); buttons.Controls.Add(repair); Controls.Add(text); Controls.Add(buttons);
            AcceptButton = repair; CancelButton = cancel;
        }
    }
}
