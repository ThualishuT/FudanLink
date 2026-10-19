using System.Drawing;

namespace FudanNetLite
{
    internal sealed class Presentation
    {
        public string Title, Detail, Badge; public Color Color;
        public bool Online;
        public static Presentation From(string category, bool enabled, bool disconnected)
        {
            if(category=="action.connecting"||category=="action.disconnecting")return Make(category=="action.connecting"?"正在连接":"正在断开","请稍候","处理中",Color.FromArgb(38,118,180));
            if(category=="logout.unconfirmed"){var value=Make("断开尚未确认","点击图标重试断开","需要关注",Color.FromArgb(195,123,41));value.Online=true;return value;}
            if(category=="connect.unconfirmed")return Make("暂时无法连接","请检查有线网络后重试","未连接",Color.FromArgb(195,123,41));
            if (category == "login.blocked" || category == "state.restricted") return Make("需要处理认证", "请检查账号或校园网状态", "需要关注", Color.FromArgb(195, 123, 41));
            if (category == "state.online") {var value=Make("已连接校园网","点击图标断开连接","连接正常",Color.FromArgb(20,137,113));value.Online=true;return value;}
            if(category=="login.transient_failure" && disconnected)return Make("连接尚未完成","点击图标重新连接","待重试",Color.FromArgb(195,123,41));
            if(disconnected)return Make(category=="state.disconnected"||category=="state.offline"?"已断开连接":"连接已暂停","点击图标连接校园网","未连接",Color.FromArgb(124,137,157));
            if(!enabled)return Make("自动连接已暂停","点击图标手动连接","已暂停",Color.FromArgb(124,137,157));
            if (category == "state.offline" || category == "login.transient_failure") return Make("正在重新连接", "后台将自动重试", "连接中", Color.FromArgb(38, 118, 180));
            if (category == "state.unknown") return Make("等待校园网络", "点击图标尝试连接", "检查中", Color.FromArgb(195, 139, 55));
            return Make("正在检查连接", "正在获取认证状态", "检查中", Color.FromArgb(38, 118, 180));
        }
        private static Presentation Make(string title, string detail, string badge, Color color)
        { return new Presentation { Title = title, Detail = detail, Badge = badge, Color = color }; }
    }
}
