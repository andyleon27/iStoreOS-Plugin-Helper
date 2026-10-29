using System;
using System.Globalization;
using System.Text;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    // Observe raw output before TerminalScreen filters OSC. Never rewrite terminal text.
    public sealed class MaintenanceMenuProtocol
    {
        public const int MaximumFrameCharacters = 512;
        private readonly object sync = new object();
        private readonly Guid operationId, runId;
        private readonly string task, run, ownedPrefix;
        private readonly StringBuilder frame = new StringBuilder();
        private int parserState, duplicates, ignored;
        private bool begun, closed, truncated, channelFailed, remoteObserved, cleaned;
        private string conflict, remoteState, remoteDiagnostic;
        private Identity identity;
        private int? exitCode;

        public MaintenanceMenuProtocol(RemoteMaintenanceWorkspace workspace, Guid runId)
        {
            MaintenanceMenuCommands.StartCommand(workspace); // Validate the launcher workspace contract.
            if (runId == Guid.Empty) throw new ArgumentException("A unique menu run ID is required.");
            operationId = workspace.OperationId; this.runId = runId;
            task = operationId.ToString("N"); run = runId.ToString("N");
            ownedPrefix = "777;" + MaintenanceMenuCommands.ProtocolName + "|" + task + "|" + run;
        }
        public void Begin()
        {
            lock (sync)
            {
                if (begun || closed) throw new InvalidOperationException("Menu protocol already started or closed.");
                begun = true; frame.Clear(); parserState = 0;
            }
        }
        public int BufferedCharacters { get { lock (sync) return frame.Length; } }
        public MaintenanceMenuRuntimeInfo Snapshot
        {
            get
            {
                lock (sync)
                {
                    MaintenanceMenuRunStatus status = Status();
                    bool final = status == MaintenanceMenuRunStatus.ExitedNormally || status == MaintenanceMenuRunStatus.ExitedWithError;
                    return new MaintenanceMenuRuntimeInfo { OperationId = operationId, RunId = runId, Status = status,
                        ProcessId = identity == null ? (int?)null : identity.Pid,
                        ProcessStartTicks = identity == null ? (long?)null : identity.Ticks,
                        StartedAtUnixSeconds = identity == null ? (long?)null : identity.Started,
                        ExitCode = final || status == MaintenanceMenuRunStatus.AwaitingClose ? exitCode : null,
                        ChannelClosed = closed, CanCleanup = closed && final && remoteState == "EXITED" && !cleaned, CleanupCompleted = cleaned,
                        DuplicateMarkers = duplicates, IgnoredMarkers = ignored,
                        Diagnostic = conflict ?? remoteDiagnostic ?? (status == MaintenanceMenuRunStatus.Unknown ?
                            (channelFailed ? "终端通道中断，尚未取得可信结束证据。" : "缺少相符的启动、退出或远端状态证据。") : "") };
                }
            }
        }
        private MaintenanceMenuRunStatus Status()
        {
            if (!begun) return MaintenanceMenuRunStatus.NotStarted;
            if (conflict != null) return MaintenanceMenuRunStatus.Unknown;
            if (!closed) return exitCode.HasValue ? MaintenanceMenuRunStatus.AwaitingClose :
                identity == null ? MaintenanceMenuRunStatus.Starting : MaintenanceMenuRunStatus.Running;
            if (remoteObserved)
            {
                if (remoteState != "EXITED") return MaintenanceMenuRunStatus.Unknown;
            }
            else if (channelFailed || truncated) return MaintenanceMenuRunStatus.Unknown;
            if (identity == null || !exitCode.HasValue) return MaintenanceMenuRunStatus.Unknown;
            return exitCode.Value == 0 ? MaintenanceMenuRunStatus.ExitedNormally : MaintenanceMenuRunStatus.ExitedWithError;
        }

        public void Feed(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (sync)
            {
                if (!begun || closed) return;
                foreach (char c in text)
                {
                    if (parserState == 0) { if (c == '\u001b') parserState = 1; continue; }
                    if (parserState == 1)
                    {
                        parserState = c == ']' ? 2 : c == '\u001b' ? 1 : 0;
                        if (parserState == 2) frame.Clear();
                        continue;
                    }
                    if (parserState == 4)
                    { if (c == '\a') parserState = 0; else if (c == '\u001b') parserState = 5; continue; }
                    if (parserState == 5)
                    { parserState = c == '\\' || c == '\a' ? 0 : c == '\u001b' ? 5 : 4; continue; }
                    if (parserState == 3)
                    {
                        if (c == '\\' || c == '\a') { FinishFrame(); continue; }
                        Append('\u001b'); if (parserState == 4) continue;
                        parserState = 2; Append(c); continue;
                    }
                    if (c == '\a') { FinishFrame(); continue; }
                    if (c == '\u001b') { parserState = 3; continue; }
                    Append(c);
                }
            }
        }
        private void Append(char value)
        {
            if (frame.Length >= MaximumFrameCharacters)
            {
                if (IsOwnedPrefix()) conflict = "本任务运行标记超过长度上限。";
                frame.Clear(); parserState = 4; return;
            }
            frame.Append(value);
        }
        private bool IsOwnedPrefix()
        {
            string value = frame.ToString();
            return value.StartsWith(ownedPrefix, StringComparison.Ordinal) &&
                (value.Length == ownedPrefix.Length || value[ownedPrefix.Length] == '|');
        }
        private void FinishFrame()
        {
            string value = frame.ToString(); frame.Clear(); parserState = 0;
            if (!value.StartsWith("777;" + MaintenanceMenuCommands.ProtocolName + "|", StringComparison.Ordinal)) return;
            string[] fields = value.Substring(4).Split('|');
            if (fields.Length < 3 || fields[1] != task || fields[2] != run) { ignored++; return; }
            Identity found;
            if (fields.Length == 7 && fields[3] == "START" && TryIdentity(fields, 4, out found))
            {
                if (identity == null) identity = found;
                else if (!identity.Same(found)) conflict = "同一任务的启动进程标识冲突。";
                else duplicates++;
                return;
            }
            int code;
            if (fields.Length == 8 && fields[3] == "EXIT" && TryIdentity(fields, 4, out found) && TryCode(fields[7], out code))
            {
                if (identity == null || !identity.Same(found)) { conflict = "退出标记缺少相符的启动进程标识。"; return; }
                if (exitCode.HasValue)
                {
                    if (exitCode.Value != code) conflict = "同一任务的退出码冲突。";
                    else duplicates++;
                }
                else exitCode = code;
                return;
            }
            conflict = "本任务运行标记格式无效。";
        }

        // Call only after the stream reader/writer have completed. An EOF alone is not success.
        public void Complete(Exception error = null)
        {
            lock (sync)
            {
                if (closed) return;
                closed = true; channelFailed = error != null;
                truncated = IsOwnedPrefix(); frame.Clear(); parserState = 0;
            }
        }

        // The reply must come from the owned ReadStateCommand on the authenticated connection.
        // A valid durable exit can reconcile lost markers; a contradictory complete marker cannot.
        public bool ApplyRemoteState(ProbeResult reply)
        {
            lock (sync)
            {
                if (!begun || conflict != null || cleaned) return false;
                remoteObserved = true; remoteState = "UNKNOWN";
                remoteDiagnostic = "远端运行状态未确认，不能清理。";
                if (reply == null || reply.ExitStatus != 0 || reply.Output == null || reply.Output.Length > MaximumFrameCharacters) return false;
                string line = reply.Output;
                if (line.EndsWith("\r\n", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 2);
                else if (line.EndsWith("\n", StringComparison.Ordinal)) line = line.Substring(0, line.Length - 1);
                if (line.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) return false;
                string[] fields = line.Split('|');
                if (fields.Length < 5 || fields[0] != MaintenanceMenuCommands.ProtocolName || fields[1] != task ||
                    fields[2] != run || fields[3] != "STATE") return false;
                if (fields.Length == 5 && (fields[4] == "UNKNOWN" || fields[4] == "NOT_STARTED"))
                { remoteState = fields[4]; return true; }
                bool ended = fields[4] == "EXITED";
                if (!(ended && fields.Length == 9 || fields[4] == "RUNNING" && fields.Length == 8)) return false;
                Identity found; int code = 0;
                if (!TryIdentity(fields, 5, out found) || ended && !TryCode(fields[8], out code)) return false;
                if (identity != null && !identity.Same(found)) { conflict = "远端与终端的进程标识冲突。"; return false; }
                if (ended && exitCode.HasValue && exitCode.Value != code) { conflict = "远端与终端的退出码冲突。"; return false; }
                identity = found;
                if (ended) exitCode = code;
                remoteState = ended ? "EXITED" : "RUNNING";
                remoteDiagnostic = ended ? null : "远端进程仍在运行，不能清理。";
                return true;
            }
        }

        public bool ConfirmCleanup(ProbeResult reply)
        {
            lock (sync)
            {
                var status = Status();
                if (!closed || remoteState != "EXITED" ||
                    !(status == MaintenanceMenuRunStatus.ExitedNormally || status == MaintenanceMenuRunStatus.ExitedWithError) ||
                    reply == null || reply.ExitStatus != 0) return false;
                string expected = MaintenanceMenuCommands.ProtocolName + "|" + task + "|" + run + "|CLEANED";
                if (reply.Output != expected + "\n" && reply.Output != expected + "\r\n") return false;
                cleaned = true; return true;
            }
        }

        private sealed class Identity
        {
            public int Pid; public long Ticks, Started;
            public bool Same(Identity other) { return Pid == other.Pid && Ticks == other.Ticks && Started == other.Started; }
        }
        private static bool TryIdentity(string[] fields, int offset, out Identity identity)
        {
            identity = null;
            long pid, ticks, started;
            if (!TryUnsigned(fields[offset], out pid) || pid == 0 || pid > int.MaxValue ||
                !TryUnsigned(fields[offset + 1], out ticks) || !TryUnsigned(fields[offset + 2], out started)) return false;
            identity = new Identity { Pid = (int)pid, Ticks = ticks, Started = started }; return true;
        }
        private static bool TryCode(string text, out int code)
        { long value; bool valid = TryUnsigned(text, out value) && value <= 255; code = valid ? (int)value : 0; return valid; }
        private static bool TryUnsigned(string text, out long value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 19 || text.Length > 1 && text[0] == '0') return false;
            foreach (char c in text) if (c < '0' || c > '9') return false;
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }
    }
}

