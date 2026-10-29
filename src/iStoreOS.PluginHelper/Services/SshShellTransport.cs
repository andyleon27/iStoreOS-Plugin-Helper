using System;
using System.IO;
using System.Threading;

namespace iStoreOS.PluginHelper.Services
{
    // Disposing the owned stream closes one channel. Never owns or disposes its SSH client.
    public sealed class SshShellTransport : IShellTransport
    {
        private readonly Stream stream;
        private readonly Action<uint, uint> resize;
        private readonly Func<bool> connected;
        private Exception failure;
        private int disposed;
        public SshShellTransport(Stream stream, Action<uint, uint> resize, Func<bool> connected)
        {
            if (stream == null || resize == null || connected == null) throw new ArgumentNullException("stream");
            this.stream = stream; this.resize = resize; this.connected = connected;
        }
        public void ReportFailure(Exception error) { if (error != null) Interlocked.CompareExchange(ref failure, error, null); }
        public int Read(byte[] buffer)
        {
            // SSH.NET supports draining ShellStream buffers even after remote closure/disposal.
            int count = stream.Read(buffer, 0, buffer.Length);
            if (count == 0 && Volatile.Read(ref disposed) == 0 && (failure != null || !connected()))
                throw new IOException("SSH 终端通道中断。", failure);
            return count;
        }
        public void Write(byte[] buffer)
        {
            if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("SshShellTransport");
            stream.Write(buffer, 0, buffer.Length); stream.Flush();
        }
        public void Resize(uint columns, uint rows)
        { MaintenanceMenuTerminal.ValidateSize(columns, rows); resize(columns, rows); }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) stream.Dispose(); }
    }
}
