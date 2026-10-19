using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FudanNetLite
{
    internal interface IPortalSession : IPortal, IDisposable
    {
        Task<PortalStatus> LogoutAsync(Credentials credentials, CancellationToken token);
    }

    public sealed class SrunPortal : IPortalSession
    {
        private readonly IPortalTransport transport;
        private readonly PortalHeartbeat heartbeat;
        private bool httpKeepaliveLogged;
        private bool disposed;

        public SrunPortal() : this(new CampusTransport(), new PortalHeartbeat()) { }
        public SrunPortal(IPortalTransport transport, PortalHeartbeat heartbeat)
        {
            this.transport = transport; this.heartbeat = heartbeat;
        }
        public async Task<PortalStatus> CheckAsync(CancellationToken cancellationToken)
        {
            try
            {
                var text = await transport.RequestAsync(CampusTransport.StatusPath, null, cancellationToken).ConfigureAwait(false);
                PortalStatus result = PortalReply.ClassifyStatus(text);
                if (result.State != PortalState.Online) httpKeepaliveLogged=false;
                if (result.State == PortalState.Online && heartbeat != null)
                {
                    var value = PortalReply.Parse(text);
                    // Native client 0043a09b selects GetJsonRadiusUserInfo for
                    // servers >= 1.01.20170101. This request itself keeps the
                    // session alive; CHECK_VERSION UDP is the older branch.
                    if (UsesHttpKeepalive(PortalReply.Field(value,"sysver")))
                    {
                        if(!httpKeepaliveLogged)Store.Log("heartbeat.http.confirmed");
                        httpKeepaliveLogged=true;return result;
                    }
                    var name = PortalReply.Field(value, "user_name");
                    var mac = PortalReply.Field(value, "user_mac");
                    var campus = transport as CampusTransport;
                    var selectedRoute = campus == null ? null : campus.Route;
                    if (selectedRoute != null) mac = selectedRoute.Mac;
                    if (String.IsNullOrEmpty(mac)) mac = NetworkIdentity.Mac();
                    if (String.IsNullOrEmpty(name)) name = PortalReply.Field(value, "username");
                    if (!String.IsNullOrEmpty(name) && !String.IsNullOrEmpty(mac))
                    {
                        // One bounded heartbeat exchange per normal status cycle.
                        // No unsolicited traffic when the server cannot confirm online.
                        var heartState = await heartbeat.TickAsync(name, mac, selectedRoute, cancellationToken).ConfigureAwait(false);
                        if (heartState == PortalState.Restricted) return new PortalStatus { State = heartState, Message = "heartbeat.server_restriction" };
                    }
                }
                return result;
            }
            catch (OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); throw; }
            catch
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new PortalStatus { State = PortalState.Unknown, Message = "gateway.unavailable" };
            }
        }

        public async Task<LoginResult> LoginAsync(Credentials credentials, CancellationToken cancellationToken)
        {
            if (credentials == null || String.IsNullOrWhiteSpace(credentials.UserName) || String.IsNullOrEmpty(credentials.Password))
                return new LoginResult { PermanentFailure = true, Message = "credentials.missing" };
            try
            {
                var campus = transport as CampusTransport;
                if (campus != null) await campus.EnsureRouteAsync(cancellationToken).ConfigureAwait(false);
                string mac = campus != null && campus.Route != null ? campus.Route.Mac : NetworkIdentity.Mac();
                if (String.IsNullOrEmpty(mac)) return new LoginResult { Message = "interface.unavailable" };
                var time = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                Dictionary<string, string> headers;
                var form = BuildLogin(credentials, mac, time, out headers);
                var text = await transport.RequestAsync("/cgi-bin/srun_portal", form, headers, cancellationToken).ConfigureAwait(false);
                return ClassifyLogin(text);
            }
            catch (EncoderFallbackException) { return new LoginResult { PermanentFailure = true, Message = "credentials.encoding_unsupported" }; }
            catch (OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); throw; }
            catch { cancellationToken.ThrowIfCancellationRequested(); return new LoginResult { Message = "gateway.unavailable" }; }
        }
        public static Dictionary<string, string> BuildLogin(Credentials credentials, string mac, string time, out Dictionary<string, string> headers)
        {
            string password = LegacySrun.EncodePassword(credentials.Password, LegacySrun.PasswordKey);
            string crc = LegacySrun.Crc32State(LegacySrun.UrlEncode(credentials.UserName) + "|" + LegacySrun.UrlEncode(password) + "|117|2|" + time);
            headers = new Dictionary<string, string> {
                { "SRun-ClientTime", time }, { "SRun-AuthorizationKey", LegacySrun.AuthorizationKey(credentials.Password, mac) },
                { "SRun-AuthorizationCRC32", crc }, { "SRun-Version", "SRun3K Client_1.0.13462.80424" },
                { "OSName", "Windows" }, { "OSVersion", Environment.OSVersion.VersionString }, { "Cache-Control", "no-cache" }
            };
            return new Dictionary<string, string> {
                { "action", "login" }, { "username", credentials.UserName }, { "password", password },
                { "drop", "0" }, { "pop", "0" }, { "type", "2" }, { "n", "117" },
                { "mbytes", "0" }, { "minutes", "0" }, { "ac_id", "1" }, { "mac", mac }, { "crc", crc }
            };
        }
        public static bool UsesHttpKeepalive(string version)
        {
            Version parsed;
            return Version.TryParse(version,out parsed) && parsed.Major==1 && parsed.Minor==1 && parsed.Build>=20170101;
        }
        public async Task<PortalStatus> LogoutAsync(Credentials credentials,CancellationToken token)
        {
            if(credentials==null || String.IsNullOrWhiteSpace(credentials.UserName))
                throw new InvalidOperationException("Credentials missing.");
            await transport.RequestAsync(BuildLogoutPath(credentials.UserName),null,token).ConfigureAwait(false);
            return await CheckAsync(token).ConfigureAwait(false);
        }
        public static string BuildLogoutPath(string username)
        {
            return "/cgi-bin/srun_portal?action=logout&username="+LegacySrun.UrlEncode(username)+"&ac_id=1&type=2";
        }
        public static LoginResult ClassifyLogin(string text)
        {
            text = (text ?? "").Trim();
            if (text == "login_ok" || text == "already_online_error") return new LoginResult { Success = true, Message = "login.accepted" };
            try
            {
                var value = PortalReply.Parse(text);
                string error = PortalReply.Field(value, "error"), res = PortalReply.Field(value, "res"), msg = PortalReply.Field(value, "suc_msg");
                if (msg == "login_ok" || msg == "ip_already_online_error" || error == "ok" && (res == "ok" || PortalReply.Field(value, "ecode") == "E0000"))
                    return new LoginResult { Success = true, Message = "login.accepted" };
                if (PortalReply.IsRestriction(value)) return new LoginResult { PermanentFailure = true, Message = "server.restricted" };
                string code = (error + " " + PortalReply.Field(value, "ecode") + " " + PortalReply.Field(value, "error_msg")).ToLowerInvariant();
                if (code.Contains("password_error") || code.Contains("username_error") || code.Contains("user_not_exist") ||
                    code.Contains("e2531") || code.Contains("e2553") || code.Contains("e2616") || code.Contains("密码错误") ||
                    code.Contains("账号不存在") || code.Contains("协议") || code.Contains("captcha") || code.Contains("locked"))
                    return new LoginResult { PermanentFailure = true, Message = "login.rejected" };
            }
            catch { }
            // Unknown replies are never accepted merely because they contain "ok".
            if (text == "password_error" || text == "username_error") return new LoginResult { PermanentFailure = true, Message = "login.rejected" };
            return new LoginResult { Message = "login.unconfirmed" };
        }
        public void Dispose() { if (!disposed) { disposed = true; if (heartbeat != null) heartbeat.Dispose(); } }
    }

    public static class NetworkIdentity
    {
        public static string Mac()
        {
            IPAddress source = null;
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                { socket.Connect(CampusTransport.ServerAddress, 3338); source = ((IPEndPoint)socket.LocalEndPoint).Address; }
            }
            catch { }
            string fallback = null;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                var bytes = nic.GetPhysicalAddress().GetAddressBytes();
                if (bytes.Length != 6) continue;
                bool physicalCampus = false, selected = false;
                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (source != null && address.Address.Equals(source)) selected = true;
                    byte[] ip = address.Address.GetAddressBytes();
                    if (ip.Length == 4 && ip[0] == 10 && nic.GetIPProperties().GatewayAddresses.Count > 0) physicalCampus = true;
                }
                string mac = BitConverter.ToString(bytes).Replace('-', ':').ToLowerInvariant();
                if (selected) return mac;
                if (physicalCampus && fallback == null) fallback = mac;
            }
            return fallback;
        }
    }

    public sealed class PortalHeartbeat : IDisposable
    {
        private UdpClient socket;
        private string identity;
        private bool acknowledged;
        private DateTime lastSent = DateTime.MinValue;
        private bool unsupported;
        private string routeKey;
        private readonly IPAddress server = IPAddress.Parse(CampusTransport.ServerAddress);
        public async Task<PortalState> TickAsync(string user, string mac, CancellationToken cancellationToken)
        { return await TickAsync(user, mac, null, cancellationToken).ConfigureAwait(false); }
        public async Task<PortalState> TickAsync(string user, string mac, CampusRoute route, CancellationToken cancellationToken)
        {
            string key = route == null ? "default" : route.Key;
            if (key != routeKey) { DisposeSocket(); routeKey=key; acknowledged=false; lastSent=DateTime.MinValue; unsupported=false; }
            if (unsupported) return PortalState.Unknown;
            if (DateTime.UtcNow - lastSent < TimeSpan.FromSeconds(45)) return PortalState.Unknown;
            lastSent = DateTime.UtcNow;
            try
            {
                if (identity != user || socket == null)
                {
                    DisposeSocket(); identity = user; acknowledged = false;
                    socket = new UdpClient(AddressFamily.InterNetwork);
                    socket.ExclusiveAddressUse = true;
                    if (route != null) route.Bind(socket.Client, 13335);
                    else socket.Client.Bind(new IPEndPoint(IPAddress.Any, 13335));
                    socket.Connect(server, 3338);
                }
                byte[] packet = LegacySrun.BuildPortalHeartbeat(acknowledged ? user : "CHECK_VERSION", mac, 0);
                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    deadline.CancelAfter(4000);
                    using (deadline.Token.Register(delegate { DisposeSocket(); }))
                    {
                        await socket.SendAsync(packet, packet.Length).ConfigureAwait(false);
                        var response = await socket.ReceiveAsync().ConfigureAwait(false);
                        if (!response.RemoteEndPoint.Address.Equals(server) || response.RemoteEndPoint.Port != 3338 || response.Buffer.Length < 96)
                            return PortalState.Unknown;
                        if (response.Buffer.Length == 276)
                        { unsupported = true; Store.Log("heartbeat.encrypted_mode_unsupported"); return PortalState.Unknown; }
                        string returned = ReadCString(response.Buffer, 0, 32);
                        if (returned != "CHECK_VERSION" && returned != user) return PortalState.Unknown;
                        int message = BitConverter.ToInt32(response.Buffer, 52);
                        if (message == -100) { Store.Log("heartbeat.server_restricted"); return PortalState.Restricted; }
                        if (returned == "CHECK_VERSION" && message == -2) { Store.Log("heartbeat.offline_hint"); return PortalState.Unknown; }
                        if (returned == "CHECK_VERSION" && message == -1)
                        { Store.Log("heartbeat.server_update_required"); return PortalState.Unknown; }
                        if (message == 0 || message > 0)
                        {
                            if (!acknowledged) Store.Log("heartbeat.acknowledged");
                            acknowledged = true;
                            return PortalState.Online;
                        }
                    }
                }
            }
            catch (Exception)
            {
                DisposeSocket();
                cancellationToken.ThrowIfCancellationRequested();
                if (acknowledged) Store.Log("heartbeat.unconfirmed");
            }
            return PortalState.Unknown;
        }
        private static string ReadCString(byte[] bytes, int start, int count)
        {
            int length = 0; while (length < count && bytes[start + length] != 0) length++;
            return Encoding.ASCII.GetString(bytes, start, length);
        }
        private void DisposeSocket() { var current = Interlocked.Exchange(ref socket, null); if (current != null) current.Close(); }
        public void Dispose() { DisposeSocket(); }
    }
}
