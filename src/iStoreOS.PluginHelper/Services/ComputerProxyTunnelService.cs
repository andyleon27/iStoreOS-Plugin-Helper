using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    internal interface IComputerProxyTunnelSession : IDisposable
    {
        bool PrimaryConnected { get; } bool PrimaryUnchanged { get; } bool SecondaryConnected { get; }
        event EventHandler ConnectionLost;
        Task<ComputerProxyCommandResult> ExecuteAsync(ComputerProxyCommand command, CancellationToken token);
        Task ConnectSecondaryAsync(CancellationToken token);
        Task StartForwardAsync(int localProxyPort, CancellationToken token);
        Task CloseSecondaryAsync();
    }
    internal sealed class ComputerProxyTunnelService
    {
        readonly IComputerProxyTunnelSession session; readonly ComputerProxyEndpoint endpoint;
        readonly IProgress<ComputerProxyLog> progress; readonly Action<ComputerProxyCloseResult> released;
        readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1); readonly object sync = new object();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        Task<ComputerProxyCloseResult> closeTask; ComputerProxyTunnelState state = ComputerProxyTunnelState.Preparing;
        int started; bool secondaryAttempted;
        public ComputerProxyTunnelService(IComputerProxyTunnelSession session, ComputerProxyEndpoint endpoint,
            IProgress<ComputerProxyLog> progress, Action<ComputerProxyCloseResult> released)
        {
            if (session == null || endpoint == null) throw new ArgumentNullException(); ComputerProxyPolicy.ValidatePort(endpoint.LocalProxyPort);
            this.session = session; this.endpoint = endpoint; this.progress = progress; this.released = released;
            session.ConnectionLost += OnConnectionLost;
        }
        public ComputerProxyTunnelState State { get { lock (sync) return state; } }
        public ComputerProxyEndpoint Endpoint { get { return endpoint; } }
        public async Task<ComputerProxyResult> CreateAsync(CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref started, 1, 0) != 0) return Result(ComputerProxyOutcome.Rejected, "AlreadyStarted");
            var result = Result(ComputerProxyOutcome.Failed, "CreateFailed"); bool entered = false; var phase = ComputerProxyPhase.Preflight;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token)) try {
                await operation.WaitAsync(linked.Token).ConfigureAwait(false); entered = true;
                if (State != ComputerProxyTunnelState.Preparing) throw new OperationCanceledException();
                await CheckPrimaryAsync(linked.Token).ConfigureAwait(false);
                var preflight = await session.ExecuteAsync(ComputerProxyCommand.Preflight, linked.Token).ConfigureAwait(false);
                if (preflight.ExitCode != 0 || !(preflight.Output ?? "").Split('\n').Any(x => x.TrimEnd('\r') == "IPH_PROXY|CURL")) throw new ProxyPolicyException("RouterCurlMissing");
                var before = ComputerProxyPortState.Parse(await session.ExecuteAsync(ComputerProxyCommand.PortState, linked.Token).ConfigureAwait(false));
                if (!before.Known || !before.Free) throw new ProxyPolicyException(before.Known ? "RemotePortOccupied" : "PortStateUnknown");
                Log(ComputerProxyPhase.Preflight, "PortFree", null, null);
                secondaryAttempted = true; phase = ComputerProxyPhase.ConnectSecondary; Log(phase, "Connecting", null, null);
                await session.ConnectSecondaryAsync(linked.Token).ConfigureAwait(false);
                if (!session.SecondaryConnected) throw new ProxyPolicyException("SecondaryUnavailable");
                await CheckPrimaryAsync(linked.Token).ConfigureAwait(false);
                phase = ComputerProxyPhase.Bind; await session.StartForwardAsync(endpoint.LocalProxyPort, linked.Token).ConfigureAwait(false);
                var during = ComputerProxyPortState.Parse(await session.ExecuteAsync(ComputerProxyCommand.PortState, linked.Token).ConfigureAwait(false));
                if (!during.LoopbackOnly) throw new ProxyPolicyException("UnsafeOrUnknownListener");
                await CheckPrimaryAsync(linked.Token).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested();
                lock (sync) { if (state != ComputerProxyTunnelState.Preparing) throw new OperationCanceledException(); state = ComputerProxyTunnelState.Active; }
                result = Result(ComputerProxyOutcome.Ready, "TunnelReady"); Log(ComputerProxyPhase.Bind, result.Code, null, null);
            } catch (Exception error) { Fail(result, error); Log(phase, result.Code, null, error); }
            finally { if (entered) operation.Release(); }
            if (result.Outcome != ComputerProxyOutcome.Ready) result.Cleanup = await CloseAsync().ConfigureAwait(false);
            return result;
        }
        public async Task<ComputerProxyResult> VerifyAsync(CancellationToken token)
        {
            var result = Result(ComputerProxyOutcome.Failed, "VerificationFailed"); bool entered = false, active = false; var phase = ComputerProxyPhase.PrimaryHealth;
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token)) try {
                await operation.WaitAsync(linked.Token).ConfigureAwait(false); entered = true;
                if (State != ComputerProxyTunnelState.Active) { result.Outcome = ComputerProxyOutcome.Rejected; result.Code = "NoActiveTunnel"; return result; }
                active = true; await CheckPrimaryAsync(linked.Token).ConfigureAwait(false);
                if (!session.SecondaryConnected) throw new ProxyPolicyException("SecondaryUnavailable");
                foreach (var command in new[] { ComputerProxyCommand.VerifyRaw, ComputerProxyCommand.VerifyApi }) {
                    phase = command == ComputerProxyCommand.VerifyApi ? ComputerProxyPhase.VerifyApi : ComputerProxyPhase.VerifyRaw;
                    ComputerProxyProbe probe;
                    try { probe = ComputerProxyPolicy.ParseProbe(command, await session.ExecuteAsync(command, linked.Token).ConfigureAwait(false)); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { if (command != ComputerProxyCommand.VerifyApi) throw; probe = ComputerProxyPolicy.ParseProbe(command, null); }
                    result.Probes.Add(probe); Log(phase, probe.Code, probe, null); phase = ComputerProxyPhase.PrimaryHealth;
                    await CheckPrimaryAsync(linked.Token).ConfigureAwait(false);
                }
                if (result.Probes.Any(x => x.Url == ComputerProxyPolicy.RawUrl && x.TransportOk && x.HttpStatus == 200)) { result.Outcome = ComputerProxyOutcome.Verified; result.Code = "RawVerified"; }
            } catch (Exception error) { Fail(result, error); active = true; Log(phase, result.Code, null, error); }
            finally { if (entered) operation.Release(); }
            if (active && result.Outcome != ComputerProxyOutcome.Verified) result.Cleanup = await CloseAsync().ConfigureAwait(false);
            return result;
        }
        public Task<ComputerProxyCloseResult> CloseAsync()
        {
            Task<ComputerProxyCloseResult> task;
            lock (sync) {
                if (closeTask != null) return closeTask;
                state = ComputerProxyTunnelState.Closing;
                closeTask = Task.Run((Func<Task<ComputerProxyCloseResult>>)CloseCoreAsync); task = closeTask;
            }
            try { lifetime.Cancel(); }
            catch (AggregateException error) { Log(ComputerProxyPhase.CloseSecondary, "CancellationCallbackFailed", null, error); }
            return task;
        }
        void OnConnectionLost(object sender, EventArgs e) { CloseAsync(); }
        async Task<ComputerProxyCloseResult> CloseCoreAsync()
        {
            var r = new ComputerProxyCloseResult { Release = secondaryAttempted ? ComputerProxyRelease.Unknown : ComputerProxyRelease.NotCreated, Errors = new List<string>() };
            await operation.WaitAsync().ConfigureAwait(false);
            try {
                // Creating can have passed its preflight while Close waited for the gate.
                r.Release = secondaryAttempted ? ComputerProxyRelease.Unknown : ComputerProxyRelease.NotCreated;
                try { await session.CloseSecondaryAsync().ConfigureAwait(false); r.SecondaryClosed = !session.SecondaryConnected; }
                catch (Exception error) { r.Errors.Add("CloseSecondary:" + error.GetType().Name); Log(ComputerProxyPhase.CloseSecondary, "CloseFailed", null, error); }
                using (var clean = new CancellationTokenSource(TimeSpan.FromSeconds(25))) {
                    if (secondaryAttempted && session.PrimaryConnected && session.PrimaryUnchanged) try {
                        for (int i = 0; i < 20; i++) {
                            var after = ComputerProxyPortState.Parse(await session.ExecuteAsync(ComputerProxyCommand.PortState, clean.Token).ConfigureAwait(false)); r.ReleaseChecks++;
                            if (!after.Known) { r.Release = ComputerProxyRelease.Unknown; break; }
                            r.Release = after.Free ? ComputerProxyRelease.Released : ComputerProxyRelease.StillListening;
                            if (after.Free) {
                                var probe = ComputerProxyPolicy.ParseProbe(ComputerProxyCommand.VerifyRaw, await session.ExecuteAsync(ComputerProxyCommand.VerifyRaw, clean.Token).ConfigureAwait(false));
                                r.ClosedPortConfirmed = probe.ExitCode == 7 && probe.Code == "ConnectFailedOrPortClosed"; break;
                            }
                            await Task.Delay(200, clean.Token).ConfigureAwait(false);
                        }
                    } catch (Exception error) { r.Release = ComputerProxyRelease.Unknown; r.Errors.Add("CheckRelease:" + error.GetType().Name); Log(ComputerProxyPhase.CheckRelease, "ReleaseUnknown", null, error); }
                    try { await CheckPrimaryAsync(clean.Token).ConfigureAwait(false); r.PrimaryHealthy = true; }
                    catch (Exception) { r.PrimaryHealthy = false; }
                }
                Log(ComputerProxyPhase.CheckRelease, r.Release.ToString(), null, null);
            } finally {
                session.ConnectionLost -= OnConnectionLost;
                try { session.Dispose(); } catch (Exception error) { r.Errors.Add("Dispose:" + error.GetType().Name); }
                r.Errors = new List<string>(r.Errors).AsReadOnly();
                lock (sync) state = ComputerProxyTunnelState.Closed;
                operation.Release(); if (released != null) released(r);
            }
            return r;
        }
        async Task CheckPrimaryAsync(CancellationToken token)
        {
            if (!session.PrimaryConnected || !session.PrimaryUnchanged) throw new ProxyPolicyException("PrimaryUnavailableOrChanged");
            var command = await session.ExecuteAsync(ComputerProxyCommand.Heartbeat, token).ConfigureAwait(false);
            if (command.ExitCode != 0 || (command.Output ?? "").TrimEnd('\r', '\n') != "IPH_PROXY_PRIMARY|OK") throw new ProxyPolicyException("PrimaryHeartbeatFailed");
        }
        ComputerProxyResult Result(ComputerProxyOutcome status, string code)
        { return new ComputerProxyResult { Outcome = status, Code = code, Endpoint = endpoint, Probes = new List<ComputerProxyProbe>() }; }
        static void Fail(ComputerProxyResult result, Exception error)
        { result.Outcome = error is OperationCanceledException ? ComputerProxyOutcome.Cancelled : ComputerProxyOutcome.Failed; result.Code = error is ProxyPolicyException ? ((ProxyPolicyException)error).Code : result.Outcome.ToString(); result.ErrorType = error.GetType().Name; }
        void Log(ComputerProxyPhase phase, string code, ComputerProxyProbe probe, Exception error)
        {
            if (progress == null) return;
            string url = probe != null ? probe.Url : phase == ComputerProxyPhase.VerifyApi ? ComputerProxyPolicy.ApiUrl : phase == ComputerProxyPhase.VerifyRaw ? ComputerProxyPolicy.RawUrl : null;
            try { progress.Report(new ComputerProxyLog { Phase = phase, Code = code, Domain = url == null ? null : new Uri(url).Host, Url = url,
                HttpStatus = probe == null ? (int?)null : probe.HttpStatus, CurlExitCode = probe == null ? (int?)null : probe.ExitCode, ErrorType = error == null ? null : error.GetType().Name }); } catch (Exception) { }
        }
        sealed class ProxyPolicyException : Exception
        { public string Code { get; private set; } public ProxyPolicyException(string code) { Code = code; } }
    }
}
