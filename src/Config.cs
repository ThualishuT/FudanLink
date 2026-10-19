using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace FudanNetLite
{
    public sealed class Settings
    {
        public bool Enabled { get; set; }
        public int IntervalSeconds { get; set; }
        public string Connection { get; set; }
        public bool Disconnected { get; set; }
        public Settings() { Enabled = true; IntervalSeconds = 50; Connection = "ethernet"; }
    }

    public static class Store
    {
#if OFFLINE_TESTS
        public static readonly string Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", Guid.NewGuid().ToString("N"));
        private static bool testStartup;
#else
        public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FudanNetLite");
#endif
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        private static readonly object Gate = new object();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FudanNetLite.credentials.v1");
        public static void Initialize()
        {
            Directory.CreateDirectory(Root);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var sid = WindowsIdentity.GetCurrent().User;
            security.SetOwner(sid);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(Root, security);
        }
        public static Settings LoadSettings()
        {
            try { return Json.Deserialize<Settings>(File.ReadAllText(Path.Combine(Root, "settings.json"))) ?? new Settings(); }
            catch { return new Settings(); }
        }
        public static void SaveSettings(Settings settings)
        {
            if (settings.IntervalSeconds < 30 || settings.IntervalSeconds > 300) throw new ArgumentOutOfRangeException("IntervalSeconds");
            AtomicWrite(Path.Combine(Root, "settings.json"), Encoding.UTF8.GetBytes(Json.Serialize(settings)));
        }
        public static Credentials LoadCredentials()
        {
            byte[] plain = null;
            try
            {
                plain = ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(Root, "credentials.dat")), Entropy, DataProtectionScope.CurrentUser);
                return Json.Deserialize<Credentials>(Encoding.UTF8.GetString(plain)) ?? new Credentials();
            }
            catch { return new Credentials(); }
            finally { if (plain != null) Array.Clear(plain, 0, plain.Length); }
        }
        public static void SaveCredentials(Credentials credentials)
        {
            var plain = Encoding.UTF8.GetBytes(Json.Serialize(credentials));
            try { AtomicWrite(Path.Combine(Root, "credentials.dat"), ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public static void AtomicWrite(string path, byte[] bytes)
        {
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public static void Log(string category)
        {
            // Call sites supply fixed categories, never raw server replies or credentials.
            lock (Gate)
            {
                try
                {
                    var path = Path.Combine(Root, "activity.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 65536)
                    {
                        var old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + " " + category + Environment.NewLine, Encoding.UTF8);
                    AtomicWrite(Path.Combine(Root, "status.txt"), Encoding.UTF8.GetBytes(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + category));
                }
                catch { }
            }
        }
        public static bool StartupEnabled()
        {
#if OFFLINE_TESTS
            return testStartup;
#else
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && key.GetValue("FudanNetLite") != null;
#endif
        }
        public static void SetStartup(bool enabled, string executable)
        {
#if OFFLINE_TESTS
            testStartup=enabled;
#else
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled) key.SetValue("FudanNetLite", "\"" + executable + "\" --background", RegistryValueKind.String);
                else key.DeleteValue("FudanNetLite", false);
            }
#endif
        }
    }
}
