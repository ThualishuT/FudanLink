using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace FudanNetLite
{
    internal sealed class MainForm : Form
    {
        private readonly TrayContext context;
        private readonly Panel home, settings;
        private readonly Label title, subtitle, statusTitle, statusDetail, badge, feedback;
        private readonly StatusArtwork artwork;
        private readonly ActionButton headerAction, primary;
        private readonly TextBox user, password;
        private readonly ComboBox connection;
        private readonly Switch autoConnect, startup;
        private Credentials saved;
        private bool settingsPage, busy;
        internal bool AllowExit;

        internal MainForm(TrayContext context)
        {
            this.context=context;
            AutoScaleDimensions=new SizeF(96F,96F); AutoScaleMode=AutoScaleMode.Dpi;
            Font=Theme.Font(10F); ForeColor=Theme.Ink; BackColor=Theme.Page;
            Text="FudanLink"; ClientSize=new Size(480,590); MinimumSize=new Size(440,540);
            StartPosition=FormStartPosition.CenterScreen; MaximizeBox=false;
            Icon=Brand.CreateIcon(Theme.Accent);
            var root=Grid(1); root.Dock=DockStyle.Fill; root.Padding=new Padding(28,22,28,18);
            root.RowCount=3; root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); Controls.Add(root);
            var header=Grid(3);header.AutoSize=true;header.RowStyles.Add(new RowStyle(SizeType.AutoSize)); header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,50));header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,78));
            header.Controls.Add(new BrandView{Dock=DockStyle.Fill,Margin=new Padding(0,7,12,10),MinimumSize=new Size(32,32)},0,0);
            var heading=Grid(1);heading.AutoSize=true;heading.RowCount=2;heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));heading.RowStyles.Add(new RowStyle(SizeType.AutoSize));heading.Padding=new Padding(0,0,0,8);
            title=Label("FudanLink",19,FontStyle.Bold);subtitle=Label("CAMPUS CONNECTION",7.5F);subtitle.ForeColor=Theme.Muted;
            heading.Controls.Add(title,0,0);heading.Controls.Add(subtitle,0,1);header.Controls.Add(heading,1,0);
            headerAction=new ActionButton{Text="设置",Dock=DockStyle.Top,AutoSize=true,Margin=new Padding(4,6,0,6)};
            headerAction.Click+=delegate{ShowPage(!settingsPage);};header.Controls.Add(headerAction,2,0);root.Controls.Add(header,0,0);
            var pages=new Panel{Dock=DockStyle.Fill,Margin=new Padding(0,9,0,17)};root.Controls.Add(pages,0,1);
            home=new Panel{Dock=DockStyle.Fill,AutoScroll=true};settings=new Panel{Dock=DockStyle.Fill,Visible=false,AutoScroll=true};pages.Controls.Add(home);pages.Controls.Add(settings);
            var card=new Card{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(20,18,20,18)}; home.Controls.Add(card);
            var hero=Grid(1);hero.Dock=DockStyle.Top;hero.AutoSize=true;hero.AutoSizeMode=AutoSizeMode.GrowAndShrink;hero.RowCount=6;
            hero.RowStyles.Add(new RowStyle(SizeType.Absolute,150));hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));hero.RowStyles.Add(new RowStyle(SizeType.Absolute,22));hero.RowStyles.Add(new RowStyle(SizeType.AutoSize));card.Controls.Add(hero);
            artwork=new StatusArtwork{Dock=DockStyle.Fill,BackColor=Color.White,AccessibleName="连接校园网",TabIndex=0};
            artwork.Click+=async delegate{await ToggleConnectionAsync();};hero.Controls.Add(artwork,0,0);
            statusTitle=Label("正在检查连接",19F,FontStyle.Bold);statusTitle.Padding=new Padding(0,6,0,7);statusTitle.TextAlign=ContentAlignment.MiddleCenter;hero.Controls.Add(statusTitle,0,1);
            statusDetail=Label("FudanLink 正在后台运行",9F);statusDetail.Padding=new Padding(0,1,0,9);statusDetail.ForeColor=Theme.Muted;statusDetail.TextAlign=ContentAlignment.MiddleCenter;hero.Controls.Add(statusDetail,0,2);
            var badgeLine=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.LeftToRight,WrapContents=false,Margin=new Padding(0),Padding=new Padding(0,5,0,5)};
            badge=new StatusBadge{Text="检查中",AutoSize=true,MinimumSize=new Size(112,0),Padding=new Padding(14,6,14,6),Font=Theme.Font(8.5F),TextAlign=ContentAlignment.MiddleCenter,BackColor=Color.FromArgb(236,243,246),Margin=new Padding(0)};
            badgeLine.Controls.Add(badge);badgeLine.Resize+=delegate{badgeLine.Padding=new Padding(Math.Max(0,(badgeLine.ClientSize.Width-badge.Width)/2),badgeLine.Padding.Top,0,0);};hero.Controls.Add(badgeLine,0,3);
            var separator=new Panel{Height=1,Dock=DockStyle.Bottom,BackColor=Theme.Line};hero.Controls.Add(separator,0,4);
            autoConnect=new Switch{Text="自动连接",Dock=DockStyle.Fill,AutoSize=true,BackColor=Color.White};
            autoConnect.Checked=Store.LoadSettings().Enabled;
            autoConnect.Click+=async delegate
            {
                if(busy)return;
                try{var value=Store.LoadSettings();value.Enabled=autoConnect.Checked;if(value.Enabled)value.Disconnected=false;Store.SaveSettings(value);if(context!=null)await context.ReconfigureAsync();RefreshStatus();}
                catch{Notify("未能更新设置");}
            };hero.Controls.Add(autoConnect,0,5);
            bool fitting=false;
            Action fitArtwork=delegate
            {
                if(fitting || !home.Visible || home.ClientSize.Height<=0)return;fitting=true;
                try
                {
                    int other=0;var heights=hero.GetRowHeights();for(int i=1;i<heights.Length;i++)other+=heights[i];
                    float scale=DeviceDpi/96F;
                    float available=home.ClientSize.Height-card.Padding.Vertical-other-2;
                    float desired=Math.Min(150*scale,Math.Max(40*scale,available));
                    if(Math.Abs(hero.RowStyles[0].Height-desired)>1)hero.RowStyles[0].Height=desired;
                }
                finally{fitting=false;}
            };
            home.Resize+=delegate{fitArtwork();};hero.Layout+=delegate{fitArtwork();};
            var fields=Grid(1);fields.Dock=DockStyle.Top;fields.AutoSize=true;fields.AutoSizeMode=AutoSizeMode.GrowAndShrink;
            AddRow(fields,Label("账号",9F,FontStyle.Bold),0);
            user=new TextBox{BorderStyle=BorderStyle.None,Dock=DockStyle.Top,Font=Theme.Font(11F),AccessibleName="UIS 账号"};AddRow(fields,Input(user),0);
            AddRow(fields,new Panel(),14);AddRow(fields,Label("密码",9F,FontStyle.Bold),0);
            password=new TextBox{BorderStyle=BorderStyle.None,Dock=DockStyle.Top,Font=Theme.Font(11F),UseSystemPasswordChar=true,AccessibleName="UIS 密码"};AddRow(fields,Input(password),0);
            var hint=Label("留空保留已保存的密码",8F);hint.ForeColor=Theme.Muted;AddRow(fields,hint,0);
            AddRow(fields,new Panel(),10);AddRow(fields,Label("连接方式",9F,FontStyle.Bold),0);
            connection=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top,Font=Theme.Font(10F),AccessibleName="连接方式"};connection.Items.AddRange(new object[]{"有线网络","WLAN"});connection.SelectedIndex=Store.LoadSettings().Connection=="wifi"?1:0;
            AddRow(fields,connection,0);
            startup=new Switch{Text="开机启动",Dock=DockStyle.Fill,AutoSize=true,Checked=Store.StartupEnabled()};AddRow(fields,startup,0);
            var logs=new LinkLabel{Text="查看日志",AutoSize=true,Padding=new Padding(0,10,0,10),Dock=DockStyle.Fill,LinkColor=Theme.Muted,ActiveLinkColor=Theme.Accent,LinkBehavior=LinkBehavior.HoverUnderline,TextAlign=ContentAlignment.MiddleLeft,Font=Theme.Font(9F)};
            logs.LinkClicked+=delegate{Process.Start("explorer.exe",Store.Root);};AddRow(fields,logs,0);
            feedback=Label("",8.5F);feedback.ForeColor=Theme.Accent;AddRow(fields,feedback,0);settings.Controls.Add(fields);
            primary=new ActionButton{Text="刷新状态",Dock=DockStyle.Fill,AutoSize=true,Margin=new Padding(0,0,0,4)};
            primary.Click+=async delegate{if(settingsPage)await SaveAsync();else await CheckAsync();};root.Controls.Add(primary,0,2);
            saved=Store.LoadCredentials();user.Text=saved.UserName;
            if(context!=null)context.Changed+=RefreshStatus;
            FormClosing+=delegate(object sender,FormClosingEventArgs e){if(!AllowExit && e.CloseReason==CloseReason.UserClosing){e.Cancel=true;Hide();}};
            Resize+=delegate{if(WindowState==FormWindowState.Minimized){Hide();WindowState=FormWindowState.Normal;}};
            FormClosed+=delegate{if(context!=null)context.Changed-=RefreshStatus;Icon.Dispose();};
            RefreshStatus();
        }
        private static TableLayoutPanel Grid(int columns)
        {return new TableLayoutPanel{ColumnCount=columns,RowCount=1,Dock=DockStyle.Fill,Margin=new Padding(0),Padding=new Padding(0),BackColor=Color.Transparent};}
        private static Label Label(string text,float size,FontStyle style=FontStyle.Regular)
        {return new Label{Text=text,Font=Theme.Font(size,style),ForeColor=Theme.Ink,Dock=DockStyle.Fill,AutoSize=true,Padding=new Padding(0,4,0,4),TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0),AutoEllipsis=false,BackColor=Color.Transparent};}
        private static Panel Input(TextBox field)
        {var box=new Card{Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(13,12,13,12),Margin=new Padding(0)};box.Controls.Add(field);return box;}
        private static void AddRow(TableLayoutPanel grid,Control control,int height)
        {
            int row=grid.Controls.Count;grid.RowCount=row+1;grid.RowStyles.Add(height>0?new RowStyle(SizeType.Absolute,height):new RowStyle(SizeType.AutoSize));
            control.Margin=new Padding(0);grid.Controls.Add(control,0,row);
            var combo=control as ComboBox;
            if(combo!=null)
            {
                // WinForms reports a short preferred size for DropDownList under
                // some themes. Reserve its actual font-driven native height.
                var style=grid.RowStyles[row];style.SizeType=SizeType.Absolute;
                Action fit=delegate{style.Height=Math.Max(combo.Height,combo.PreferredHeight)+10*combo.DeviceDpi/96F;};
                combo.FontChanged+=delegate{fit();};combo.SizeChanged+=delegate{fit();};fit();
            }
        }
        internal void ShowPage(bool value)
        {
            settingsPage=value;home.Visible=!value;settings.Visible=value;
            headerAction.Text=value?"返回":"设置";primary.Text=value?"保存设置":"刷新状态";primary.Primary=value;primary.Invalidate();
            title.Text=value?"设置":"FudanLink";subtitle.Text=value?"FUDANLINK":"CAMPUS CONNECTION";
            if(value){saved=Store.LoadCredentials();user.Text=saved.UserName;password.Clear();startup.Checked=Store.StartupEnabled();connection.SelectedIndex=Store.LoadSettings().Connection=="wifi"?1:0;feedback.Text="";settings.BringToFront();}else home.BringToFront();
        }
        private void RefreshStatus()
        {
            if(IsDisposed)return;
            var state=context==null?Presentation.From("state.online",true,false):context.Snapshot;
            statusTitle.Text=state.Title;statusDetail.Text=state.Detail;badge.Text=state.Badge;
            badge.ForeColor=state.Color;badge.BackColor=Color.FromArgb(242,247,247);artwork.Accent=state.Color;
            artwork.AccessibleName=state.Online?"断开校园网":"连接校园网";artwork.Enabled=!busy && (context==null || !context.ConnectionBusy);artwork.Invalidate();
            autoConnect.Checked=Store.LoadSettings().Enabled;
        }
        private void Notify(string text){if(!IsDisposed){feedback.Text=text;if(!settingsPage)MessageBox.Show(this,text,"FudanLink",MessageBoxButtons.OK,MessageBoxIcon.Information);}}
        private async Task CheckAsync()
        {
            if(busy || context==null)return;busy=true;primary.Enabled=false;primary.Text="正在检查…";
            try{await context.CheckAsync();}catch{Notify("暂时无法检查连接");}
            finally{busy=false;if(!IsDisposed){primary.Enabled=true;primary.Text=settingsPage?"保存设置":"刷新状态";RefreshStatus();}}
        }
        private async Task SaveAsync()
        {
            if(busy)return;busy=true;primary.Enabled=false;
            try
            {
                if(String.IsNullOrWhiteSpace(user.Text)){Notify("请输入 UIS 账号");return;}
                if(password.TextLength==0 && !String.Equals(user.Text.Trim(),saved.UserName,StringComparison.Ordinal)){Notify("更换账号时请填写对应密码");return;}
                var credentials=new Credentials{UserName=user.Text.Trim(),Password=password.TextLength>0?password.Text:saved.Password};
                if(String.IsNullOrEmpty(credentials.Password)){Notify("请输入 UIS 密码");return;}
                Store.SaveCredentials(credentials);Store.SetStartup(startup.Checked,Program.Executable);saved=credentials;password.Clear();
                var preferences=Store.LoadSettings();preferences.Connection=connection.SelectedIndex==1?"wifi":"ethernet";Store.SaveSettings(preferences);
                if(context!=null)await context.ReconfigureAsync();Notify("已保存");
            }
            catch{Notify("保存未完成，请重试");}finally{busy=false;if(!IsDisposed)primary.Enabled=true;}
        }
        private async Task ToggleConnectionAsync()
        {
            if(busy || context==null)return;busy=true;primary.Enabled=false;autoConnect.Enabled=false;artwork.Enabled=false;
            try{await context.SetConnectedAsync(!context.Snapshot.Online);}
            finally{busy=false;if(!IsDisposed){primary.Enabled=true;autoConnect.Enabled=true;RefreshStatus();}}
        }
    }
}
