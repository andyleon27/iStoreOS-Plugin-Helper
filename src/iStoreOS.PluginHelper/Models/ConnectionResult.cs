namespace iStoreOS.PluginHelper.Models
{
    public enum ConnectionError
    {
        None, NetworkUnreachable, Timeout, AuthenticationFailure, SshProtocolError,
        HostKeyChanged, EnvironmentDetectionFailed, Cancelled, Other
    }

    public sealed class ConnectionResult
    {
        public bool Success { get; set; }
        public RouterEnvironment Environment { get; set; }
        public ConnectionError Error { get; set; }
        public string UserMessage { get; set; }
    }
}
