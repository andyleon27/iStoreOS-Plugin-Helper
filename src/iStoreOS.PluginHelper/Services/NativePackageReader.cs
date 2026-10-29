using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // Reads metadata only. Never extracts paths, loads code or executes package scripts.
    internal static class NativePackageReader
    {
        private const int MetadataLimit = 1024 * 1024;
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        internal static void Read(Stream source, PluginPackageInfo info)
        {
            source.Position = 0;
            try
            {
                if (info.Format == LocalPackageFormat.Ipk) ReadIpk(source, info);
                else if (info.Format == LocalPackageFormat.Apk) ReadApk(source, info);
                else throw new ArgumentException("不是原生软件包。");
            }
            catch (ZstdSharp.ZstdException ex) { throw new InvalidDataException("APK zstd 元数据解码失败或窗口超限。禁止安装。", ex); }
            if (info.PluginName == null || info.PluginName.Length > 255) throw Bad();
            PluginInstallCommands.ValidatePackageName(info.PluginName);
            if (!Regex.IsMatch(info.PluginVersion ?? "", @"\A[A-Za-z0-9][A-Za-z0-9.+_~:-]{0,255}\z") ||
                !Regex.IsMatch(info.Architecture ?? "", @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,127}\z"))
                throw new InvalidDataException("包版本或架构元数据不合法。");
            info.MetadataRead = true; info.IsRecognizedPlugin = true;
            source.Position = 0;
        }
        private static void ReadIpk(Stream source, PluginPackageInfo info)
        {
            byte[] magic = Exact(source, 8); source.Position = 0;
            byte[] control = null; bool data = false, version = false;
            Action<string, byte[], long> entry = (name, bytes, size) =>
            {
                if (name == "control.tar.gz") { if (control != null) throw Bad(); control = bytes; }
                if (name == "data.tar.gz") { if (data) throw Bad(); data = true; }
                if (name == "debian-binary") { if (version || Utf8.GetString(bytes) != "2.0\n") throw Bad(); version = true; }
            };
            if (Encoding.ASCII.GetString(magic) == "!<arch>\n")
            {
                source.Position = 8;
                for (int count = 0; source.Position < source.Length; count++)
                {
                    if (count >= 32) throw Bad();
                    byte[] h = Exact(source, 60); long size;
                    if (h[58] != 96 || h[59] != 10 || !long.TryParse(Encoding.ASCII.GetString(h, 48, 10).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out size) || size > source.Length - source.Position) throw Bad();
                    string name = Encoding.ASCII.GetString(h, 0, 16).TrimEnd(' ', '/');
                    bool wanted = name == "control.tar.gz" || name == "debian-binary";
                    byte[] bytes = wanted ? Exact(source, Limited(size)) : null;
                    if (!wanted) source.Position += size;
                    entry(name, bytes, size);
                    if ((size & 1) != 0) Exact(source, 1);
                }
            }
            else
            {
                using (var gz = new GZipStream(source, CompressionMode.Decompress, true))
                    Tar(gz, 512L * 1024 * 1024, name => name == "control.tar.gz" || name == "debian-binary", entry);
            }
            if (control == null || !data || !version) throw Bad();
            string fields = null;
            using (var gz = new GZipStream(new MemoryStream(control), CompressionMode.Decompress))
                Tar(gz, MetadataLimit, name => name == "control", (name, bytes, size) => { if (name == "control") { if (fields != null) throw Bad(); fields = Utf8.GetString(bytes); } });
            Fields(fields, false, info);
        }
        private static void ReadApk(Stream source, PluginPackageInfo info)
        {
            byte[] magic = Exact(source, 4); source.Position = 0;
            if (magic[0] == 65 && magic[1] == 68 && magic[2] == 66)
            {
                if (magic[3] == 46) { ReadAdb(source, info); return; }
                source.Position = 4;
                int algorithm = magic[3] == 100 ? 1 : magic[3] == 99 ? source.ReadByte() : -1;
                if (magic[3] == 99 && source.ReadByte() < 0) throw Bad(); // compression level
                if (algorithm == 0) { ReadAdb(source, info); return; }
                if (algorithm == 2)
                {
                    using (var zstd = new ZstdSharp.DecompressionStream(source))
                    { zstd.SetParameter(ZstdSharp.Unsafe.ZSTD_dParameter.ZSTD_d_windowLogMax, 23); ReadAdb(zstd, info); }
                    return;
                }
                if (algorithm != 1) throw new InvalidDataException("不支持此 APK 压缩算法。未执行安装。");
                using (var deflate = new DeflateStream(source, CompressionMode.Decompress, true)) ReadAdb(deflate, info);
                return;
            }
            if (magic[0] != 0x1f || magic[1] != 0x8b) throw new InvalidDataException("不是 apk-tools 软件包（Android APK 不受支持）。");
            // APK v2 has separate concatenated gzip members for signatures, control and data.
            // Prevent Framework GZipStream read-ahead from consuming the following member.
            for (int member = 0; member < 8 && source.Position < source.Length; member++)
            {
                byte[] bytes;
                using (var gz = new GZipStream(new SingleByteStream(source), CompressionMode.Decompress)) bytes = ReadBounded(gz, MetadataLimit);
                string fields = null;
                using (var tar = new MemoryStream(bytes))
                    Tar(tar, MetadataLimit, name => name == ".PKGINFO", (name, value, size) => { if (name == ".PKGINFO") { if (fields != null) throw Bad(); fields = Utf8.GetString(value); } });
                if (fields != null) { Fields(fields, true, info); return; }
            }
            throw Bad();
        }
        private static void Fields(string text, bool apk, PluginPackageInfo info)
        {
            if (text == null) throw Bad();
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in text.Split('\n'))
            {
                if (raw.Length == 0 || raw[0] == '#' || char.IsWhiteSpace(raw[0])) continue;
                int split = raw.IndexOf(apk ? '=' : ':'); if (split < 1) throw Bad();
                string key = raw.Substring(0, split).Trim(), value = raw.Substring(split + 1).Trim();
                if (key != (apk ? "pkgname" : "Package") && key != (apk ? "pkgver" : "Version") &&
                    key != (apk ? "arch" : "Architecture") && key != (apk ? "size" : "Installed-Size")) continue;
                if (fields.ContainsKey(key)) throw Bad(); fields.Add(key, value);
            }
            info.PluginName = Required(fields, apk ? "pkgname" : "Package");
            info.PluginVersion = Required(fields, apk ? "pkgver" : "Version");
            info.Architecture = Required(fields, apk ? "arch" : "Architecture");
            string size;
            if (fields.TryGetValue(apk ? "size" : "Installed-Size", out size))
            {
                long number;
                if (!long.TryParse(size, NumberStyles.None, CultureInfo.InvariantCulture, out number)) throw Bad();
                info.InstalledBytes = number; // OpenWrt ipkg-build and apk-tools both store bytes.
            }
        }
        private static string Required(IDictionary<string, string> fields, string key)
        { string value; if (!fields.TryGetValue(key, out value) || value.Length == 0) throw Bad(); return value; }
        private static void ReadAdb(Stream source, PluginPackageInfo info)
        {
            byte[] header = Exact(source, 12);
            if (U32(header, 0) != 0x2e424441 || U32(header, 4) != 0x676b6370) throw Bad();
            uint block = U32(header, 8);
            if ((block >> 30) != 0 || block < 12 || block > 8 * MetadataLimit) throw Bad();
            byte[] adb = Exact(source, (int)block - 4);
            if (adb[0] != 0) throw new InvalidDataException("不支持此 APK ADB 兼容版本。");
            uint root = U32(adb, 4), pi = Slot(adb, root, 1);
            info.PluginName = Blob(adb, Slot(adb, pi, 1));
            info.PluginVersion = Blob(adb, Slot(adb, pi, 2));
            info.Architecture = Blob(adb, Slot(adb, pi, 5));
            uint size = Slot(adb, pi, 12);
            if (size != 0)
            {
                int offset = (int)(size & 0xfffffff); uint type = size >> 28;
                if (type == 1) info.InstalledBytes = offset;
                else if (type == 2) info.InstalledBytes = U32(adb, offset);
                else if (type == 3) { ulong number = U32(adb, offset) | ((ulong)U32(adb, checked(offset + 4)) << 32); info.InstalledBytes = checked((long)number); }
                else throw Bad();
            }
        }
        private static uint Slot(byte[] adb, uint obj, int index)
        {
            if ((obj >> 28) != 14) throw Bad(); int offset = (int)(obj & 0xfffffff);
            uint count = U32(adb, offset);
            if (count < 1 || count > 128 || (long)offset + count * 4 > adb.Length) throw Bad();
            return index < count ? U32(adb, checked(offset + index * 4)) : 0;
        }
        private static string Blob(byte[] adb, uint value)
        {
            int offset = (int)(value & 0xfffffff), prefix = (value >> 28) == 8 ? 1 : (value >> 28) == 9 ? 2 : (value >> 28) == 10 ? 4 : 0;
            if (prefix == 0 || offset > adb.Length - prefix) throw Bad();
            uint size = prefix == 1 ? adb[offset] : prefix == 2 ? (uint)(adb[offset] | adb[offset + 1] << 8) : U32(adb, offset);
            if (size > 4096 || (long)offset + prefix + size > adb.Length) throw Bad();
            return Utf8.GetString(adb, offset + prefix, (int)size);
        }
        private static uint U32(byte[] bytes, int offset)
        { if (offset < 0 || offset > bytes.Length - 4) throw Bad(); return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
        private static void Tar(Stream stream, long limit, Func<string, bool> wanted, Action<string, byte[], long> entry)
        {
            long consumed = 0;
            for (int count = 0; count < 4096; count++)
            {
                byte[] h = new byte[512]; int first = stream.ReadByte(); if (first < 0) return;
                h[0] = (byte)first; ReadInto(stream, h, 1, 511); consumed += 512;
                bool zero = true; foreach (byte b in h) if (b != 0) { zero = false; break; } if (zero) return;
                long sum = 0; for (int i = 0; i < h.Length; i++) sum += i >= 148 && i < 156 ? 32 : h[i];
                if (Octal(h, 148, 8) != sum) throw Bad();
                string name = Encoding.ASCII.GetString(h, 0, 100).TrimEnd('\0'); if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
                long size = Octal(h, 124, 12), padded = checked((size + 511) / 512 * 512);
                if (consumed + padded > limit) throw new InvalidDataException("软件包元数据扫描超过安全上限。");
                consumed += padded;
                bool read = wanted(name);
                if (h[156] == (byte)'x' || h[156] == (byte)'g' || h[156] == (byte)'L' || h[156] == (byte)'K')
                    throw new InvalidDataException("元数据归档使用扩展路径头，无法唯一确认包元数据。禁止安装。");
                if (read && ((h[156] != 0 && h[156] != 48) || (Encoding.ASCII.GetString(h, 257, 6) == "ustar\0" && h[345] != 0))) throw Bad();
                byte[] bytes = read ? Exact(stream, Limited(size)) : null;
                Skip(stream, read ? padded - size : padded);
                entry(name, bytes, size);
            }
            throw Bad();
        }
        private static long Octal(byte[] bytes, int offset, int count)
        {
            string value = Encoding.ASCII.GetString(bytes, offset, count).Trim('\0', ' ');
            if (!Regex.IsMatch(value, "^[0-7]+$")) throw Bad(); return Convert.ToInt64(value, 8);
        }
        private static int Limited(long size) { if (size < 0 || size > MetadataLimit) throw Bad(); return (int)size; }
        private static byte[] Exact(Stream source, int size) { var bytes = new byte[size]; ReadInto(source, bytes, 0, size); return bytes; }
        private static void ReadInto(Stream source, byte[] bytes, int offset, int count)
        { while (count > 0) { int n = source.Read(bytes, offset, count); if (n == 0) throw Bad(); count -= n; offset += n; } }
        private static void Skip(Stream source, long size)
        { var buffer = new byte[8192]; while (size > 0) { int n = source.Read(buffer, 0, (int)Math.Min(size, buffer.Length)); if (n == 0) throw Bad(); size -= n; } }
        private static byte[] ReadBounded(Stream source, int limit)
        {
            using (var output = new MemoryStream())
            { var buffer = new byte[8192]; int n; while ((n = source.Read(buffer, 0, buffer.Length)) > 0) { if (output.Length + n > limit) throw Bad(); output.Write(buffer, 0, n); } return output.ToArray(); }
        }
        private static InvalidDataException Bad() { return new InvalidDataException("无法可靠读取软件包元数据：格式错误、缺失、重复或越界。禁止安装。"); }
        private sealed class SingleByteStream : Stream
        {
            private readonly Stream inner;
            public SingleByteStream(Stream inner) { this.inner = inner; }
            public override int Read(byte[] b, int o, int c) { return inner.Read(b, o, Math.Min(c, 1)); }
            public override bool CanRead { get { return true; } } public override bool CanWrite { get { return false; } } public override bool CanSeek { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } } public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { } public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
            public override void SetLength(long v) { throw new NotSupportedException(); } public override void Write(byte[] b, int o, int c) { throw new NotSupportedException(); }
        }
    }
}
