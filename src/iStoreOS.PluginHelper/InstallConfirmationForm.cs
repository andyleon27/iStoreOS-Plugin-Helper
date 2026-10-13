using System;
using System.Drawing;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper
{
    public sealed class InstallConfirmationForm : Form
    {
        public InstallConfirmationForm(InstallConfirmation summary)
        {
            Text = "确认安装插件"; Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White; ForeColor = UiTheme.Ink; ShowIcon = false;
            ClientSize = new Size(660, 455); MinimumSize = new Size(600, 460);
            StartPosition = FormStartPosition.CenterParent; MinimizeBox = false; MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            string text = "插件：" + summary.Plugin.PluginName + "\r\n版本：" + summary.Plugin.PluginVersion +
                "\r\n目标：iStoreOS " + summary.Router.Version + "\r\nArchitecture：" + summary.Router.Architecture +
                "\r\nSDK：" + summary.Router.SdkSeries + "\r\n软件环境：Ready" +
                "\r\n当前软件源：" + UiResultFormatter.SourceLabel(summary.SoftwareEnvironment.CurrentSource) +
                "\r\n安装包：" + summary.Plugin.FileName + "\r\nSHA256：\r\n" + summary.Sha256 +
                "\r\n完整性：Makeself --check 通过\r\n\r\n将执行作者原始 .run，并独立验证安装结果。";
            layout.Controls.Add(new TextBox { Text = text, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = SystemColors.Window }, 0, 0);
            var notice = new Label { Dock = DockStyle.Fill, Text = summary.ExistingPackage == null ? "安装开始后请勿关闭程序或断开路由器电源。" :
                "检测到 " + summary.Plugin.PluginName + " 已经安装：" + summary.ExistingPackage + " " + summary.ExistingVersion + "。继续将执行安装 / 更新。", ForeColor = summary.ExistingPackage == null ? Color.DimGray : Color.Firebrick };
            layout.Controls.Add(notice, 0, 1);
            var acknowledge = new CheckBox { Dock = DockStyle.Fill, Text = "我确认继续安装 / 更新现有插件", Visible = summary.ExistingPackage != null };
            layout.Controls.Add(acknowledge, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "取消", Width = 110, Height = 30, DialogResult = DialogResult.Cancel };
            var install = new Button { Text = "开始安装", Width = 120, Height = 30, Enabled = summary.ExistingPackage == null, DialogResult = DialogResult.OK, Name = "confirmInstallButton" };
            UiTheme.Button(install, true); UiTheme.Button(cancel, false);
            acknowledge.CheckedChanged += (sender, args) => install.Enabled = acknowledge.Checked;
            actions.Controls.Add(cancel); actions.Controls.Add(install); layout.Controls.Add(actions, 0, 3);
            Controls.Add(layout); CancelButton = cancel; ActiveControl = cancel;
            // Enter defaults to cancelling; installation requires an explicit click.
            AcceptButton = cancel;
        }
    }
}
