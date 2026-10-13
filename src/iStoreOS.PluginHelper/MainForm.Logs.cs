using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper
{
    public partial class MainForm
    {
        private readonly StringBuilder diagnosticTranscript = new StringBuilder();

        private void RecordDiagnosticLine(string line)
        {
            diagnosticTranscript.Append(line);
            RefreshLogButtons();
        }
        private void RefreshLogButtons()
        {
            if (IsDisposed || Disposing) return;
            bool available = diagnosticTranscript.Length > 0 || softwareTranscript.Length > 0 || installTranscript.Length > 0;
            saveSoftwareLogButton.Enabled = available;
            saveInstallLogButton.Enabled = available;
            if (saveLogButton != null) saveLogButton.Enabled = available;
            saveSoftwareLogButton.Text = softwareTranscript.Length > 0 ? "保存检测日志" : "保存当前日志";
            saveInstallLogButton.Text = installTranscript.Length > 0 ? "保存安装日志" : "保存当前日志";
            uiToolTip.SetToolTip(saveSoftwareLogButton, softwareTranscript.Length > 0 ? "保存完整软件环境检测记录；检测中也可保存当前快照。" : "尚无检测记录，保存当前连接与文件分析诊断日志。");
            uiToolTip.SetToolTip(saveInstallLogButton, installTranscript.Length > 0 ? "保存完整安装准备 / 安装记录；进行中也可保存当前快照。" : "尚无安装记录，保存当前诊断日志；不会启动安装。");
        }
        private string GetSoftwareLogText()
        {
            return ExportSnapshot(softwareTranscript.Length > 0 ? "软件环境检测日志" : "当前诊断日志（本次尚无软件环境检测记录）",
                softwareTranscript.Length > 0 ? softwareTranscript.ToString() : diagnosticTranscript.ToString());
        }
        private string GetInstallLogText()
        {
            return ExportSnapshot(installTranscript.Length > 0 ? "安装准备 / 安装日志" : "当前诊断日志（本次尚未执行安装流程）",
                installTranscript.Length > 0 ? installTranscript.ToString() : diagnosticTranscript.ToString());
        }
        private string ExportSnapshot(string scope, string body)
        {
            string state = isInstallBusy ? installerDispatched ? "安装器进行中；当前日志快照" : "安装前准备中；当前日志快照" :
                isSoftwareBusy ? "软件环境检测中；当前日志快照" : "当前无界面操作；具体操作结果以正文为准";
            return ProductInfo.WindowTitle + " (" + ProductInfo.Version + ")" + Environment.NewLine +
                "日志内容：" + scope + Environment.NewLine + "保存时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                "界面操作状态：" + state + Environment.NewLine + Environment.NewLine + body;
        }
        private static void WriteLogFile(string path, string text)
        { File.WriteAllText(path, text, new UTF8Encoding(true)); }

        private string GetAllLogText() { return ExportSnapshot("完整会话诊断与安装日志", diagnosticTranscript.ToString()); }
        private void SaveLogButton_Click(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog { Filter = "日志 (*.log)|*.log", DefaultExt = "log", AddExtension = true,
                FileName = "istore-plugin-helper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log", Title = "保存完整日志" })
            {
                if (dialog.ShowDialog(advancedInfoForm.Visible ? (IWin32Window)advancedInfoForm : this) != DialogResult.OK) return;
                try { WriteLogFile(dialog.FileName, GetAllLogText()); }
                catch (Exception ex) { AppendLog("日志保存失败：" + ex.GetType().Name); }
            }
        }

        private void SaveLogSnapshot(bool installation)
        {
            bool scoped = installation ? installTranscript.Length > 0 : softwareTranscript.Length > 0;
            string prefix = installation ? scoped ? "plugin-install-" : "install-diagnostics-" : scoped ? "software-environment-" : "environment-diagnostics-";
            using (var dialog = new SaveFileDialog
            {
                Filter = "日志 (*.log)|*.log", DefaultExt = "log", AddExtension = true,
                FileName = prefix + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log",
                Title = scoped ? installation ? "保存完整安装日志" : "保存完整软件环境检测日志" : "保存当前诊断日志"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try { WriteLogFile(dialog.FileName, installation ? GetInstallLogText() : GetSoftwareLogText()); }
                catch (Exception exception) { AppendLog("日志保存失败：" + exception.GetType().Name); }
            }
        }
    }
}
