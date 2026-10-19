using System;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using FudanNetLite;

internal static class LiveCycleTests
{
    [STAThread]
    public static int Main()
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        int result=1;bool started=false;
        using(var context=new TrayContext())
        using(var timer=new Timer{Interval=250})
        {
            Program.ActiveContext=context;
            timer.Tick+=async delegate
            {
                if(started)return;started=true;timer.Stop();
                bool automatic=Store.LoadSettings().Enabled;
                try
                {
                    await context.CheckAsync();
                    if(!context.Snapshot.Online)throw new Exception("Initial session is not online.");
                    context.OpenWindow(false);
                    var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                    var window=(MainForm)typeof(TrayContext).GetField("window",flags).GetValue(context);
                    window.Location=new Point(-32000,-32000);window.ShowInTaskbar=false;
                    var button=(Button)typeof(MainForm).GetField("artwork",flags).GetValue(window);
                    button.PerformClick();
                    while(context.ConnectionBusy)await Task.Delay(50);
                    if(!Store.LoadSettings().Disconnected || context.Snapshot.Title!="已断开连接")throw new Exception("Logout did not confirm offline.");
                    Console.WriteLine("Real icon click: disconnected; automatic reconnect remains suspended.");
                    await Task.Delay(2200);
                    if(context.Snapshot.Online)throw new Exception("Manual disconnect was undone.");
                    button.PerformClick();
                    while(context.ConnectionBusy)await Task.Delay(50);
                    if(Store.LoadSettings().Disconnected || !context.Snapshot.Online)throw new Exception("Reconnect did not confirm online.");
                    if(Store.LoadSettings().Enabled!=automatic)throw new Exception("Manual toggle changed automatic connection preference.");
                    Console.WriteLine("Real icon click: login and online state confirmed on wired network.");result=0;
                }
                catch(Exception error){Console.Error.WriteLine(error.Message);}
                if(Store.LoadSettings().Disconnected || !context.Snapshot.Online)await context.SetConnectedAsync(true);
                Console.WriteLine("Recovery online="+context.Snapshot.Online);
                context.RequestShutdown();
            };
            timer.Start();Application.Run(context);Program.ActiveContext=null;
        }
        return result;
    }
}
