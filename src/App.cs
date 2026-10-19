using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
[assembly: AssemblyTitle("FudanLink")]
[assembly: AssemblyProduct("FudanLink")]
[assembly: AssemblyDescription("Lightweight campus network companion")]
[assembly: AssemblyVersion("1.3.1.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]
namespace FudanNetLite
{
    internal static class Program
    {
        internal static TrayContext ActiveContext;
        internal static string Executable
        {
            get
            {
                string installed = @"C:\Program Files\FudanLink\FudanLink.exe";
                return File.Exists(installed) ? installed : Assembly.GetExecutingAssembly().Location;
            }
        }
        internal static string WorkerName { get { return Name("Worker"); } }
        internal static string StopName { get { return Name("Stop"); } }
        internal static string Name(string purpose)
        {
#if OFFLINE_TESTS
            return @"Local\FudanLink.Tests." + purpose + "." + System.IO.Path.GetFileName(Store.Root);
#else
            return @"Local\FudanNetLite." + purpose + "." + WindowsIdentity.GetCurrent().User.Value;
#endif
        }
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Store.Initialize();
                string mode = args.Length == 0 ? "--open" : args[0];
                if (mode == "--stop") { StopWorker(); return 0; }
                if (mode == "--check")
                {
                    using (var portal = new SrunPortal())
                    {
                        var status = portal.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                        Store.AtomicWrite(Path.Combine(Store.Root, "check.json"), System.Text.Encoding.UTF8.GetBytes(Store.Json.Serialize(status)));
                        return status.State == PortalState.Online ? 0 : 2;
                    }
                }
                if (mode == "--enable-startup") { Store.SetStartup(true, Executable); return 0; }
                if (mode == "--disable-startup") { Store.SetStartup(false, Executable); return 0; }
                bool created;
                using (var mutex = new Mutex(true, WorkerName, out created))
                {
                    if (!created)
                    {
                        if (mode != "--background") Signal(mode == "--settings" ? "Settings" : "Show", true);
                        return 0;
                    }
                    try
                    {
                        using (var context = new TrayContext())
                        {
                            ActiveContext = context;
                            if (mode != "--background") context.OpenWindow(mode == "--settings");
                            Application.Run(context);
                            ActiveContext = null;
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            }
            catch
            {
                Store.Log("application.error");
                if (args.Length == 0 || args[0] == "--settings" || args[0] == "--open")
                    MessageBox.Show("暂时无法打开，请重新启动 FudanLink。", "FudanLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            }
        }
        internal static bool Signal(string purpose, bool retry)
        {
            for (int i = 0; i < (retry ? 20 : 1); i++)
            {
                EventWaitHandle handle;
                if (EventWaitHandle.TryOpenExisting(Name(purpose), out handle))
                { using (handle) handle.Set(); return true; }
                if (retry) Thread.Sleep(50);
            }
            return false;
        }
        internal static void StopWorker()
        {
            if (ActiveContext != null) { ActiveContext.RequestShutdown(); return; }
            Signal("Stop", false);
        }
        internal static void StartWorker()
        {
            Process.Start(new ProcessStartInfo(Executable, "--background")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        }
        internal static Task RestartWorkerAsync()
        {
            if (ActiveContext != null) return ActiveContext.ReconfigureAsync();
            if (!Signal("Reload", false)) StartWorker();
            return Task.FromResult(0);
        }
    }
}
