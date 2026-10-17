using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // Narrow stream boundary: production uses the trusted SFTP factory; tests use raw streams.
    public interface IInstallerFileSession : IDisposable
    {
        void Connect();
        bool Exists(string path);
        Stream OpenRead(string path);
    }

    public sealed class SftpInstallerPoller
    {
        public const int MaximumLogBytes = 65536;
        public InstallerPoll Poll(Func<IInstallerFileSession> createTransfer, RemoteTempSession session, long stdoutOffset, long stderrOffset)
        {
            if (createTransfer == null) throw new ArgumentNullException("createTransfer");
            if (session == null) throw new ArgumentNullException("session");
            RemoteTempSession.ValidateDirectory(session.Directory);
            if (stdoutOffset < 0 || stderrOffset < 0) throw new ArgumentException("日志 offset 不得为负数。");
            using (IInstallerFileSession transfer = createTransfer())
            {
                if (transfer == null) throw new IOException("SFTP 日志连接工厂未返回连接。");
                transfer.Connect();
                return PollConnected(transfer, session, stdoutOffset, stderrOffset);
            }
        }
        public InstallerPoll PollConnected(IInstallerFileSession transfer, RemoteTempSession session, long stdoutOffset, long stderrOffset)
        {
                if (transfer == null) throw new ArgumentNullException("transfer");
                if (session == null) throw new ArgumentNullException("session");
                RemoteTempSession.ValidateDirectory(session.Directory);
                if (stdoutOffset < 0 || stderrOffset < 0) throw new ArgumentException("日志 offset 不得为负数。");
                var diagnostics = new List<string>();
                int? exit = ReadExit(transfer, session.Directory + "/exit.code");
                byte[] stdout = ReadLog(transfer, session.Directory + "/stdout.log", stdoutOffset, diagnostics);
                byte[] stderr = ReadLog(transfer, session.Directory + "/stderr.log", stderrOffset, diagnostics);
                return new InstallerPoll { ExitCode = exit, Stdout = stdout, Stderr = stderr, Diagnostic = string.Join("；", diagnostics) };
        }
        private static int? ReadExit(IInstallerFileSession transfer, string path)
        {
            try
            {
            if (!transfer.Exists(path)) return null;
            using (Stream stream = transfer.OpenRead(path))
            {
                byte[] bytes = new byte[65]; int count = Fill(stream, bytes);
                string state = Encoding.ASCII.GetString(bytes, 0, count).Trim();
                int code;
                if (count == bytes.Length || !Regex.IsMatch(state, @"\A[0-9]+\z") || !int.TryParse(state, NumberStyles.None,
                    CultureInfo.InvariantCulture, out code) || code < 0 || code > 255)
                    throw new InvalidDataException("安装器结束状态不可读。");
                return code;
            }
            }
            catch (FileNotFoundException) { return null; }
        }
        private static byte[] ReadLog(IInstallerFileSession transfer, string path, long offset, List<string> diagnostics)
        {
            try
            {
            if (!transfer.Exists(path)) return new byte[0];
            using (Stream stream = transfer.OpenRead(path))
            {
                long length = stream.Length;
                if (offset >= length)
                {
                    if (offset > length) diagnostics.Add("SFTP " + path.Substring(path.LastIndexOf('/') + 1) + " offset=" +
                        offset.ToString(CultureInfo.InvariantCulture) + " > size=" + length.ToString(CultureInfo.InvariantCulture) + "；返回空，不回退偏移。");
                    return new byte[0];
                }
                stream.Seek(offset, SeekOrigin.Begin);
                byte[] bytes = new byte[MaximumLogBytes]; int count = Fill(stream, bytes);
                if (count != bytes.Length) Array.Resize(ref bytes, count);
                return bytes;
            }
            }
            catch (FileNotFoundException) { return new byte[0]; }
        }
        private static int Fill(Stream stream, byte[] bytes)
        {
            int count = 0, read;
            while (count < bytes.Length && (read = stream.Read(bytes, count, bytes.Length - count)) > 0) count += read;
            return count;
        }
    }
}
