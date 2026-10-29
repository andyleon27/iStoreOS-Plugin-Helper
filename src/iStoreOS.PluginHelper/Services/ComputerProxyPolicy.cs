using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public enum ComputerProxyCommand { Heartbeat, Preflight, PortState, VerifyApi, VerifyRaw, DirectRaw, DirectApi }
    public sealed class ComputerProxyCommandResult
    { public int ExitCode { get; set; } public string Output { get; set; } }
    public sealed class ComputerProxyPortState
    {
        public bool Known { get; private set; } public bool Free { get; private set; } public bool LoopbackOnly { get; private set; }
        public static ComputerProxyPortState Parse(ComputerProxyCommandResult command)
        {
            var r = new ComputerProxyPortState(); var listeners = new List<string>(); int ends = 0;
            if (command == null || command.ExitCode != 0 || command.Output == null || command.Output.Length > 65536) return r;
            foreach (string raw in command.Output.Split('\n')) {
                string line = raw.TrimEnd('\r');
                if (!line.StartsWith("IPH_PROXY_PORT|", StringComparison.Ordinal)) continue;
                if (line == "IPH_PROXY_PORT|END") { ends++; continue; }
                var match = Regex.Match(line, @"\AIPH_PROXY_PORT\|LISTEN\|([0-9A-F]{8}(?:[0-9A-F]{24})?:45E2)\z");
                if (!match.Success) return r; listeners.Add(match.Groups[1].Value);
            }
            r.Known = ends == 1; r.Free = r.Known && listeners.Count == 0;
            r.LoopbackOnly = r.Known && listeners.Contains("0100007F:45E2") && listeners.Distinct().Count() == listeners.Count &&
                listeners.All(x => x == "0100007F:45E2" || x == "00000000000000000000000001000000:45E2");
            return r;
        }
    }
    public static class ComputerProxyPolicy
    {
        public const int RemotePort = 17890;
        public const string ApiUrl = "https://api.github.com/repos/slobys/openclash-auto-installer/git/ref/heads/main";
        public const string RawUrl = "https://raw.githubusercontent.com/slobys/openclash-auto-installer/main/LICENSE";
        public static void ValidatePort(int port) { if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port"); }
        public static string Url(ComputerProxyCommand command)
        { return command == ComputerProxyCommand.VerifyApi || command == ComputerProxyCommand.DirectApi ? ApiUrl : command == ComputerProxyCommand.VerifyRaw || command == ComputerProxyCommand.DirectRaw ? RawUrl : null; }
        public static string GetCommand(ComputerProxyCommand command)
        {
            if (command == ComputerProxyCommand.Heartbeat) return "printf 'IPH_PROXY_PRIMARY|OK\\n'";
            if (command == ComputerProxyCommand.Preflight) return "if command -v curl >/dev/null 2>&1; then printf 'IPH_PROXY|CURL\\n'; else printf 'IPH_PROXY|NO_CURL\\n'; fi";
            if (command == ComputerProxyCommand.PortState) return "if ! command -v awk >/dev/null 2>&1 || ! test -r /proc/net/tcp; then printf 'IPH_PROXY_PORT|UNKNOWN\\n'; exit 0; fi; " +
                "for f in /proc/net/tcp /proc/net/tcp6; do if test -r \"$f\"; then awk 'NR>1 && toupper($2) ~ /:45E2$/ && $4 == \"0A\" {print \"IPH_PROXY_PORT|LISTEN|\" toupper($2)}' \"$f\" || { printf 'IPH_PROXY_PORT|UNKNOWN\\n'; exit 0; }; elif test -e \"$f\"; then printf 'IPH_PROXY_PORT|UNKNOWN\\n'; exit 0; fi; done; printf 'IPH_PROXY_PORT|END\\n'";
            string url = Url(command); if (url == null) throw new ArgumentOutOfRangeException("command");
            if (command == ComputerProxyCommand.DirectRaw || command == ComputerProxyCommand.DirectApi)
                return "http_proxy='' https_proxy='' HTTP_PROXY='' HTTPS_PROXY='' ALL_PROXY='' all_proxy='' no_proxy='*' NO_PROXY='*' curl -q --silent --show-error --proxy '' --noproxy '*' " +
                    (command == ComputerProxyCommand.DirectApi ? "--connect-timeout 2 --max-time 3 --header 'Accept: application/vnd.github+json' " : "--connect-timeout 3 --max-time 5 ") +
                    "--max-filesize 65536 --user-agent 'iStoreOS-Plugin-Helper/1.1' --output /dev/null --dump-header - --write-out '\nIPH_PROXY_CURL|%{http_code}|%{http_connect}|%{ssl_verify_result}\n' --url '" + url + "'";
            return "http_proxy='http://127.0.0.1:17890' https_proxy='http://127.0.0.1:17890' HTTP_PROXY='' HTTPS_PROXY='' ALL_PROXY='' all_proxy='' no_proxy='' NO_PROXY='' " +
                "curl -q --silent --show-error --connect-timeout 5 --max-time 12 --max-filesize 65536 --noproxy '' --user-agent 'iStoreOS-Plugin-Helper/1.1' " +
                (command == ComputerProxyCommand.VerifyApi ? "--header 'Accept: application/vnd.github+json' " : "") + "--output /dev/null --dump-header - " +
                "--write-out '\nIPH_PROXY_CURL|%{http_code}|%{http_connect}|%{ssl_verify_result}\n' --url '" + url + "'";
        }
        public static ComputerProxyProbe ParseProbe(ComputerProxyCommand command, ComputerProxyCommandResult response)
        {
            string url = Url(command); if (url == null) throw new ArgumentOutOfRangeException("command");
            var r = new ComputerProxyProbe { Url = url, Domain = new Uri(url).Host, ExitCode = response == null ? -1 : response.ExitCode, TlsVerify = -1, Code = "MissingFooter" };
            string output = response == null ? null : response.Output;
            if (output == null || output.Length > 65536) return r;
            var matches = Regex.Matches(output, @"(?m)^IPH_PROXY_CURL\|([0-9]{3})\|([0-9]{3})\|([0-9]+)\r?$");
            if (matches.Count != 1 || !Regex.IsMatch(output, @"IPH_PROXY_CURL\|[0-9]{3}\|[0-9]{3}\|[0-9]+\r?\n?\z")) return r;
            var match = matches[0]; int tls;
            if (!int.TryParse(match.Groups[3].Value, out tls)) return r;
            r.HttpStatus = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); r.ConnectStatus = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture); r.TlsVerify = tls;
            bool proxy = command == ComputerProxyCommand.VerifyApi || command == ComputerProxyCommand.VerifyRaw;
            r.TransportOk = r.ExitCode == 0 && tls == 0 && (!proxy || r.ConnectStatus == 200) && r.HttpStatus >= 100 && r.HttpStatus <= 599;
            r.Code = r.TransportOk ? r.HttpStatus == 200 ? "OK" : "TargetHttp" + r.HttpStatus :
                r.ExitCode == 7 ? "ConnectFailedOrPortClosed" : r.ExitCode == 28 ? "Timeout" : r.ExitCode == 5 || r.ExitCode == 6 ? "DnsFailure" :
                r.ExitCode == 35 || r.ExitCode == 60 || tls > 0 ? "TlsFailure" : r.ConnectStatus == 407 ? "ProxyAuthenticationRequired" : "ProxyOrCurlFailure";
            if (command == ComputerProxyCommand.VerifyApi || command == ComputerProxyCommand.DirectApi) foreach (string raw in output.Split('\n')) {
                int colon = raw.IndexOf(':'); if (colon < 0) continue;
                string name = raw.Substring(0, colon).Trim();
                if (name.Equals("x-ratelimit-remaining", StringComparison.OrdinalIgnoreCase)) r.RateRemaining = HeaderNumber(raw.Substring(colon + 1));
                if (name.Equals("x-ratelimit-reset", StringComparison.OrdinalIgnoreCase)) r.RateReset = HeaderNumber(raw.Substring(colon + 1));
            }
            return r;
        }
        static string HeaderNumber(string value)
        { value = value.Trim(); long number; return value.Length <= 19 && Regex.IsMatch(value, @"\A[0-9]+\z") && long.TryParse(value, out number) ? number.ToString(CultureInfo.InvariantCulture) : "[invalid]"; }
    }
}
