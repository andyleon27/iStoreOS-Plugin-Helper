using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using iStoreOS.PluginHelper.Models;
using iStoreOS.PluginHelper.Services;
using iStoreOS.PluginHelper.Utilities;

namespace iStoreOS.PluginHelper.Tests
{
    internal static class SshProtocolTests
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly string Root = FindRoot();
        internal static void Run(Action<string, Action> test)
        {
            var cases = new[] {
                Pair(new SshConnectionException("MAC error", DisconnectReason.MacError), ConnectionError.MacError),
                Pair(new SshConnectionException("localized integrity failure", DisconnectReason.MacError), ConnectionError.MacError),
                Pair(new SshException("MAC error"), ConnectionError.MacError),
                Pair(new SshAuthenticationException("denied"), ConnectionError.AuthenticationFailure),
                Pair(new SshConnectionException("unverifiable", DisconnectReason.HostKeyNotVerifiable), ConnectionError.HostKeyChanged),
                Pair(new SshOperationTimeoutException("timeout"), ConnectionError.Timeout),
                Pair(new SocketException((int)SocketError.TimedOut), ConnectionError.Timeout),
                Pair(new SocketException((int)SocketError.HostUnreachable), ConnectionError.NetworkUnreachable),
                Pair(new SocketException((int)SocketError.ConnectionRefused), ConnectionError.NetworkUnreachable),
                Pair(new SocketException((int)SocketError.ConnectionReset), ConnectionError.ConnectionAborted),
                Pair(new SshConnectionException("lost", DisconnectReason.ConnectionLost), ConnectionError.ConnectionAborted),
                Pair(new SshConnectionException("KEX verification failed", DisconnectReason.KeyExchangeFailed), ConnectionError.KeyExchangeFailed),
                Pair(new SshConnectionException("No matching key exchange algorithm (server offers curve25519-sha256)", DisconnectReason.KeyExchangeFailed), ConnectionError.AlgorithmNegotiationFailed),
                Pair(new SshException("Unknown algorithm-looking text"), ConnectionError.GenericSshProtocolError),
                Pair(new SshException("MAC error maybe"), ConnectionError.GenericSshProtocolError),
                Pair(new Exception("No matching host key algorithm (server offers ssh-ed25519)"), ConnectionError.Other),
                Pair(new CryptographicException("Hash not valid for use in specified state."), ConnectionError.Other),
                Pair(new OperationCanceledException(), ConnectionError.Cancelled),
                Pair(new SshConnectionException("auth", DisconnectReason.NoMoreAuthenticationMethodsAvailable), ConnectionError.AuthenticationFailure),
                Pair(new SshConnectionException("protocol", DisconnectReason.ProtocolError), ConnectionError.GenericSshProtocolError)
            };
            foreach (var entry in cases) {
                var value = entry;
                test("SSH protocol classify " + value.Value + " " + value.Key.GetType().Name + " " + value.Key.Message, () => Equal(value.Value, SshService.ClassifyError(value.Key)));
                test("SSH protocol classify inner " + value.Value + " " + value.Key.Message, () => Equal(value.Value, SshService.ClassifyError(new IOException("wrapper", value.Key))));
            }
            foreach (string kind in new[] { "host key", "client encryption", "server encryption", "client MAC", "server MAC", "client compression", "server compression" }) {
                string name = kind; test("SSH protocol exact negotiation template " + name, () => Equal(ConnectionError.AlgorithmNegotiationFailed, SshService.ClassifyError(new SshConnectionException("No matching " + name + " algorithm (server offers a,b@c)", DisconnectReason.KeyExchangeFailed))));
            }
            test("SSH protocol aggregate preserves structured MAC reason", () => Equal(ConnectionError.MacError, SshService.ClassifyError(new AggregateException(new IOException("outer"), new SshConnectionException("localized", DisconnectReason.MacError)))));
            test("SSH protocol generic alias preserves existing enum value and explicit diagnostic name", () => { Equal(ConnectionError.SshProtocolError, ConnectionError.GenericSshProtocolError); Equal(4, (int)ConnectionError.GenericSshProtocolError); Equal("GenericSshProtocolError", SshErrorClassifier.Name(ConnectionError.GenericSshProtocolError)); });
            test("SSH protocol existing auth host timeout and generic prompts preserved", () => {
                var method = typeof(SshService).GetMethod("GetUserMessage", BindingFlags.Static | BindingFlags.NonPublic);
                Equal("用户名或密码错误", method.Invoke(null, new object[] { ConnectionError.AuthenticationFailure }));
                Equal("SSH 协议错误，请查看详细日志", method.Invoke(null, new object[] { ConnectionError.GenericSshProtocolError }));
                foreach (var code in new[] { ConnectionError.HostKeyChanged, ConnectionError.Timeout, ConnectionError.EnvironmentDetectionFailed, ConnectionError.MacError, ConnectionError.KeyExchangeFailed, ConnectionError.AlgorithmNegotiationFailed, ConnectionError.ConnectionAborted }) True(((string)method.Invoke(null, new object[] { code })).Length > 5);
            });
            test("SSH protocol negotiated diagnostic allowlist excludes authentication identity and secrets", () => {
                using (var info = SshConnectionFactory.Password("private-address", 2222, "private-user", "private-password")) {
                    Negotiated(info); string text = SshDiagnostics.Negotiated(info, "首页 SSH", false);
                    foreach (string part in new[] { "Server=SSH-2.0-dropbear_fixture", "KEX=curve25519-sha256", "HostKey=ssh-ed25519", "ClientCipher=aes128-ctr", "ServerCipher=aes256-ctr", "ClientMAC=hmac-sha2-256", "ServerMAC=hmac-sha2-512", "ClientCompression=none", "ServerCompression=none" }) Contains(text, part);
                    foreach (string secret in new[] { "private-address", "private-user", "private-password", "AuthenticationMethods", "SessionKey", "Cookie", "SharedSecret" }) Equal(false, text.Contains(secret));
                }
            });
            test("SSH protocol absent negotiation values remain unknown without guessing defaults", () => {
                using (var info = SshConnectionFactory.Password("fixture", 22, "fixture", "inert")) { var text = SshDiagnostics.Negotiated(info, "SSH", true); Contains(text, "失败前"); Equal(9, Occurrences(text, "unknown")); Equal(false, text.Contains("aes128-ctr")); }
            });
            test("SSH protocol partial negotiation reports only actual known fields", () => {
                using (var info = SshConnectionFactory.Password("fixture", 22, "fixture", "inert")) { Set(info, "ServerVersion", "SSH-2.0-fixture"); Set(info, "CurrentHostKeyAlgorithm", "ssh-ed25519"); var text = SshDiagnostics.Negotiated(info, "SSH", true); Contains(text, "HostKey=ssh-ed25519"); Contains(text, "KEX=unknown"); Equal(7, Occurrences(text, "unknown")); }
            });
            foreach (string cipher in new[] { "chacha20-poly1305@openssh.com", "aes128-gcm@openssh.com", "aes256-gcm@openssh.com" }) {
                string value = cipher; test("SSH protocol AEAD has no fictitious independent MAC " + value, () => { using (var info = SshConnectionFactory.Password("fixture", 22, "fixture", "inert")) { Set(info, "CurrentClientEncryption", value); Set(info, "CurrentServerEncryption", value); var text = SshDiagnostics.Negotiated(info, "SSH", false); Equal(2, Occurrences(text, "AEAD（集成认证，无独立 MAC）")); Equal(false, text.Contains("hmac-")); } });
            }
            test("SSH protocol failed connection snapshot remains redacted and single-line", () => {
                var logs = new List<string>(); const string secret = "test secret+$";
                using (var client = SshConnectionFactory.Ssh(SshConnectionFactory.Password("private-host", 22, "private-user", secret), "SSH", t => logs.Add(LogHelper.Redact(t, secret)))) {
                    Set(client.ConnectionInfo, "ServerVersion", "SSH-2.0-" + secret + "\r\n" + Uri.EscapeDataString(secret)); SshConnectionFactory.Failure(client, new SshConnectionException("MAC error", DisconnectReason.MacError));
                    string text = string.Join("|", logs); Contains(text, "MacError"); Contains(text, "失败前"); Equal(false, text.Contains(secret)); Equal(false, text.Contains(Uri.EscapeDataString(secret))); Equal(false, text.Contains("private-user")); Equal(false, text.Contains('\n'));
                }
            });
            test("SSH protocol diagnostic callback failure does not mask transport result", () => { using (var client = SshConnectionFactory.Ssh(SshConnectionFactory.Password("fixture", 22, "fixture", "secret"), "SSH", t => { throw new Exception("UI disposed"); })) SshConnectionFactory.Failure(client, new SshAuthenticationException("denied")); });
            test("SSH protocol runtime diagnostics distinguish patched library and mscorlib", () => { var text = SshDiagnostics.RuntimeVersion(); Contains(text, "pr1830.f099365c9d"); Contains(text, "mscorlib="); Equal(false, text.Contains(Root)); });
            test("SSH protocol MAC failure never triggers SCP fallback", () => Equal(false, InstallationUploadPolicy.IsUnavailable(new SshConnectionException("MAC error", DisconnectReason.MacError), true, true, false)));
            test("SSH factory missing trust callback rejects host key by default", () => {
                using (var client = SshConnectionFactory.Ssh(SshConnectionFactory.Password("fixture", 22, "fixture", "secret"), "SSH", null)) {
                    var handlers = (EventHandler<HostKeyEventArgs>)typeof(BaseClient).GetField("HostKeyReceived", Private).GetValue(client); var args = Key(new byte[] { 1 }); args.CanTrust = true; handlers(client, args); Equal(false, args.CanTrust);
                }
            });
            test("SSH factory production constructors are centralized and algorithms are not overridden", () => {
                foreach (string path in Directory.GetFiles(Path.Combine(Root, "src/iStoreOS.PluginHelper/Services"), "*.cs")) {
                    if (Path.GetFileName(path) == "SshConnectionFactory.cs") continue;
                    string source = File.ReadAllText(path);
                    Equal(false, System.Text.RegularExpressions.Regex.IsMatch(source, @"new\s+(?:PasswordConnectionInfo|ConnectionInfo|SshClient|SftpClient|ScpClient)\s*\("));
                    Equal(false, System.Text.RegularExpressions.Regex.IsMatch(source, @"(?:KeyExchangeAlgorithms|Encryptions|HmacAlgorithms|HostKeyAlgorithms|CompressionAlgorithms)\s*\.\s*(?:Clear|Remove|Add)\s*\("));
                }
            });
            foreach (string kind in new[] { "ssh", "sftp", "installation-sftp", "scp" }) {
                string value = kind; test("SSH factory defaults identity host trust and timeout " + value, () => Factory(value));
            }
            var patched = typeof(SshClient).Assembly;
            string originalPath = Path.Combine(Root, "packages/SSH.NET.2026.0.0/lib/net462/Renci.SshNet.dll");
            var original = Assembly.LoadFile(originalPath);
            foreach (string property in new[] { "KeyExchangeAlgorithms", "Encryptions", "HmacAlgorithms", "HostKeyAlgorithms", "CompressionAlgorithms" }) {
                string key = property; test("SSH factory patched algorithms and priority identical to original NuGet " + key, () => {
                    using (var current = SshConnectionFactory.Password("fixture", 22, "fixture", "inert")) {
                        var prior = New(original, "Renci.SshNet.PasswordConnectionInfo", "fixture", "fixture", "inert");
                        try { Equal(Keys(prior, key), Keys(current, key)); } finally { ((IDisposable)prior).Dispose(); }
                    }
                });
            }
            test("SSH patched DLL identity hash and deployment are pinned", () => {
                Equal("2026.0.0.1", patched.GetName().Version.ToString()); Equal("1cee9f8bde3db106", BitConverter.ToString(patched.GetName().GetPublicKeyToken()).Replace("-", "").ToLowerInvariant());
                using (var sha = SHA256.Create()) { var actual = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(patched.Location))).Replace("-", "").ToLowerInvariant(); Equal("31c62881cf8375b47118ab11a620c85d8208ae86b93f418f58ff2c79605ad481", actual); }
                Equal("2026.0.0.1+7b2fd3dbf2.pr1830.f099365c9d", patched.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false).Cast<AssemblyInformationalVersionAttribute>().Single().InformationalVersion);
                Equal(original.GetType("Renci.SshNet.Session").GetField("ClientVersionString", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), patched.GetType("Renci.SshNet.Session").GetField("ClientVersionString", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
            });
            test("SSH patched DLL ReceiveMessage IL contains both official MAC Initialize calls", () => { Equal(2, InitializeCalls(patched)); Equal(0, InitializeCalls(original)); });
            foreach (string algorithm in new[] { "hmac-sha2-256", "hmac-sha2-512", "hmac-sha1" }) foreach (bool etm in new[] { false, true }) {
                string name = algorithm; bool encryptThenMac = etm; string suffix = name + (etm ? " ETM" : " non-ETM");
                test("SSH MAC actual patched ReceiveMessage legacy-state regression " + suffix, () => { var r = Packets(patched, name, encryptThenMac, true, false); Equal(3, r.Completed); Equal(null, r.Error); Equal(3, r.Resets); });
                test("SSH MAC unpatched negative control fails on second packet " + suffix, () => { var r = Packets(original, name, encryptThenMac, true, false); Equal(1, r.Completed); True(r.Error != null); Equal(0, r.Resets); Contains(r.Error.Message, name == "hmac-sha1" ? "Hash not valid" : "MAC error"); });
                test("SSH MAC native framework HMAC through patched ReceiveMessage " + suffix, () => { var r = Packets(patched, name, encryptThenMac, false, false); Equal(3, r.Completed); Equal(null, r.Error); });
                test("SSH MAC corrupted packet still rejected " + suffix, () => { var r = Packets(patched, name, encryptThenMac, true, true); Equal(1, r.Completed); True(r.Error != null); Contains(r.Error.Message, "MAC error"); });
            }
        }
        private static void Factory(string kind)
        {
            var info = SshConnectionFactory.Password("fixture.invalid", 2222, "private-user", "private-secret");
            using (info) using (BaseClient client = kind == "ssh" ? (BaseClient)SshConnectionFactory.Ssh(info, "SSH", null) : kind == "scp" ? (BaseClient)SshConnectionFactory.Scp(info, null) : SshConnectionFactory.Sftp(info, kind == "installation-sftp", null)) {
                Equal("fixture.invalid", client.ConnectionInfo.Host); Equal(2222, client.ConnectionInfo.Port); Equal(1, info.AuthenticationMethods.Count); True(info.AuthenticationMethods[0] is PasswordAuthenticationMethod); Equal(TimeSpan.FromSeconds(8), info.Timeout); Equal(TimeSpan.FromSeconds(30), client.KeepAliveInterval);
                byte[] key = { 1, 2, 3 }; var trust = new HostKeyTrustStore(); string fingerprint = SshConnectionFactory.Fingerprint(key); bool first; trust.TryTrust("fixture.invalid", 2222, fingerprint, out first);
                SshConnectionFactory.Trust(client, value => SshConnectionFactory.Fingerprint(value) == fingerprint && trust.IsTrusted(info.Host, info.Port, fingerprint));
                var handlers = (EventHandler<HostKeyEventArgs>)typeof(BaseClient).GetField("HostKeyReceived", Private).GetValue(client);
                var match = Key(key); handlers(client, match); True(match.CanTrust);
                var changed = Key(new byte[] { 9 }); handlers(client, changed); Equal(false, changed.CanTrust);
                var noTrust = new HostKeyTrustStore(); SshConnectionFactory.Trust(client, value => noTrust.IsTrusted(info.Host, info.Port, SshConnectionFactory.Fingerprint(value)));
                var unknown = Key(key); handlers = (EventHandler<HostKeyEventArgs>)typeof(BaseClient).GetField("HostKeyReceived", Private).GetValue(client); handlers(client, unknown); Equal(false, unknown.CanTrust);
                if (kind == "scp") { var scp = (ScpClient)client; True(scp.UseDirectoryFlag); Equal(RemotePathTransformation.ShellQuote, scp.RemotePathTransformation); }
            }
        }
        private static HostKeyEventArgs Key(byte[] bytes) { var args = (HostKeyEventArgs)FormatterServices.GetUninitializedObject(typeof(HostKeyEventArgs)); typeof(HostKeyEventArgs).GetField("<HostKey>k__BackingField", Private).SetValue(args, bytes); return args; }
        private static string Keys(object info, string property) { object dictionary = info.GetType().GetProperty(property).GetValue(info, null); return string.Join(",", ((IEnumerable)dictionary.GetType().GetProperty("Keys").GetValue(dictionary, null)).Cast<string>()); }
        private static void Negotiated(ConnectionInfo info)
        {
            Set(info, "ServerVersion", "SSH-2.0-dropbear_fixture"); Set(info, "CurrentKeyExchangeAlgorithm", "curve25519-sha256"); Set(info, "CurrentHostKeyAlgorithm", "ssh-ed25519");
            Set(info, "CurrentClientEncryption", "aes128-ctr"); Set(info, "CurrentServerEncryption", "aes256-ctr"); Set(info, "CurrentClientHmacAlgorithm", "hmac-sha2-256"); Set(info, "CurrentServerHmacAlgorithm", "hmac-sha2-512"); Set(info, "CurrentClientCompressionAlgorithm", "none"); Set(info, "CurrentServerCompressionAlgorithm", "none");
        }
        private static void Set(object target, string property, object value) { target.GetType().GetProperty(property).SetValue(target, value, null); }
        private static object New(Assembly a, string type, params object[] args) { return Activator.CreateInstance(a.GetType(type, true), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null); }
        private sealed class PacketResult { internal int Completed, Resets; internal Exception Error; }
        private static PacketResult Packets(Assembly assembly, string name, bool etm, bool legacy, bool corrupt)
        { try { return PacketsCore(assembly, name, etm, legacy, corrupt); } catch (Exception error) { throw new Exception("MAC fixture: " + error.ToString()); } }
        private static PacketResult PacketsCore(Assembly assembly, string name, bool etm, bool legacy, bool corrupt)
        {
            byte[] key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
            var info = New(assembly, "Renci.SshNet.PasswordConnectionInfo", "fixture", "fixture", "inert");
            var session = New(assembly, "Renci.SshNet.Session", info, New(assembly, "Renci.SshNet.ServiceFactory"), New(assembly, "Renci.SshNet.Connection.SocketFactory"));
            var type = session.GetType(); var result = new PacketResult();
            using (HashAlgorithm mac = legacy ? (HashAlgorithm)new LegacyStateMac(name, key) : Hmac(name, key)) {
                type.GetField("_serverMac", Private).SetValue(session, mac); type.GetField("_serverEtm", Private).SetValue(session, etm);
                type.GetField("_sshMessageFactory", Private).SetValue(session, New(assembly, "Renci.SshNet.SshMessageFactory")); type.GetMethod("RegisterMessage").Invoke(session, new object[] { "SSH_MSG_IGNORE" });
                var packets = new List<byte>();
                for (int i = 0; i < 3; i++) {
                    byte[] packet = new byte[16]; packet[3] = 12; packet[4] = 6; packet[5] = 2; // SSH_MSG_IGNORE with an empty string and six padding bytes.
                    byte[] sequence = { 0, 0, 0, (byte)i }; byte[] tag; using (var reference = Hmac(name, key)) tag = reference.ComputeHash(sequence.Concat(packet).ToArray());
                    if (corrupt && i == 1) tag[0] ^= 1; packets.AddRange(packet); packets.AddRange(tag);
                }
                object buffer = type.GetField("_receiveBuffer", Private).GetValue(session); byte[] storage = (byte[])buffer.GetType().GetMethod("DangerousGetUnderlyingBuffer").Invoke(buffer, null); packets.CopyTo(storage); buffer.GetType().GetMethod("Commit").Invoke(buffer, new object[] { packets.Count });
                type.GetField("_receiveBuffer", Private).SetValue(session, buffer); // ArrayBuffer is a value type; persist the reflected Commit.
                try {
                    for (int i = 0; i < 3; i++) { object message = type.GetMethod("ReceiveMessage", Private).Invoke(session, new object[] { null }); Equal("IgnoreMessage", message.GetType().Name); result.Completed++; }
                } catch (TargetInvocationException error) { result.Error = error.InnerException; }
                var fixture = mac as LegacyStateMac; if (fixture != null) result.Resets = fixture.Resets;
            }
            ((IDisposable)session).Dispose(); ((IDisposable)info).Dispose(); return result;
        }
        // Deterministic model of the old mscorlib HMAC reuse contract described in #1829.
        // Actual authentication still uses framework HMAC; only missing-reset state is injected.
        private sealed class LegacyStateMac : HashAlgorithm
        {
            private readonly HMAC inner; private readonly string name; private readonly MemoryStream current = new MemoryStream();
            private byte[] previous; private bool finalized; internal int Resets;
            internal LegacyStateMac(string name, byte[] key) { this.name = name; inner = Hmac(name, key); HashSizeValue = inner.HashSize; }
            protected override void HashCore(byte[] array, int offset, int count)
            {
                if (finalized) {
                    if (name == "hmac-sha1") throw new CryptographicException("Hash not valid for use in specified state.");
                    inner.Initialize(); inner.TransformBlock(previous, 0, previous.Length, null, 0); finalized = false;
                }
                current.Write(array, offset, count); inner.TransformBlock(array, offset, count, null, 0);
            }
            protected override byte[] HashFinal() { inner.TransformFinalBlock(new byte[0], 0, 0); previous = current.ToArray(); finalized = true; return inner.Hash; }
            public override void Initialize() { Resets++; inner.Initialize(); finalized = false; current.SetLength(0); }
            protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); current.Dispose(); } base.Dispose(disposing); }
        }
        private static HMAC Hmac(string name, byte[] key) { if (name == "hmac-sha2-256") return new HMACSHA256(key); if (name == "hmac-sha2-512") return new HMACSHA512(key); return new HMACSHA1(key); }
        private static int InitializeCalls(Assembly assembly)
        {
            var method = assembly.GetType("Renci.SshNet.Session", true).GetMethod("ReceiveMessage", Private); byte[] il = method.GetMethodBody().GetILAsByteArray(); int count = 0;
            var codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(c => unchecked((ushort)c.Value));
            for (int i = 0; i < il.Length;) {
                ushort key = il[i++]; if (key == 0xfe) key = (ushort)(0xfe00 | il[i++]); OpCode op = codes[key]; int length;
                if (op.OperandType == OperandType.InlineMethod) { var called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i)); if (called.Name == "Initialize" && called.DeclaringType == typeof(HashAlgorithm)) count++; }
                switch (op.OperandType) {
                    case OperandType.InlineNone: length = 0; break;
                    case OperandType.ShortInlineI: case OperandType.ShortInlineVar: case OperandType.ShortInlineBrTarget: length = 1; break;
                    case OperandType.InlineVar: length = 2; break;
                    case OperandType.InlineI8: case OperandType.InlineR: length = 8; break;
                    case OperandType.InlineSwitch: length = 4 + 4 * BitConverter.ToInt32(il, i); break;
                    default: length = 4; break;
                }
                i += length;
            }
            return count;
        }
        private static KeyValuePair<Exception, ConnectionError> Pair(Exception error, ConnectionError code) { return new KeyValuePair<Exception, ConnectionError>(error, code); }
        private static string FindRoot() { var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); while (dir != null && !File.Exists(Path.Combine(dir.FullName, "build/Common.props"))) dir = dir.Parent; if (dir == null) throw new Exception("Source root not found."); return dir.FullName; }
        private static int Occurrences(string text, string value) { return (text.Length - text.Replace(value, "").Length) / value.Length; }
        private static void Contains(string text, string value) { if (!text.Contains(value)) throw new Exception("Missing " + value + " in " + text); }
        private static void True(bool value) { Equal(true, value); }
        private static void Equal<T>(T expected, T actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", actual " + actual); }
    }
}
