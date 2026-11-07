namespace iStoreOS.PluginHelper.Models
{
    public enum ConnectionError
    {
        None = 0, NetworkUnreachable = 1, Timeout = 2, AuthenticationFailure = 3,
        GenericSshProtocolError = 4, SshProtocolError = GenericSshProtocolError,
        HostKeyChanged = 5, EnvironmentDetectionFailed = 6, Cancelled = 7, Other = 8,
        MacError = 9, KeyExchangeFailed = 10, AlgorithmNegotiationFailed = 11, ConnectionAborted = 12
    }

    public sealed class ConnectionResult
    {
        public bool Success { get; set; }
        public RouterEnvironment Environment { get; set; }
        public ConnectionError Error { get; set; }
        public string UserMessage { get; set; }
    }
}
