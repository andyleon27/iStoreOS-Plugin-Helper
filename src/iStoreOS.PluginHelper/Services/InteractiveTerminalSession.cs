using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace iStoreOS.PluginHelper.Services
{
    public interface IShellTransport : IDisposable
    {
        int Read(byte[] buffer);
        void Write(byte[] buffer);
        void Resize(uint columns, uint rows);
    }

    public interface IInteractiveTerminalSession
    {
        event Action<string> Output;
        bool Send(string text);
        bool Resize(uint columns, uint rows);
    }

    // One reader and one writer own the stream. No SSH I/O runs on the UI thread.
    public sealed class InteractiveTerminalSession : IInteractiveTerminalSession
    {
        private readonly IShellTransport transport;
        private readonly BlockingCollection<Action> commands = new BlockingCollection<Action>(1024);
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private readonly object sync = new object();
        private Task completion;
        private Task closeTask;
        private bool started;
        private bool stopped;
        public event Action<string> Output;
        public event Action<Exception> Closed;
        public Task Completion { get { lock (sync) return completion ?? Task.FromResult(0); } }
        public InteractiveTerminalSession(IShellTransport transport) { this.transport = transport; }

        public void Start()
        {
            lock (sync)
            {
                if (stopped) throw new ObjectDisposedException("InteractiveTerminalSession");
                if (started) throw new InvalidOperationException("Shell already started.");
                started = true;
                completion = RunAsync();
            }
        }
        public bool Send(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            // Preserve ordering, including paste and Ctrl+C, without waiting for the network.
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            return Enqueue(() => transport.Write(bytes));
        }
        public bool Resize(uint columns, uint rows)
        { return Enqueue(() => transport.Resize(columns, rows)); }
        private bool Enqueue(Action action)
        {
            lock (sync)
            {
                if (!started || stopped || commands.IsAddingCompleted) return false;
                return commands.TryAdd(action);
            }
        }
        private async Task RunAsync()
        {
            Exception failure = null;
            Task reader = Task.Run(() =>
            {
                byte[] bytes = new byte[8192];
                // Decoder fallback can emit a pending invalid prefix plus a full ASCII block.
                char[] chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
                Decoder decoder = Encoding.UTF8.GetDecoder();
                while (!stopping.IsCancellationRequested)
                {
                    int count = transport.Read(bytes);
                    int length = decoder.GetChars(bytes, 0, count, chars, 0, count == 0);
                    if (length > 0) { var output = Output; if (output != null) output(new string(chars, 0, length)); }
                    if (count == 0) break;
                }
            });
            Task writer = Task.Run(() =>
            {
                foreach (Action action in commands.GetConsumingEnumerable(stopping.Token)) action();
            });
            try
            {
                Task first = await Task.WhenAny(reader, writer).ConfigureAwait(false);
                await first.ConfigureAwait(false);
            }
            catch (Exception exception) { if (!stopping.IsCancellationRequested) failure = exception; }
            await CloseTransportAsync().ConfigureAwait(false);
            try { await Task.WhenAll(reader, writer).ConfigureAwait(false); } catch (Exception) { }
            var closed = Closed; if (closed != null) closed(failure);
        }
        private Task CloseTransportAsync()
        {
            lock (sync)
            {
                if (closeTask != null) return closeTask;
                stopped = true;
                stopping.Cancel();
                commands.CompleteAdding();
                closeTask = Task.Run(() => transport.Dispose());
                return closeTask;
            }
        }
        public async Task StopAsync()
        {
            await CloseTransportAsync().ConfigureAwait(false);
            await Completion.ConfigureAwait(false);
        }
    }
}
