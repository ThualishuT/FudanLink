using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FudanNetLite
{
    // Small bounded HTTP/1.1 reader for the one fixed, plaintext campus endpoint.
    // No redirects, DNS, proxy discovery, compression or external hosts are used.
    internal sealed class PortalHttp
    {
        private readonly Stream input;private readonly CancellationToken token;
        private readonly byte[] buffer=new byte[4096];private int position,count,headerBytes;
        private PortalHttp(Stream input,CancellationToken token){this.input=input;this.token=token;}
        internal static Task<string> ReadAsync(Stream input,CancellationToken token){return new PortalHttp(input,token).ReadResponseAsync();}
        private async Task<int> ByteAsync()
        {
            if(position==count){count=await input.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false);position=0;if(count==0)return -1;}
            return buffer[position++];
        }
        private async Task<string> LineAsync()
        {
            var line=new StringBuilder();int b;
            while((b=await ByteAsync().ConfigureAwait(false))>=0)
            {
                if(++headerBytes>16384)throw new IOException("HTTP headers exceed budget.");
                if(b=='\r'){if(await ByteAsync().ConfigureAwait(false)!='\n')throw new IOException("Invalid HTTP line.");return line.ToString();}
                if(b=='\n')throw new IOException("Invalid HTTP line.");line.Append((char)b);
            }
            throw new EndOfStreamException();
        }
        private async Task CopyAsync(MemoryStream output,int length)
        {
            if(length<0 || output.Length+length>262144)throw new IOException("Portal response exceeds budget.");
            while(length>0)
            {
                token.ThrowIfCancellationRequested();
                if(position==count)
                {
                    count=await input.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false);position=0;
                    if(count==0)throw new EndOfStreamException();
                }
                int available=Math.Min(length,count-position);
                output.Write(buffer,position,available);position+=available;length-=available;
            }
        }
        private async Task<string> ReadResponseAsync()
        {
            string status=await LineAsync().ConfigureAwait(false);
            if(!status.StartsWith("HTTP/1.1 200 ",StringComparison.Ordinal)&&!status.StartsWith("HTTP/1.0 200 ",StringComparison.Ordinal))throw new IOException("Unexpected portal HTTP status.");
            var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);string line;
            while((line=await LineAsync().ConfigureAwait(false)).Length>0)
            {
                int colon=line.IndexOf(':');if(colon<1)throw new IOException("Invalid HTTP header.");
                string key=line.Substring(0,colon);if(headers.ContainsKey(key))throw new IOException("Ambiguous HTTP headers.");headers[key]=line.Substring(colon+1).Trim();
            }
            string transfer,length,encoding;headers.TryGetValue("Transfer-Encoding",out transfer);headers.TryGetValue("Content-Length",out length);
            if(headers.TryGetValue("Content-Encoding",out encoding)&&encoding!="identity")throw new IOException("Unexpected encoding.");
            using(var output=new MemoryStream())
            {
                if(transfer!=null)
                {
                    if(!transfer.Equals("chunked",StringComparison.OrdinalIgnoreCase)||length!=null)throw new IOException("Ambiguous HTTP framing.");
                    while(true)
                    {
                        string chunk=await LineAsync().ConfigureAwait(false);int extension=chunk.IndexOf(';');if(extension>=0)chunk=chunk.Substring(0,extension);
                        int size;if(!Int32.TryParse(chunk,System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out size)||size<0)throw new IOException("Invalid HTTP chunk.");
                        if(size==0){while((await LineAsync().ConfigureAwait(false)).Length>0){}break;}
                        await CopyAsync(output,size).ConfigureAwait(false);if((await LineAsync().ConfigureAwait(false)).Length!=0)throw new IOException("Invalid HTTP chunk terminator.");
                    }
                }
                else if(length!=null)
                {int size;if(!Int32.TryParse(length,out size))throw new IOException("Invalid HTTP length.");await CopyAsync(output,size).ConfigureAwait(false);}
                else
                {
                    while(true)
                    {
                        token.ThrowIfCancellationRequested();
                        if(position==count){count=await input.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false);position=0;if(count==0)break;}
                        await CopyAsync(output,count-position).ConfigureAwait(false);
                    }
                }
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }
}
