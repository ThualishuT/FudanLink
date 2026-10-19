using System;
using System.Collections.Generic;
using FudanNetLite;
public static class PortalReplyTests
{
    static void Equal(object actual, object expected, string message) { if (!actual.Equals(expected)) throw new Exception(message); }
    public static int Main()
    {
        Equal(PortalReply.ClassifyStatus("not_online").State, PortalState.Offline, "Explicit offline");
        Equal(PortalReply.ClassifyStatus("{\"error\":\"ok\",\"user_name\":\"fixture-only\",\"remain_seconds\":0}").State, PortalState.Online, "Unlimited account is online");
        Equal(PortalReply.ClassifyStatus("jsonp({\"error\":\"not_online_error\"});").State, PortalState.Offline, "JSONP is data, not evaluated");
        Equal(PortalReply.ClassifyStatus("<html>login ok</html>").State, PortalState.Unknown, "HTML redirect is unknown");
        Equal(PortalReply.ClassifyStatus("{\"error\":\"ok\"}").State, PortalState.Unknown, "Generic OK is not an online identity");
        Equal(PortalReply.ClassifyStatus("{\"error\":\"time_limit\"}").State, PortalState.Restricted, "Server quota is respected");
        Equal(PortalReply.ClassifyStatus("{\"error\":\"password_error\"}").State, PortalState.Unknown, "Check error is not offline permission");
        Equal(CampusTransport.FormEncode(new Dictionary<string,string>{{"username","fixture&a=b"}}), "username=fixture%26a%3db", "Credentials cannot inject form parameters");
        Console.WriteLine("Portal reply: 8 offline tests passed."); return 0;
    }
}
