using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    internal static class SshErrorClassifier
    {
        // Exact message template from the pinned ServiceFactory/KeyExchange source, not a keyword guess.
        private static readonly Regex Negotiation = new Regex(@"\ANo matching (?:key exchange|host key|client encryption|server encryption|client MAC|server MAC|client compression|server compression) algorithm \(server offers [a-zA-Z0-9@+_.,/=-]*\)\z", RegexOptions.CultureInvariant);
        internal static ConnectionError Classify(Exception error)
        {
            var errors = Flatten(error).ToArray();
            if (errors.Any(e => Reason(e, DisconnectReason.HostKeyNotVerifiable))) return ConnectionError.HostKeyChanged;
            if (errors.Any(e => e is SshAuthenticationException || Reason(e, DisconnectReason.NoMoreAuthenticationMethodsAvailable) || Reason(e, DisconnectReason.IllegalUserName))) return ConnectionError.AuthenticationFailure;
            if (errors.Any(e => Reason(e, DisconnectReason.MacError) || (e is SshException && e.Message == "MAC error"))) return ConnectionError.MacError;
            if (errors.Any(e => e is OperationCanceledException || Reason(e, DisconnectReason.AuthenticationCanceledByUser))) return ConnectionError.Cancelled;
            if (errors.Any(e => e is SshOperationTimeoutException || e is TimeoutException || (e is SocketException && ((SocketException)e).SocketErrorCode == SocketError.TimedOut))) return ConnectionError.Timeout;
            if (errors.Any(e => e is SshException && Negotiation.IsMatch(e.Message ?? ""))) return ConnectionError.AlgorithmNegotiationFailed;
            if (errors.Any(e => Reason(e, DisconnectReason.KeyExchangeFailed))) return ConnectionError.KeyExchangeFailed;
            if (errors.Any(e => Reason(e, DisconnectReason.ConnectionLost) || Reason(e, DisconnectReason.ByApplication) || Aborted(e))) return ConnectionError.ConnectionAborted;
            if (errors.Any(e => e is SocketException)) return ConnectionError.NetworkUnreachable;
            return errors.Any(e => e is SshException) ? ConnectionError.GenericSshProtocolError : ConnectionError.Other;
        }
        internal static string Name(ConnectionError error)
        { return error == ConnectionError.GenericSshProtocolError ? "GenericSshProtocolError" : error.ToString(); }
        internal static string DisconnectReasonName(Exception error)
        { var ssh = Flatten(error).OfType<SshConnectionException>().FirstOrDefault(e => e.DisconnectReason != DisconnectReason.None); return ssh == null ? "unknown" : ssh.DisconnectReason.ToString(); }
        private static bool Reason(Exception error, DisconnectReason reason)
        { var ssh = error as SshConnectionException; return ssh != null && ssh.DisconnectReason == reason; }
        private static bool Aborted(Exception error)
        {
            var socket = error as SocketException;
            return socket != null && (socket.SocketErrorCode == SocketError.ConnectionAborted || socket.SocketErrorCode == SocketError.ConnectionReset || socket.SocketErrorCode == SocketError.NetworkReset || socket.SocketErrorCode == SocketError.Shutdown);
        }
        private static IEnumerable<Exception> Flatten(Exception error)
        {
            var pending = new Queue<Exception>(); var seen = new HashSet<Exception>(); if (error != null) pending.Enqueue(error);
            while (pending.Count > 0 && seen.Count < 32)
            {
                var item = pending.Dequeue(); if (!seen.Add(item)) continue; yield return item;
                var aggregate = item as AggregateException;
                if (aggregate != null) foreach (var child in aggregate.InnerExceptions) pending.Enqueue(child);
                else if (item.InnerException != null) pending.Enqueue(item.InnerException);
            }
        }
    }
}
