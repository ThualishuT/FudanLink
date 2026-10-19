using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;

namespace FudanNetLite
{
    public interface IPortalTransport
    {
        Task<string> RequestAsync(string path, IDictionary<string, string> form, CancellationToken cancellationToken);
        Task<string> RequestAsync(string path, IDictionary<string, string> form, IDictionary<string, string> headers, CancellationToken cancellationToken);
    }

    internal delegate Task<string> PortalExchange(string path, IDictionary<string,string> form,
        IDictionary<string,string> headers, CancellationToken token);

    public sealed class CampusTransport : IPortalTransport
    {
        public const string ServerAddress = "10.250.3.66";
        public const string Server = "http://" + ServerAddress;
        internal const string StatusPath = "/cgi-bin/rad_user_info";
        private static readonly IPAddress server = IPAddress.Parse(ServerAddress);
        private static readonly string userAgent = "FudanLink/" + Assembly.GetExecutingAssembly().GetName().Version.ToString(2);
        private static readonly SemaphoreSlim selection = new SemaphoreSlim(1, 1);
        private static CampusRoute selected;
        private CampusRoute route;
        private readonly string connection;
        private readonly Func<CancellationToken,Task<string>> selectRoute;
        private readonly PortalExchange exchange;
        public CampusTransport() : this(Store.LoadSettings().Connection) { }
        public CampusTransport(string connection)
        {
            this.connection = connection == "wifi" ? "wifi" : "ethernet";
            selectRoute = SelectAsync;
            exchange = (path, form, headers, token) => ExchangeAsync(route, path, form, headers, token);
        }
        internal CampusTransport(Func<CancellationToken,Task<string>> selectRoute, PortalExchange exchange)
        {
            if(selectRoute==null)throw new ArgumentNullException("selectRoute");
            if(exchange==null)throw new ArgumentNullException("exchange");
            this.selectRoute=selectRoute;this.exchange=exchange;
        }
        public CampusRoute Route { get { return route; } }
        public Task<string> RequestAsync(string path, IDictionary<string, string> form, CancellationToken token)
        { return RequestAsync(path, form, null, token); }
        public async Task<string> RequestAsync(string path, IDictionary<string, string> form, IDictionary<string, string> headers, CancellationToken token)
        {
            if (String.IsNullOrEmpty(path) || !path.StartsWith("/",StringComparison.Ordinal) || path.StartsWith("//",StringComparison.Ordinal) || path.IndexOfAny(new[]{'\r','\n',' '})>=0)
                throw new ArgumentException("Invalid portal path.");
            token.ThrowIfCancellationRequested();
            string probe = null;
            if (route == null) probe = await selectRoute(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (form == null && path == StatusPath && probe != null) return probe;
            try { return await exchange(path,form,headers,token).ConfigureAwait(false); }
            catch
            {
                token.ThrowIfCancellationRequested();
                Invalidate();
                // Logout is also a state-changing request, despite using GET.
                if (form != null || path != StatusPath) throw;
            }
            return await selectRoute(token).ConfigureAwait(false);
        }
        public async Task EnsureRouteAsync(CancellationToken token)
        { if(route==null) await selectRoute(token).ConfigureAwait(false); }
        private void Invalidate()
        {
            if (Object.ReferenceEquals(selected,route)) selected=null;
            route=null;
        }
        private async Task<string> SelectAsync(CancellationToken token)
        {
            await selection.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var candidates = CampusRoute.Candidates(connection); var cached=selected;
                if(cached!=null)
                {
                    foreach(var candidate in candidates) if(candidate.Key==cached.Key)
                    {
                        var reply=await ProbeAsync(candidate,token).ConfigureAwait(false);
                        if(reply!=null){route=candidate;selected=candidate;return reply;}
                        break;
                    }
                }
                // Only read-only status probes are raced. Credentials are sent once,
                // on the path whose real authentication server reply was validated.
                using(var race=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var pending=new List<Task<KeyValuePair<CampusRoute,string>>>();
                    foreach(var candidate in candidates) if(cached==null || candidate.Key!=cached.Key) pending.Add(ProbeRouteAsync(candidate,race.Token));
                    while(pending.Count>0)
                    {
                        var done=await Task.WhenAny(pending).ConfigureAwait(false);pending.Remove(done);
                        var reply=await done.ConfigureAwait(false);
                        if(reply.Value==null)continue;
                        route=reply.Key;selected=route;race.Cancel();
                        // Probe helpers absorb their failures, so losing operations
                        // close their sockets and finish without unobserved exceptions.
                        await Task.WhenAll(pending).ConfigureAwait(false);
                        Store.Log("route.physical.confirmed");return reply.Value;
                    }
                }
                token.ThrowIfCancellationRequested();throw new IOException("Campus interface unavailable.");
            }
            finally{selection.Release();}
        }
        private static async Task<KeyValuePair<CampusRoute,string>> ProbeRouteAsync(CampusRoute candidate,CancellationToken token)
        { return new KeyValuePair<CampusRoute,string>(candidate,await ProbeAsync(candidate,token).ConfigureAwait(false)); }
        private static async Task<string> ProbeAsync(CampusRoute candidate,CancellationToken token)
        {
            try
            {
                var text=await ExchangeAsync(candidate,StatusPath,null,null,token).ConfigureAwait(false);
                return PortalReply.ClassifyStatus(text).State==PortalState.Unknown?null:text;
            }
            catch{return null;}
        }
        private static async Task<string> ExchangeAsync(CampusRoute route,string path,IDictionary<string,string> form,IDictionary<string,string> headers,CancellationToken token)
        {
            byte[] body=form==null?new byte[0]:Encoding.UTF8.GetBytes(FormEncode(form));
            try
            {
                using(var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp))
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    deadline.CancelAfter(6000);route.Bind(socket,0);socket.NoDelay=true;
                    using(deadline.Token.Register(socket.Close))
                    {
                        try
                        {
                            await Task.Factory.FromAsync(socket.BeginConnect,socket.EndConnect,server,80,null).ConfigureAwait(false);
                            using(var stream=new NetworkStream(socket,false))
                            {
                                var request=new StringBuilder(form==null?"GET ":"POST ");request.Append(path).Append(" HTTP/1.1\r\nHost: ").Append(ServerAddress).Append("\r\nConnection: close\r\nUser-Agent: ").Append(userAgent).Append("\r\nAccept: application/json,text/plain\r\n");
                                if(headers!=null)foreach(var header in headers)
                                {
                                    if(header.Key.IndexOfAny(new[]{'\r','\n',':',' '})>=0 || header.Value.IndexOfAny(new[]{'\r','\n'})>=0)throw new ArgumentException("Invalid portal header.");
                                    if(header.Key.Equals("Host",StringComparison.OrdinalIgnoreCase)||header.Key.Equals("Content-Length",StringComparison.OrdinalIgnoreCase)||header.Key.Equals("Connection",StringComparison.OrdinalIgnoreCase)||header.Key.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Reserved header.");
                                    request.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                                }
                                if(form!=null)request.Append("Content-Type: application/x-www-form-urlencoded; charset=utf-8\r\nContent-Length: ").Append(body.Length).Append("\r\n");
                                request.Append("\r\n");byte[] head=Encoding.ASCII.GetBytes(request.ToString());
                                await stream.WriteAsync(head,0,head.Length,deadline.Token).ConfigureAwait(false);
                                if(body.Length>0)await stream.WriteAsync(body,0,body.Length,deadline.Token).ConfigureAwait(false);
                                return await PortalHttp.ReadAsync(stream,deadline.Token).ConfigureAwait(false);
                            }
                        }
                        catch{token.ThrowIfCancellationRequested();if(deadline.IsCancellationRequested)throw new TimeoutException("Portal deadline.");throw;}
                    }
                }
            }
            finally{Array.Clear(body,0,body.Length);}
        }
        public static string FormEncode(IDictionary<string,string> values)
        {
            var result=new StringBuilder();foreach(var item in values){if(result.Length>0)result.Append('&');result.Append(LegacySrun.UrlEncode(item.Key)).Append('=').Append(LegacySrun.UrlEncode(item.Value??""));}return result.ToString();
        }
    }
}
