using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FudanNetLite
{
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly Control dispatcher = new Control();
        private readonly List<EventWaitHandle> signals = new List<EventWaitHandle>();
        private readonly List<RegisteredWaitHandle> waits = new List<RegisteredWaitHandle>();
        private readonly SemaphoreSlim transition = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Func<bool,IPortalSession> createPortal;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly ToolStripMenuItem stateItem, toggleItem;
        private Icon trayImage;
        private MainForm window;
        private Keeper keeper;
        private IPortalSession portal;
        private CancellationTokenSource cancellation;
        private Task workerTask;
        private bool exiting, disposed;
        private int generation;
        private bool connectionBusy;
        private string category = "worker.started";
        internal event Action Changed;

        internal TrayContext() : this(keepalive => new SrunPortal(new CampusTransport(), keepalive ? new PortalHeartbeat() : null)) { }
        internal TrayContext(Func<bool,IPortalSession> createPortal)
        {
            if(createPortal==null)throw new ArgumentNullException("createPortal");
            this.createPortal=createPortal;
            var handle = dispatcher.Handle;
            menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Microsoft YaHei UI", 9F) };
            stateItem = new ToolStripMenuItem("FudanLink") { Enabled = false }; menu.Items.Add(stateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("打开面板", null, delegate { OpenWindow(false); });
            menu.Items.Add("设置", null, delegate { OpenWindow(true); });
            toggleItem = new ToolStripMenuItem("暂停自动连接");
            toggleItem.Click += async delegate
            {
                try { var settings = Store.LoadSettings(); settings.Enabled = !settings.Enabled;
                    if(settings.Enabled)settings.Disconnected=false;
                    Store.SaveSettings(settings); await ReconfigureAsync(); }
                catch { Store.Log("settings.error"); }
            }; menu.Items.Add(toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { RequestShutdown(); });
            trayImage = Brand.CreateIcon(Color.FromArgb(31, 126, 119));
            tray = new NotifyIcon { Icon = trayImage, Text = "FudanLink · 正在检查", ContextMenuStrip = menu, Visible = true };
            tray.MouseDoubleClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) OpenWindow(false); };
            AddSignal("Stop", delegate { RequestShutdown(); });
            AddSignal("Show", delegate { OpenWindow(false); });
            AddSignal("Settings", delegate { OpenWindow(true); });
            AddSignal("Reload", async delegate { try { await ReconfigureAsync(); } catch { Store.Log("worker.error"); } });
            NetworkChange.NetworkAddressChanged += OnNetworkChanged;
            SystemEvents.PowerModeChanged += OnPowerChanged;
            StartKeeper(); UpdatePresentation();
        }
        private void AddSignal(string purpose, Action action)
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, Program.Name(purpose)); signals.Add(handle);
            waits.Add(ThreadPool.RegisterWaitForSingleObject(handle, delegate { Post(action); }, null, Timeout.Infinite, false));
        }
        private void Post(Action action)
        {
            if (disposed || exiting || dispatcher.IsDisposed) return;
            try { dispatcher.BeginInvoke((MethodInvoker)delegate { if (!disposed && !exiting) action(); }); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
        private void OnNetworkChanged(object sender, EventArgs e) { var current = keeper; if (current != null) current.ForceCheck(); }
        private void OnPowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) OnNetworkChanged(sender, EventArgs.Empty); }
        private void StartKeeper()
        {
            if (exiting || Store.LoadSettings().Disconnected) { category = "connection.paused"; return; }
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); portal = createPortal(true); var settings = Store.LoadSettings();
            int version=++generation;
            keeper = new Keeper(portal, settings.Enabled?Store.LoadCredentials():new Credentials(),delegate(string value){Log(value,version);},
                options: new KeeperOptions { CheckInterval = TimeSpan.FromSeconds(Math.Max(30, Math.Min(300, settings.IntervalSeconds))) });
            category = "worker.started"; Store.Log("worker.started"); workerTask = keeper.RunAsync(cancellation.Token);
            workerTask.ContinueWith(delegate(Task task)
            { if (task.IsFaulted) { var observed = task.Exception; Log("worker.error",version); } }, TaskScheduler.Default);
        }
        private async Task StopKeeperAsync()
        {
            var current = keeper; keeper = null; if (current == null) return;
            ++generation;
            cancellation.Cancel();
            try { if (workerTask != null) await workerTask; } catch { Store.Log("worker.stop_error"); }
            current.Dispose(); portal.Dispose(); cancellation.Dispose();
            portal = null; cancellation = null; workerTask = null; category = "worker.stopped"; Store.Log("worker.stopped");
        }
        internal async Task ReconfigureAsync()
        {
            await transition.WaitAsync();
            try { if (exiting) return; await StopKeeperAsync(); StartKeeper(); UpdatePresentation(); }
            finally { transition.Release(); }
        }
        private void Log(string value,int version)
        {
            Store.Log(value);
            if (value.StartsWith("state.", StringComparison.Ordinal) || value.StartsWith("login.", StringComparison.Ordinal) || value.StartsWith("worker.", StringComparison.Ordinal))
                Post(delegate { if(version==generation){category = value; UpdatePresentation();} });
        }
        internal Presentation Snapshot
        {
            get
            {
                var settings=Store.LoadSettings();var state=Presentation.From(category,settings.Enabled,settings.Disconnected);
                if(category=="state.online")state.Detail=(settings.Connection=="wifi"?"WLAN":"有线网络")+" · 点击图标断开";
                return state;
            }
        }
        private Color lastColor;
        private void UpdatePresentation()
        {
            if (disposed || exiting) return; var state = Snapshot;
            tray.Text = "FudanLink · " + state.Title; stateItem.Text = state.Title;
            toggleItem.Text = Store.LoadSettings().Enabled ? "暂停自动连接" : "启用自动连接";
            if (trayImage == null || !state.Color.Equals(lastColor))
            {
                var image = Brand.CreateIcon(state.Color); tray.Icon = image;
                var old = trayImage; trayImage = image; lastColor = state.Color; if (old != null) old.Dispose();
            }
            var handler = Changed; if (handler != null) handler();
        }
        internal void OpenWindow(bool settings)
        {
            if (exiting) return;
            if (window == null || window.IsDisposed) window = new MainForm(this);
            window.ShowPage(settings); window.Show();
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            window.Activate(); window.BringToFront();
        }
        internal async Task<PortalStatus> CheckAsync()
        {
            await transition.WaitAsync();
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                using (var probe = createPortal(false))
                {
                    var result = await probe.CheckAsync(lifetime.Token);
                    category = "state." + result.State.ToString().ToLowerInvariant(); UpdatePresentation();
                    if (keeper != null) keeper.ForceCheck(); return result;
                }
            }
            finally{transition.Release();}
        }
        internal bool ConnectionBusy { get { return connectionBusy; } }
        internal async Task SetConnectedAsync(bool connected)
        {
            if(connectionBusy || exiting)return;
            connectionBusy=true;await transition.WaitAsync();
            try
            {
                if(exiting)return;
                await StopKeeperAsync();
                var settings=Store.LoadSettings();settings.Disconnected=true;Store.SaveSettings(settings);
                category=connected?"action.connecting":"action.disconnecting";UpdatePresentation();
                using(var operation=createPortal(true))
                {
                    if(!connected)
                    {
                        var result=await operation.LogoutAsync(Store.LoadCredentials(),lifetime.Token);
                        category=result.State==PortalState.Offline?"state.disconnected":"logout.unconfirmed";
                        Store.Log(category);
                    }
                    else
                    {
                        var status=await operation.CheckAsync(lifetime.Token);
                        if(status.State!=PortalState.Online)
                        {
                            if(status.State!=PortalState.Offline)throw new InvalidOperationException("Gateway unavailable.");
                            var login=await operation.LoginAsync(Store.LoadCredentials(),lifetime.Token);
                            if(!login.Success){category=login.PermanentFailure?"login.blocked":"login.transient_failure";Store.Log(category);return;}
                            Store.Log("login.accepted");status=await operation.CheckAsync(lifetime.Token);
                        }
                        if(status.State!=PortalState.Online)throw new InvalidOperationException("Connection unconfirmed.");
                        settings.Disconnected=false;Store.SaveSettings(settings);StartKeeper();category="state.online";
                    }
                }
            }
            catch{category=connected?"connect.unconfirmed":"logout.unconfirmed";Store.Log(category);}
            finally{connectionBusy=false;transition.Release();UpdatePresentation();}
        }
        internal void RequestShutdown() { if (!exiting) ShutdownAsync(); }
        private async void ShutdownAsync()
        {
            if (exiting) return; exiting = true; lifetime.Cancel(); tray.Visible = false;
            await transition.WaitAsync();
            try { await StopKeeperAsync(); } finally { transition.Release(); }
            if (window != null && !window.IsDisposed) { window.AllowExit = true; window.Close(); }
            ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposed) return; disposed = true;
            if (disposing)
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkChanged; SystemEvents.PowerModeChanged -= OnPowerChanged;
                foreach (var wait in waits) wait.Unregister(null); foreach (var signal in signals) signal.Dispose();
                lifetime.Dispose(); tray.Visible = false; tray.Dispose(); menu.Dispose();
                if (trayImage != null) trayImage.Dispose(); dispatcher.Dispose(); transition.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
