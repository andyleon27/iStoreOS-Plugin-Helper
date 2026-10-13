using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public static class InstallFailureClassifier
    {
        private const string FetchError = @"(?i)could not resolve|unable to resolve|name or service not known|bad address|DNS.*fail|network error|network unreachable|network is unreachable|connection.*(timed out|timeout|reset|refused)|operation timed out|TLS.*(fail|error)|SSL.*(fail|error|problem)|failed to (download|fetch)|unable to (download|fetch)|fetching.*(error|fail)|temporary error|HTTP[^\r\n]*\b[45]\d\d\b|wget returned [1-9]";
        public static InstallationFailure Classify(string text, IList<RepositoryFeed> official, IList<RepositoryFeed> additional)
        {
            text = text ?? "";
            // Hard errors win even when the same log also contains a download error.
            var rules = new[] {
                new { Pattern = @"no space left on device|\bENOSPC\b|disk full|空间不足", Kind = InstallationFailureKind.NoSpace },
                new { Pattern = @"network is unreachable|network unreachable", Kind = InstallationFailureKind.Offline },
                new { Pattern = @"read.only file system|\bEROFS\b", Kind = InstallationFailureKind.ReadOnlyFileSystem },
                new { Pattern = @"kernel.*(ABI|incompatible|mismatch)|kmod.*(incompatible|mismatch)|kernel.*version.*(depend|mismatch)", Kind = InstallationFailureKind.KernelAbi },
                new { Pattern = @"architecture.*(mismatch|incompatible)|SDK.*(mismatch|incompatible)|兼容性.*未通过|架构.*冲突|SDK.*冲突", Kind = InstallationFailureKind.Compatibility },
                new { Pattern = @"conflicts with|trying to overwrite|version conflict|replacement conflict|file.*owned by|cannot satisfy.*depend|unsatisfiable constraints", Kind = InstallationFailureKind.PackageConflict },
                new { Pattern = @"syntax error|post.install.*(fail|error|exit[^0-9]*[1-9])|postinst.*(fail|error)|install\.sh.*(not found|permission denied)|script.*returned.*[1-9]", Kind = InstallationFailureKind.InstallerScript },
                new { Pattern = @"could not lock|unable to lock|database.*locked|resource temporarily unavailable.*lock", Kind = InstallationFailureKind.PackageManagerBusy },
                new { Pattern = @"SHA256.*(不匹配|失败|mismatch)|大小不一致", Kind = InstallationFailureKind.Integrity },
                new { Pattern = @"Makeself.*(失败|冲突)|完整性验证失败|checksum.*(mismatch|failed)|BAD signature|UNTRUSTED|signature.*(invalid|failed)", Kind = InstallationFailureKind.Makeself }
            };
            foreach (var rule in rules)
                if (Regex.IsMatch(text, rule.Pattern, RegexOptions.IgnoreCase)) return Failure(rule.Kind, text);
            bool officialError = false, otherError = false; InstallationFailureKind otherKind = InstallationFailureKind.ExternalRepository;
            string[] lines = text.Replace("\r", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (!Regex.IsMatch(lines[i], FetchError)) continue;
                string context = lines[i];
                if (!Regex.IsMatch(context, @"https?://") && i > 0 && Regex.IsMatch(lines[i - 1], @"(?i)fetch|download|https?://")) context = lines[i - 1] + "\n" + context;
                MatchCollection urls = Regex.Matches(context, @"https?://[^\s'""<>]+", RegexOptions.IgnoreCase);
                if (urls.Count == 0) { otherError = true; continue; } // Bare timeout is insufficient evidence for changing distfeeds.
                foreach (Match match in urls)
                {
                    string url = match.Value.TrimEnd(',', ')', ';', ':');
                    if (Matches(url, additional)) { otherError = true; otherKind = InstallationFailureKind.AdditionalRepository; }
                    else if (Matches(url, official)) officialError = true;
                    else otherError = true;
                }
            }
            if (otherError) return Failure(otherKind, text);
            if (officialError)
            {
                var failure = Failure(InstallationFailureKind.OfficialRepository, text); failure.CanRotateOfficial = true; return failure;
            }
            return Failure(InstallationFailureKind.UnknownInstaller, text);
        }
        private static bool Matches(string value, IList<RepositoryFeed> feeds)
        {
            Uri url; if (!Uri.TryCreate(value, UriKind.Absolute, out url)) return false;
            return feeds != null && feeds.Any(feed => {
                Uri index; if (!Uri.TryCreate(feed.IndexUrl, UriKind.Absolute, out index)) return false;
                string directory = index.AbsolutePath.Substring(0, index.AbsolutePath.LastIndexOf('/') + 1);
                return url.Scheme == index.Scheme && url.Host == index.Host && url.Port == index.Port && url.AbsolutePath.StartsWith(directory, StringComparison.Ordinal);
            });
        }
        public static InstallationFailure Failure(InstallationFailureKind kind, string evidence = null)
        {
            string message; bool retry = false;
            switch (kind)
            {
                case InstallationFailureKind.Offline: case InstallationFailureKind.DnsFailure: message = "路由器当前无法访问互联网。请检查网络连接后重试。"; retry = true; break;
                case InstallationFailureKind.TimeInvalid: message = "路由器系统时间异常。请先校准时间后重新安装。"; retry = true; break;
                case InstallationFailureKind.PackageManagerBusy: message = "软件包管理器正在被其他任务使用。请稍后再次尝试。"; retry = true; break;
                case InstallationFailureKind.NoSpace: message = "路由器存储空间不足。请清理存储空间后重新安装。"; retry = true; break;
                case InstallationFailureKind.ReadOnlyFileSystem: message = "路由器文件系统只读。请检查存储状态后再试。"; break;
                case InstallationFailureKind.Integrity: message = "安装包完整性校验失败。请重新下载插件安装包后再试。"; break;
                case InstallationFailureKind.HashUnavailable: message = "路由器缺少 SHA256 校验能力，无法安全重新执行安装包。请保存日志并检查设备。"; break;
                case InstallationFailureKind.Makeself: message = "插件安装包校验失败。建议重新下载最新版插件后再试。"; break;
                case InstallationFailureKind.Compatibility: case InstallationFailureKind.KernelAbi: message = "此插件与当前设备不兼容。请下载适合当前 SDK 和架构的插件。"; break;
                case InstallationFailureKind.PackageConflict: message = "安装遇到软件包冲突。请保存日志，检查现有软件包后再试。"; break;
                case InstallationFailureKind.InstallerScript: case InstallationFailureKind.UnknownInstaller: message = "插件安装程序执行失败。建议下载最新版插件后重新安装；如果仍然失败，请保存日志查看详细原因。"; break;
                case InstallationFailureKind.OfficialRepository: case InstallationFailureKind.AdditionalRepository: case InstallationFailureKind.ExternalRepository: message = "当前软件源暂时不可用。建议稍后再次尝试。"; retry = true; break;
                case InstallationFailureKind.MirrorsExhausted: message = "已尝试所有可用软件源，仍未完成安装。可能是当前网络或镜像服务器临时异常，建议 30～60 分钟后再次尝试。"; retry = true; break;
                case InstallationFailureKind.UpdateFailure: message = "软件源更新未完成。请保存日志，检查路由器后再次尝试。"; retry = true; break;
                case InstallationFailureKind.ConnectionLost: message = "与路由器的连接已中断。请确认路由器在线后再次尝试。"; retry = true; break;
                case InstallationFailureKind.IdentityChanged: message = "路由器身份或环境已变化。请核对设备后重新连接。"; break;
                case InstallationFailureKind.VerificationIncomplete: message = "插件安装程序已执行完成，但工具无法确认安装结果。请查看高级信息。"; break;
                case InstallationFailureKind.Cancelled: message = "已取消安装前准备。可以再次尝试。"; retry = true; break;
                default: message = "工具执行出现异常。插件未确认安装成功。请保存日志后重新尝试。"; retry = true; break;
            }
            return new InstallationFailure { Kind = kind, UserMessage = message, CanRetry = retry, Evidence = evidence };
        }
    }
}
