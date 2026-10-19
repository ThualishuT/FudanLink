using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FudanNetLite;

public static class ProtocolTests
{
    private static int count;
    private static void Assert(bool value, string name) { if (!value) throw new Exception(name); count++; }
    public static int Main()
    {
        Assert(LegacySrun.UrlEncode("Aa09.-_ ~:") == "Aa09.-_+%7e%3a", "Exact legacy URL alphabet and hex case");
        Assert(LegacySrun.EncodePassword("ABC",117) == "j<9jj8", "Independent hand-calculated nibble vector");
        Assert(LegacySrun.EncodePassword("ABC",LegacySrun.PasswordKey) == "7jjAAj", "Password key differs from client version n=117");
        Assert(LegacySrun.EncodePassword("",117) == "", "Empty input");
        Assert(LegacySrun.AuthorizationKey("Fixture123!?","00:11:22:33:44:55") == "bbbf53d073c37a25654edbee707ccf73", "Independent Node crypto signature vector");
        Assert(LegacySrun.Crc32State("123456789") == "340BC6D9", "Standard reflected CRC with no final XOR");
        var bytes = LegacySrun.BuildPortalHeartbeat("CHECK_VERSION","00:11:22:33:44:55",0x12345678);
        Assert(bytes.Length == 60 && bytes[13] == 0 && bytes[31] == 0 && bytes[49] == 0 && bytes[51] == 0 && bytes[56] == 0 && bytes[59] == 0, "Fixed packet zero-padding");
        Assert(Encoding.ASCII.GetString(bytes,0,13) == "CHECK_VERSION" && Encoding.ASCII.GetString(bytes,32,17) == "00:11:22:33:44:55", "Recovered packet fields");
        Assert(bytes[52] == 0x78 && bytes[53] == 0x56 && bytes[54] == 0x34 && bytes[55] == 0x12, "Packet uses little endian");
        bool rejected = false; try { LegacySrun.BuildPortalHeartbeat(new String('x',33),"x",0); } catch (ArgumentException) { rejected = true; }
        Assert(rejected,"Packet rejects oversized identities");
        rejected = false; try { LegacySrun.EncodePassword("密码",117); } catch (EncoderFallbackException) { rejected = true; }
        Assert(rejected,"Unverified non-ASCII input cannot silently corrupt a password");
        Dictionary<string,string> headers;
        var form=SrunPortal.BuildLogin(new Credentials{UserName="fixture&a=b",Password="ABC"},"00:11:22:33:44:55","1700000000",out headers);
        Assert(form["action"]=="login" && form["type"]=="2" && form["n"]=="117" && form["ac_id"]=="1" && form["password"]=="7jjAAj", "Recovered defaults and runtime-verified password key");
        Assert(headers["SRun-AuthorizationCRC32"]==form["crc"] && headers["SRun-ClientTime"]=="1700000000", "Time and CRC are bound consistently");
        Assert(!SrunPortal.ClassifyLogin("<html>everything ok</html>").Success,"HTML cannot spoof a successful login");
        Assert(SrunPortal.ClassifyLogin("{\"error\":\"ok\",\"suc_msg\":\"login_ok\"}").Success,"Exact successful JSON response");
        Assert(SrunPortal.ClassifyLogin("{\"error\":\"password_error\"}").PermanentFailure,"Bad password stops retries");
        var fake=new FakeTransport();
        using(var portal=new SrunPortal(fake,null))
        {
            Assert(portal.CheckAsync(CancellationToken.None).Result.State==PortalState.Offline,"Offline JSON is recognized");
            fake.Body="malformed proxy reply";
            Assert(portal.CheckAsync(CancellationToken.None).Result.State==PortalState.Unknown,"Unknown HTTP body remains unknown");
        }
        Assert(SrunPortal.BuildLogoutPath("fixture&a=b")=="/cgi-bin/srun_portal?action=logout&username=fixture%26a%3db&ac_id=1&type=2","Logout encodes account and uses fixed protocol endpoint");
        using(var portal=new SrunPortal(fake,null))
        {
            fake.Body="{\"error\":\"not_online_error\"}";
            Assert(portal.LogoutAsync(new Credentials{UserName="fixture"},CancellationToken.None).Result.State==PortalState.Offline,"Logout is confirmed by server state");
            fake.Body="{\"error\":\"ok\",\"user_name\":\"fixture\"}";
            Assert(portal.LogoutAsync(new Credentials{UserName="fixture"},CancellationToken.None).Result.State==PortalState.Online,"Still-online response cannot be claimed disconnected");
            fake.Body="malformed";
            Assert(portal.LogoutAsync(new Credentials{UserName="fixture"},CancellationToken.None).Result.State==PortalState.Unknown,"Unknown logout result remains unconfirmed");
        }
        Console.WriteLine("Protocol: "+count+" offline tests passed."); return 0;
    }
    private sealed class FakeTransport:IPortalTransport
    {
        public string Body="{\"error\":\"not_online_error\"}";
        public int Calls;
        public Task<string> RequestAsync(string path,IDictionary<string,string> form,CancellationToken token)
        {token.ThrowIfCancellationRequested();Calls++;return Task.FromResult(Body);}
        public Task<string> RequestAsync(string path,IDictionary<string,string> form,IDictionary<string,string> headers,CancellationToken token)
        {return RequestAsync(path,form,token);}
    }
}
