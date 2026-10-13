using System;
using System.Text;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Utilities
{
    // Presentation only: preserves engine status and verification decisions.
    public static class UiResultFormatter
    {
        public static UiTone Tone(InstallStatus status)
        {
            return status == InstallStatus.SuccessVerified ? UiTone.Success :
                status == InstallStatus.SuccessWithWarning || status == InstallStatus.InstallerStateUnknown || status == InstallStatus.Cancelled || status == InstallStatus.AlreadyInstalled ? UiTone.Warning : UiTone.Error;
        }
        public static string Title(PluginInstallResult result)
        {
            switch (result.Status)
            {
                case InstallStatus.SuccessVerified: return (result.Plugin == null ? "插件" : result.Plugin.PluginName) + " 安装成功";
                case InstallStatus.SuccessWithWarning: return "安装完成，但自动验证不完整";
                case InstallStatus.InstallerStateUnknown: return "安装状态未知，请先检查日志和路由器";
                case InstallStatus.FailedUpload: return "上传失败，详情见日志";
                case InstallStatus.FailedIntegrity: return (result.Message ?? "").Contains("SHA256") ? "SHA256 校验失败，已停止安装" : (result.Message ?? "").Contains("Makeself") ? "Makeself 校验失败，已停止安装" : "安装包校验失败，已停止安装";
                case InstallStatus.FailedInstaller: return "安装程序执行失败，详情见日志";
                case InstallStatus.FailedVerification: return "安装结果未通过独立验证，详情见日志";
                case InstallStatus.Cancelled: return "已取消安装前准备";
                case InstallStatus.AlreadyInstalled: return "已保留现有插件，未执行安装";
                default: return result.SoftwareEnvironment != null && result.SoftwareEnvironment.Status != SoftwareEnvironmentStatus.Ready ? "软件环境未就绪，插件未安装" :
                    (result.Message ?? "").Contains("空间不足") ? "路由器临时空间不足，已停止安装" : "安装前检查未通过，详情见日志";
            }
        }
        public static string Details(PluginInstallResult result)
        {
            var text = new StringBuilder();
            if (result.Verification != null && !string.IsNullOrEmpty(result.Verification.PackageName))
                text.AppendLine("验证 package：" + result.Verification.PackageName + "    版本：" + (result.Verification.Version ?? "未确认"));
            text.AppendLine("安装包返回码：" + (result.InstallerExitCode.HasValue ? result.InstallerExitCode.ToString() : "未执行 / 未确认") +
                "    临时文件：" + (result.CleanupSucceeded ? "已清理" : result.RemoteDirectory == null ? "未创建" : "未确认清理"));
            if (result.Diff != null) text.AppendLine("软件包变化：新增 " + result.Diff.Added.Count + "，升级 / 版本变化 " + result.Diff.Updated.Count + "，删除 " + result.Diff.Removed.Count);
            if (result.SoftwareEnvironment != null) text.AppendLine("软件源：" + SourceLabel(result.SoftwareEnvironment.CurrentSource) +
                "（" + (result.SoftwareEnvironment.SourceSwitchOccurred ? "已切换" : result.SoftwareEnvironment.SwitchAttempted ? "曾尝试切换，见日志" : "未切换") + "）");
            if (result.Warnings.Count > 0) text.AppendLine("注意：有 " + result.Warnings.Count + " 条提示，请查看详细日志。");
            if (result.Status == InstallStatus.SuccessWithWarning) text.AppendLine("请查看安装日志 / LuCI 菜单确认。");
            if (result.InstallerStarted && result.Status != InstallStatus.SuccessVerified) text.AppendLine("请勿重复安装；先查看日志及路由器状态。");
            return text.ToString().TrimEnd();
        }
        public static string SourceLabel(SoftwareSourceInfo source)
        { return source == null ? "自定义 / 未知来源" : !string.IsNullOrWhiteSpace(source.Identity) ? source.Identity : source.ToString(); }
    }
}
