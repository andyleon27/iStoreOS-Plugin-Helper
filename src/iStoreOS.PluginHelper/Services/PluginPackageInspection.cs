using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public static class PluginPackageInspection
    {
        public const long SafetyMargin = 32L * 1024 * 1024;
        public static string ComputeSha256(Stream stream)
        {
            stream.Position = 0;
            using (var sha = SHA256.Create())
            {
                string result = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                stream.Position = 0; return result;
            }
        }
        public static string ParseSha256(string text)
        {
            Match match = Regex.Match(text ?? "", @"\A\s*([a-fA-F0-9]{64})(?:\s|\z)");
            if (!match.Success) throw new InvalidDataException("无法解析远程 SHA256。");
            return match.Groups[1].Value.ToLowerInvariant();
        }
        public static long ParseTmpAvailable(string text)
        {
            foreach (string line in (text ?? "").Split('\n'))
            {
                string[] fields = Regex.Split(line.Trim(), @"\s+"); long available;
                if (fields.Length >= 6 && fields[fields.Length - 1].StartsWith("/", StringComparison.Ordinal) && long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out available))
                    return checked(available * 1024);
            }
            throw new InvalidDataException("无法读取路由器 /tmp 可用空间。");
        }
        public static RemotePackageInfo ParseInfo(string text)
        {
            var result = new RemotePackageInfo { Identification = Field(text, "Identification"), TargetDirectory = Field(text, "Target directory"), Compression = Field(text, "Compression") };
            string target = result.TargetDirectory;
            if (target != null && Regex.IsMatch(target, @"\A(?:x86_64|aarch64(?:_[a-zA-Z0-9]+)?|arm[a-zA-Z0-9_]*|mips[a-zA-Z0-9_]*|i[3-6]86|riscv64)\z")) result.Architecture = target;
            Match sdk = Regex.Match(result.Identification ?? "", @"sdk_(\d{2}\.\d{2})(?:\b|_)", RegexOptions.IgnoreCase);
            if (sdk.Success) result.SdkSeries = sdk.Groups[1].Value;
            Match size = Regex.Match(Field(text, "Uncompressed size") ?? "", @"\A([0-9]+)\s*(KB|KiB|MB|MiB|bytes|B)\b", RegexOptions.IgnoreCase);
            long number;
            if (size.Success && long.TryParse(size.Groups[1].Value, out number))
            {
                string unit = size.Groups[2].Value.ToUpperInvariant();
                result.UncompressedBytes = checked(number * (unit == "KB" || unit == "KIB" ? 1024L : unit == "MB" || unit == "MIB" ? 1048576L : 1));
            }
            Match version = Regex.Match(text ?? "", @"Makeself version\s+([0-9.]+)", RegexOptions.IgnoreCase);
            if (version.Success) result.MakeselfVersion = version.Groups[1].Value;
            return result;
        }
        private static string Field(string text, string label)
        {
            Match match = Regex.Match(text ?? "", @"^\s*" + Regex.Escape(label) + @":\s*([^\r\n]+)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }
        public static bool IntegrityPassed(ProbeResult check)
        {
            if (check == null || check.ExitStatus != 0) return false;
            string text = (check.Output ?? "") + "\n" + (check.Error ?? "");
            return Regex.IsMatch(text, @"All good\b|checksums? (?:are |is )?OK\b", RegexOptions.IgnoreCase) &&
                !Regex.IsMatch(text, @"checksum.*(?:mismatch|FAILED|error)|(?:corrupt|integrity.*failed)", RegexOptions.IgnoreCase);
        }
    }
}
