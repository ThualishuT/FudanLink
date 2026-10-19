using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FudanNetLite;

internal static class TransportTests
{
    private static int count;
    private static void Assert(bool value,string label){if(!value)throw new Exception(label);count++;}
    private static string Read(string value)
    {using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(value)))return PortalHttp.ReadAsync(stream,CancellationToken.None).GetAwaiter().GetResult();}
    private static void Reject(string value,string label)
    {bool rejected=false;try{Read(value);}catch(IOException){rejected=true;}Assert(rejected,label);}
    public static int Main()
    {
        try
        {
            Assert(Read("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nnot_online")=="not_online","Fixed-length response completes without waiting for EOF");
            Assert(Read("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3;ext=yes\r\nnot\r\n7\r\n_online\r\n0\r\nX-End: ok\r\n\r\n")=="not_online","Chunked status replies and trailers");
            Assert(Read("HTTP/1.0 200 OK\r\n\r\nnot_online")=="not_online","Close-delimited response");
            Reject("HTTP/1.1 302 Found\r\nLocation: http://other/\r\n\r\n","Redirect cannot become a credential destination");
            Reject("HTTP/1.1 200 OK\r\nContent-Length: 10\r\nContent-Length: 9\r\n\r\nnot_online","Duplicate framing is rejected");
            Reject("HTTP/1.1 200 OK\r\nContent-Length: 10\r\nTransfer-Encoding: chunked\r\n\r\n","Conflicting framing is rejected");
            Reject("HTTP/1.1 200 OK\r\nContent-Length: 9999999\r\n\r\n","Oversized body rejected before allocation");
            Reject("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nshort","Truncated response cannot be accepted");
            Reject("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n-1\r\n","Invalid chunk cannot overflow");
            Reject("HTTP/1.1 200 OK\r\nX-Large: "+new string('a',17000)+"\r\n\r\n","Headers have a separate bounded budget");
            string body=new string('x',65539)+"校园网";
            int bytes=Encoding.UTF8.GetByteCount(body);
            using(var stream=new FragmentedStream("HTTP/1.1 200 OK\r\nContent-Length: "+bytes+"\r\n\r\n"+body,101))
                Assert(PortalHttp.ReadAsync(stream,CancellationToken.None).GetAwaiter().GetResult()==body,"Buffered fixed-length reader preserves fragmented UTF-8 payloads");
            using(var stream=new FragmentedStream("HTTP/1.0 200 OK\r\n\r\n"+body,101))
                Assert(PortalHttp.ReadAsync(stream,CancellationToken.None).GetAwaiter().GetResult()==body,"Buffered close-delimited reader preserves fragmented payloads");
            using(var stream=new FragmentedStream("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nnot\r\n7\r\n_online\r\n0\r\n\r\n",1))
                Assert(PortalHttp.ReadAsync(stream,CancellationToken.None).GetAwaiter().GetResult()=="not_online","Chunk boundaries survive one-byte network reads");
            Reject("HTTP/1.0 200 OK\r\n\r\n"+new string('x',262145),"Close-delimited buffered reads retain the body budget");
            Reject("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nxy","Truncated buffered chunks cannot be accepted");
            Assert(new Settings().Connection=="ethernet","New settings default to wired network");
            Assert(Store.Json.Deserialize<Settings>("{\"Enabled\":true,\"IntervalSeconds\":50}").Connection=="ethernet","Existing settings migrate to wired without WLAN fallback");
            Assert(SrunPortal.UsesHttpKeepalive("1.01.20260311"),"Installed server follows native HTTP keepalive branch");
            Assert(SrunPortal.UsesHttpKeepalive("1.01.20170101"),"Native branch threshold is inclusive");
            Assert(!SrunPortal.UsesHttpKeepalive("1.01.20161231"),"Old server retains UDP branch");
            Assert(!SrunPortal.UsesHttpKeepalive("garbled")&&!SrunPortal.UsesHttpKeepalive("2.01.20260311"),"Unverified server versions do not suppress UDP");
            Console.WriteLine("Transport: "+count+" checks passed.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
    private sealed class FragmentedStream : MemoryStream
    {
        private readonly int fragment;
        internal FragmentedStream(string text,int fragment):base(Encoding.UTF8.GetBytes(text)){this.fragment=fragment;}
        public override Task<int> ReadAsync(byte[] bytes,int offset,int count,CancellationToken token)
        {token.ThrowIfCancellationRequested();return Task.FromResult(Read(bytes,offset,Math.Min(count,fragment)));}
    }
}
