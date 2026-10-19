using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace FudanNetLite
{
    public static class PortalReply
    {
        public static Dictionary<string, object> Parse(string text)
        {
            text = (text ?? "").Trim();
            if (!text.StartsWith("{", StringComparison.Ordinal))
            {
                int start = text.IndexOf('('), end = text.LastIndexOf(')');
                if (start < 1 || end <= start || text.Substring(0, start).IndexOfAny(new[] { '<', '>', ' ', '\n', '\r' }) >= 0)
                    throw new FormatException("Unexpected portal reply.");
                text = text.Substring(start + 1, end - start - 1).Trim();
            }
            return new JavaScriptSerializer { MaxJsonLength = 262144, RecursionLimit = 32 }
                .Deserialize<Dictionary<string, object>>(text);
        }
        public static string Field(IDictionary<string, object> value, string name)
        {
            object result; return value != null && value.TryGetValue(name, out result) && result != null ? Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) : "";
        }
        public static PortalStatus ClassifyStatus(string text)
        {
            text = (text ?? "").Trim();
            if (text == "not_online" || text == "not_online_error") return Status(PortalState.Offline, "offline.confirmed");
            try
            {
                var value = Parse(text); string error = Field(value, "error");
                if ((error == "ok" || error == "" && Field(value, "ecode") == "E0000") &&
                    (!String.IsNullOrEmpty(Field(value, "user_name")) || !String.IsNullOrEmpty(Field(value, "username"))))
                    return Status(PortalState.Online, "online.confirmed");
                if (error == "not_online" || error == "not_online_error" || Field(value, "res") == "not_online")
                    return Status(PortalState.Offline, "offline.confirmed");
                if (IsRestriction(value)) return Status(PortalState.Restricted, "server.restricted");
            }
            catch { }
            return Status(PortalState.Unknown, "status.unrecognized");
        }
        public static bool IsRestriction(IDictionary<string, object> value)
        {
            // A zero remain_seconds is also used by free/unlimited accounts; it is
            // deliberately not interpreted as an exhausted balance or time quota.
            string code = (Field(value, "error") + " " + Field(value, "ecode") + " " + Field(value, "error_msg")).ToLowerInvariant();
            return code.Contains("time_limit") || code.Contains("time_expire") || code.Contains("欠费") || code.Contains("超过上网时间") || code.Contains("余额不足") || code.Contains("rate_limit");
        }
        private static PortalStatus Status(PortalState state, string message) { return new PortalStatus { State = state, Message = message }; }
    }
}
