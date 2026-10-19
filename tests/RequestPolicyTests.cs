using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FudanNetLite;

internal static class RequestPolicyTests
{
    private static int count;
    private static void Assert(bool condition,string message)
    {if(!condition)throw new Exception(message);count++;}
    public static int Main()
    {
        try
        {
            Run().GetAwaiter().GetResult();
            Console.WriteLine("Request policy: "+count+" checks passed.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static async Task Run()
    {
        foreach(bool login in new[]{false,true})
        {
            int selected=0,sent=0;
            var transport=new CampusTransport(token=>{selected++;return Task.FromResult<string>(null);},
                (path,form,headers,token)=>{sent++;throw new IOException("Reply lost after submission.");});
            bool failed=false;
            try
            {
                await transport.RequestAsync(login?"/cgi-bin/srun_portal":SrunPortal.BuildLogoutPath("fixture"),
                    login?new Dictionary<string,string>{{"action","login"}}:null,CancellationToken.None);
            }
            catch(IOException){failed=true;}
            Assert(failed&&sent==1&&selected==1,login?"Ambiguous login must never be replayed":"Ambiguous GET logout must never be replayed");
        }
        int selections=0,exchanges=0;
        var statusTransport=new CampusTransport(token=>Task.FromResult(++selections==1?null:"not_online"),
            (path,form,headers,token)=>{exchanges++;throw new IOException("Route failed.");});
        Assert(await statusTransport.RequestAsync(CampusTransport.StatusPath,null,CancellationToken.None)=="not_online"&&selections==2&&exchanges==1,
            "Read-only check can recover through one validated route probe");
        int calls=0;
        var canceledTransport=new CampusTransport(token=>{calls++;return Task.FromResult("not_online");},
            (path,form,headers,token)=>{calls++;return Task.FromResult("unexpected");});
        using(var stop=new CancellationTokenSource())
        {
            stop.Cancel();bool canceled=false;
            try{await canceledTransport.RequestAsync(CampusTransport.StatusPath,null,stop.Token);}
            catch(OperationCanceledException){canceled=true;}
            Assert(canceled&&calls==0,"Already canceled operations do not select a route or submit a request");
        }
        foreach(string invalid in new[]{"//other/path","/path\r\nInjected: yes","https://other/path"})
        {
            bool rejected=false;
            try{await canceledTransport.RequestAsync(invalid,null,CancellationToken.None);}
            catch(ArgumentException){rejected=true;}
            Assert(rejected&&calls==0,"Invalid paths are rejected before any I/O");
        }
    }
}
