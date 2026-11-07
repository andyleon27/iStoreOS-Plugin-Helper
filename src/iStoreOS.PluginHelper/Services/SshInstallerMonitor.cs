using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Services
{
    internal sealed class InstallerCommandResult
    {
        public byte[] Output;
        public string Error;
        public int? ExitStatus;
    }

    // Read-only exec channels on the existing trusted SSH connection; no extra SSH login or tool install.
    internal sealed class SshInstallerMonitor : IDisposable
    {
        internal const int MaximumWireBytes = 256 + 64 + 2 * SftpInstallerPoller.MaximumLogBytes + 80;
        private readonly RemoteTempSession remote;
        private readonly Func<string, InstallerCommandResult> execute;
        private bool disposed;
        public SshInstallerMonitor(RemoteTempSession remote, Func<string, InstallerCommandResult> execute)
        {
            if (remote == null || execute == null) throw new ArgumentNullException();
            if (!remote.SftpUnavailable) throw new InvalidOperationException("没有经过确认的 SFTP subsystem 不可用证据，禁止 SSH 文件监测 fallback。");
            RemoteTempSession.ValidateDirectory(remote.Directory); this.remote = remote; this.execute = execute;
        }
        public InstallerPoll Poll(long stdoutOffset, long stderrOffset)
        {
            if (disposed) throw new ObjectDisposedException("SshInstallerMonitor");
            var result = execute(Command(remote, stdoutOffset, stderrOffset));
            if (result == null || result.ExitStatus != 0) throw new IOException("SSH 安装监测读取失败（exit " + (result == null ? "unknown" : Convert.ToString(result.ExitStatus, CultureInfo.InvariantCulture)) + "）：" + (result == null ? "" : result.Error));
            return Parse(remote, stdoutOffset, stderrOffset, result.Output);
        }
        internal static InstallerCommandResult Execute(Func<SshClient> active, string text)
        {
            var client = active();
            if (client == null || !client.IsConnected) throw new SshConnectionException("SSH 安装监测连接已断开。");
            try {
            using (var command = client.CreateCommand(text))
            {
                command.CommandTimeout = TimeSpan.FromSeconds(15);
                // Execute()/Result decode UTF-8. Read OutputStream directly to retain exact byte offsets,
                // NUL bytes and UTF-8 sequences split between polls. Never reconnect/dispose the home SSH here.
                command.ExecuteAsync(CancellationToken.None).GetAwaiter().GetResult();
                using (var output = new MemoryStream())
                {
                    byte[] buffer = new byte[8192]; int count;
                    while ((count = command.OutputStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (output.Length + count > MaximumWireBytes) throw new InvalidDataException("SSH 安装监测响应超过上限。");
                        output.Write(buffer, 0, count);
                    }
                    return new InstallerCommandResult { Output = output.ToArray(), Error = command.Error, ExitStatus = command.ExitStatus };
                }
            }
            } catch (Exception error) { SshConnectionFactory.Failure(client, error); throw; }
        }
        internal static string Command(RemoteTempSession s, long stdoutOffset, long stderrOffset)
        {
            if (s == null) throw new ArgumentNullException("s");
            RemoteTempSession.ValidateDirectory(s.Directory);
            if (stdoutOffset < 0 || stderrOffset < 0) throw new ArgumentException("日志 offset 不得为负数。");
            string output = s.File("stdout.log"), error = s.File("stderr.log"), exit = s.File("exit.code");
            // Capture exit.code BEFORE log lengths: observing exit proves the foreground installer has
            // finished writing both logs. Missing files are RUNNING, invalid paths/read errors are failures.
            // Length-framed raw data avoids base64/od/hexdump dependencies and log marker injection.
            return "LC_ALL=C; export LC_ALL; " +
                "test ! -L " + ShellArgument.Quote(RemoteTempSession.Root) + " && test ! -L " + ShellArgument.Quote(s.Directory) + " && test -d " + ShellArgument.Quote(s.Directory) + " || exit 70; " +
                "file_size() { test ! -L \"$1\" || return 71; if test -f \"$1\"; then n=$(wc -c < \"$1\") || return 72; case \"$n\" in ''|*[!0-9]*) return 72;; esac; printf '%s' \"$n\"; elif test -e \"$1\"; then return 71; else printf '%s' '-1'; fi; }; " +
                "x=$(file_size " + exit + ") || exit $?; test \"$x\" -le 64 || exit 73; " +
                "o=$(file_size " + output + ") || exit $?; e=$(file_size " + error + ") || exit $?; " +
                "count_for() { if test \"$1\" -gt \"$2\"; then n=$(($1 - $2)); test \"$n\" -le 65536 || n=65536; else n=0; fi; printf '%s' \"$n\"; }; " +
                "oc=$(count_for \"$o\" " + stdoutOffset.ToString(CultureInfo.InvariantCulture) + "); ec=$(count_for \"$e\" " + stderrOffset.ToString(CultureInfo.InvariantCulture) + "); " +
                "printf 'IPH_INSTALL_POLL_V1|" + s.Id + "|%s|%s|%s|%s|%s\\n' \"$x\" \"$o\" \"$oc\" \"$e\" \"$ec\"; " +
                "if test \"$x\" -gt 0; then dd if=" + exit + " bs=1 count=\"$x\" 2>/dev/null || exit 74; fi; " +
                "if test \"$oc\" -gt 0; then dd if=" + output + " bs=1 skip=" + stdoutOffset.ToString(CultureInfo.InvariantCulture) + " count=\"$oc\" 2>/dev/null || exit 74; fi; " +
                "if test \"$ec\" -gt 0; then dd if=" + error + " bs=1 skip=" + stderrOffset.ToString(CultureInfo.InvariantCulture) + " count=\"$ec\" 2>/dev/null || exit 74; fi; " +
                "printf 'IPH_INSTALL_POLL_END|" + s.Id + "\\n'";
        }
        internal static InstallerPoll Parse(RemoteTempSession s, long stdoutOffset, long stderrOffset, byte[] bytes)
        {
            if (s == null) throw new ArgumentNullException("s");
            RemoteTempSession.ValidateDirectory(s.Directory);
            if (stdoutOffset < 0 || stderrOffset < 0) throw new ArgumentException("日志 offset 不得为负数。");
            if (bytes == null || bytes.Length > MaximumWireBytes) throw Invalid();
            int line = Array.IndexOf(bytes, (byte)'\n');
            if (line < 0 || line > 255) throw Invalid();
            string[] header = Encoding.ASCII.GetString(bytes, 0, line).Split('|');
            if (header.Length != 7 || header[0] != "IPH_INSTALL_POLL_V1" || header[1] != s.Id) throw Invalid();
            long exitLength = Length(header[2]), outLength = Length(header[3]), outCount = Length(header[4]), errLength = Length(header[5]), errCount = Length(header[6]);
            if (exitLength > 64 || outCount != Count(outLength, stdoutOffset) || errCount != Count(errLength, stderrOffset)) throw Invalid();
            byte[] end = Encoding.ASCII.GetBytes("IPH_INSTALL_POLL_END|" + s.Id + "\n");
            long expected = line + 1L + Math.Max(0, exitLength) + outCount + errCount + end.Length;
            if (bytes.Length != expected) throw Invalid();
            int cursor = line + 1; int? code = null;
            if (exitLength >= 0)
            {
                string state = Encoding.ASCII.GetString(bytes, cursor, (int)exitLength).Trim(); int value;
                if (!Regex.IsMatch(state, @"\A[0-9]+\z") || !int.TryParse(state, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value > 255) throw new InvalidDataException("安装器结束状态不可读。");
                code = value; cursor += (int)exitLength;
            }
            byte[] stdout = new byte[(int)outCount], stderr = new byte[(int)errCount];
            Buffer.BlockCopy(bytes, cursor, stdout, 0, stdout.Length); cursor += stdout.Length;
            Buffer.BlockCopy(bytes, cursor, stderr, 0, stderr.Length); cursor += stderr.Length;
            for (int i = 0; i < end.Length; i++) if (bytes[cursor + i] != end[i]) throw Invalid();
            string diagnostic = "";
            if (outLength >= 0 && stdoutOffset > outLength) diagnostic = "SSH stdout.log offset > size；返回空，不回退偏移。";
            if (errLength >= 0 && stderrOffset > errLength) diagnostic += "SSH stderr.log offset > size；返回空，不回退偏移。";
            return new InstallerPoll { ExitCode = code, Stdout = stdout, Stderr = stderr, Diagnostic = diagnostic };
        }
        private static long Length(string text)
        { long value; if (text == "-1") return -1; if (!Regex.IsMatch(text, @"\A(?:0|[1-9][0-9]*)\z") || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) throw Invalid(); return value; }
        private static long Count(long length, long offset) { return length <= offset ? 0 : Math.Min(length - offset, SftpInstallerPoller.MaximumLogBytes); }
        private static InvalidDataException Invalid() { return new InvalidDataException("SSH 安装监测响应损坏或不属于本轮 session，状态未知。"); }
        public void Dispose() { disposed = true; }
    }
}
