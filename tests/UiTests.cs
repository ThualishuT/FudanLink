using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FudanNetLite;

internal static class UiTests
{
    private static int count;
    [DllImport("user32.dll")]private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")]private static extern bool AreDpiAwarenessContextsEqual(IntPtr left,IntPtr right);
    private static void Assert(bool condition,string name){if(!condition)throw new Exception(name);count++;}
    [STAThread]
    public static int Main(string[] args)
    {
        Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Store.Initialize();
            Assert(Presentation.From("state.online",true,false).Title=="已连接校园网","Online presentation");
            Assert(Presentation.From("state.disconnected",true,true).Title=="已断开连接","Manual disconnect survives enabled reconnect preference");
            Assert(Presentation.From("login.blocked",true,false).Badge=="需要关注","Rejected authentication stays visible");
            Assert(Presentation.From("state.online",false,false).Online,"Online remains visible with automatic login disabled");
            using(var bitmap=Brand.Bitmap(256,null))Assert(bitmap.Width==256&&bitmap.GetPixel(0,0).A==0,"Transparent vector icon");
            using(var icon=Brand.CreateIcon(Theme.Accent))Assert(icon.Width>=32,"Tray icon has sufficient native resolution");
            using(var window=new MainForm(null))
            {
                window.StartPosition=FormStartPosition.Manual;window.Location=new Point(-32000,-32000);window.ShowInTaskbar=false;
                window.Show();Application.DoEvents();
                Assert(AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(),new IntPtr(-4)),"Actual runtime is PerMonitorV2 DPI aware");
                Assert(window.AutoScaleMode==AutoScaleMode.Dpi,"DPI autoscaling enabled");
                Assert(window.Text=="FudanLink","English product name");
                var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                var connectionButton=(Button)typeof(MainForm).GetField("artwork",flags).GetValue(window);
                Assert(connectionButton.AccessibleRole==AccessibleRole.PushButton && connectionButton.TabStop,"Power icon supports keyboard and accessibility");
                Assert(connectionButton.AccessibleName=="断开校园网","Online icon advertises disconnect");
                Assert(typeof(MainForm).GetField("advanced",flags)==null,"Old-client compatibility menu removed");
                Assert(Presentation.From("logout.unconfirmed",true,true).Online,"Unconfirmed logout remains retryable");
                TestTextLayout(window);
                window.Close();Application.DoEvents();Assert(!window.IsDisposed&&!window.Visible,"Close minimizes to tray");
                window.ShowPage(true);window.Show();Application.DoEvents();Assert(window.Visible,"Settings can be reopened");
                window.WindowState=FormWindowState.Minimized;Application.DoEvents();Assert(!window.Visible,"Minimize hides the window");
                window.AllowExit=true;window.Close();Assert(window.IsDisposed,"Explicit quit disposes the window");
            }
            TestLifecycle();
            TestShutdownCancelsCheck();
            Console.WriteLine("UI: "+count+" checks passed.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine("UI test failed: "+error.GetType().Name+" "+error.Message);return 1;}
    }
    private static void TestTextLayout(MainForm window)
    {
        var fonts=new System.Collections.Generic.Dictionary<Control,Font>();CollectFonts(window,fonts);
        foreach(float factor in new[]{1F,1.5F,2F,2.5F})
        {
            foreach(var entry in fonts)entry.Key.Font=new Font(entry.Value.FontFamily,entry.Value.SizeInPoints*factor,entry.Value.Style);
            foreach(bool settings in new[]{false,true})
            {
                window.ShowPage(settings);window.PerformLayout();Application.DoEvents();
                CheckText(window,factor,settings);
            }
        }
        foreach(var entry in fonts){entry.Key.Font=entry.Value;}
        window.ShowPage(false);window.PerformLayout();
    }
    private static void CollectFonts(Control parent,System.Collections.Generic.Dictionary<Control,Font> fonts)
    {fonts[parent]=(Font)parent.Font.Clone();foreach(Control child in parent.Controls)CollectFonts(child,fonts);}
    private static void CheckText(Control parent,float scale,bool settings)
    {
        foreach(Control child in parent.Controls)
        {
            if(!child.Visible)continue;
            var label=child as Label;
            if(label!=null && !String.IsNullOrEmpty(label.Text))
            {
                var measured=TextRenderer.MeasureText(label.Text,label.Font,new Size(Math.Max(1,label.ClientSize.Width-label.Padding.Horizontal),Int32.MaxValue),TextFormatFlags.WordBreak|TextFormatFlags.TextBoxControl);
                Assert(label.ClientSize.Height>=measured.Height+label.Padding.Vertical,"Label fits "+scale+" "+label.Text);
            }
            var box=child as TextBox;
            if(box!=null)Assert(box.Height>=box.PreferredHeight,"Input font fits at "+scale);
            var combo=child as ComboBox;
            if(combo!=null)
            {
                var grid=(TableLayoutPanel)combo.Parent;int row=grid.GetRow(combo);
                Assert(grid.GetRowHeights()[row]>=combo.Height,"Dropdown cannot overlap next row at "+scale);
            }
            CheckText(child,scale,settings);
        }
    }
    private static void TestLifecycle()
    {
        bool created;
        using(var mutex=new Mutex(true,Program.WorkerName,out created))
        {
            Assert(created,"No other FudanLink host during lifecycle test");
            try
            {
                using(var context=new TrayContext(keepalive=>new OfflineSession()))
                using(var timer=new System.Windows.Forms.Timer{Interval=120})
                {
                    Program.ActiveContext=context;Exception failure=null;int phase=0;
                    timer.Tick+=async delegate
                    {
                        timer.Stop();
                        try
                        {
                            var type=typeof(TrayContext);var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                            var tray=(NotifyIcon)type.GetField("tray",flags).GetValue(context);
                            if(phase==0){Assert(tray.Visible,"Background host publishes tray icon");context.OpenWindow(false);var form=(MainForm)type.GetField("window",flags).GetValue(context);form.Location=new Point(-32000,-32000);form.ShowInTaskbar=false;form.Close();Assert(!form.IsDisposed&&!form.Visible,"Host keeps hidden window alive");}
                            if(phase==1){Assert(Program.Signal("Settings",false),"Settings activation signal accepted");}
                            if(phase==2){var form=(MainForm)type.GetField("window",flags).GetValue(context);Assert(form.Visible,"Settings signal reopens same window");Assert((bool)typeof(MainForm).GetField("settingsPage",flags).GetValue(form),"Settings signal selects correct page");await context.ReconfigureAsync();Assert(tray.Visible,"Saving settings keeps tray host alive");}
                            if(phase==3){Assert(Program.Signal("Stop",false),"Exit signal accepted");phase++;return;}
                            phase++;timer.Start();
                        }
                        catch(Exception error){failure=error;context.RequestShutdown();}
                    };
                    timer.Start();Application.Run(context);Program.ActiveContext=null;
                    if(failure!=null)throw failure;
                    Assert(phase==4,"Exit completed cleanly after worker cancellation");
                }
            }
            finally{mutex.ReleaseMutex();}
        }
    }
    private static void TestShutdownCancelsCheck()
    {
        var probe=new OfflineSession{WaitForCancellation=true};
        using(var context=new TrayContext(keepalive=>keepalive?new OfflineSession():probe))
        using(var timer=new System.Windows.Forms.Timer{Interval=20})
        {
            Task pending=null;
            timer.Tick+=delegate
            {
                timer.Stop();pending=context.CheckAsync();
                Assert(!pending.IsCompleted,"Status probe is in flight before shutdown");
                context.RequestShutdown();
            };
            timer.Start();Application.Run(context);
            Assert(pending!=null&&pending.IsCanceled,"Shutdown cancels an in-flight manual status request");
            Assert(probe.Disposed,"Shutdown disposes the canceled session");
        }
    }
    private sealed class OfflineSession : IPortalSession
    {
        internal bool WaitForCancellation,Disposed;
        public async Task<PortalStatus> CheckAsync(CancellationToken token)
        {
            if(WaitForCancellation)await Task.Delay(Timeout.Infinite,token);
            token.ThrowIfCancellationRequested();return new PortalStatus{State=PortalState.Online};
        }
        public Task<LoginResult> LoginAsync(Credentials credentials,CancellationToken token)
        {throw new Exception("Offline UI tests must not authenticate.");}
        public Task<PortalStatus> LogoutAsync(Credentials credentials,CancellationToken token)
        {throw new Exception("Offline UI tests must not log out.");}
        public void Dispose(){Disposed=true;}
    }
}
