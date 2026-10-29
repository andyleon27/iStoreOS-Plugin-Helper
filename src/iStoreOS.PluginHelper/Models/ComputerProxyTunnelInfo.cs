using System;
using System.Collections.Generic;

namespace iStoreOS.PluginHelper.Models
{
    public enum ComputerProxyTunnelState { Preparing, Active, Closing, Closed }
    public enum ComputerProxyOutcome { Ready, Verified, Rejected, Failed, Cancelled }
    public enum ComputerProxyRelease { NotCreated, Released, StillListening, Unknown }
    public enum ComputerProxyPhase { Preflight, ConnectSecondary, Bind, VerifyApi, VerifyRaw, CloseSecondary, CheckRelease, PrimaryHealth }
    public sealed class ComputerProxyEndpoint
    {
        public string DeviceHost { get; internal set; }
        public int DevicePort { get; internal set; }
        public int LocalProxyPort { get; internal set; }
        public int RemotePort { get { return 17890; } }
        public string BindAddress { get { return "127.0.0.1"; } }
    }
    public sealed class ComputerProxyLog
    {
        public ComputerProxyPhase Phase { get; internal set; }
        public string Domain { get; internal set; } public string Url { get; internal set; }
        public int? HttpStatus { get; internal set; } public int? CurlExitCode { get; internal set; }
        public string Code { get; internal set; } public string ErrorType { get; internal set; }
    }
    public sealed class ComputerProxyProbe
    {
        public string Domain { get; internal set; } public string Url { get; internal set; }
        public int ExitCode { get; internal set; } public int HttpStatus { get; internal set; }
        public int ConnectStatus { get; internal set; } public int TlsVerify { get; internal set; }
        public bool TransportOk { get; internal set; } public string Code { get; internal set; }
        public string RateRemaining { get; internal set; } public string RateReset { get; internal set; }
    }
    public sealed class ComputerProxyResult
    {
        public ComputerProxyOutcome Outcome { get; internal set; }
        public ComputerProxyEndpoint Endpoint { get; internal set; }
        public string Code { get; internal set; } public string ErrorType { get; internal set; }
        public IList<ComputerProxyProbe> Probes { get; internal set; }
        public ComputerProxyCloseResult Cleanup { get; internal set; }
    }
    public sealed class ComputerProxyCloseResult
    {
        public bool SecondaryClosed { get; internal set; }
        public ComputerProxyRelease Release { get; internal set; }
        public bool ClosedPortConfirmed { get; internal set; }
        public bool PrimaryHealthy { get; internal set; }
        public int ReleaseChecks { get; internal set; }
        public IList<string> Errors { get; internal set; }
        public bool Succeeded { get { return SecondaryClosed && Errors.Count == 0 &&
            (Release == ComputerProxyRelease.NotCreated || Release == ComputerProxyRelease.Released && ClosedPortConfirmed && PrimaryHealthy); } }
    }
}
