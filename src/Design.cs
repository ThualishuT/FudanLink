using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FudanNetLite
{
    internal static class Theme
    {
        internal static readonly Color Page = Color.FromArgb(243, 247, 249), Ink = Color.FromArgb(24, 43, 56),
            Muted = Color.FromArgb(119, 137, 148), Line = Color.FromArgb(226, 235, 240), Accent = Color.FromArgb(20, 137, 119);
        internal static Font Font(float points, FontStyle style = FontStyle.Regular) { return new Font("Microsoft YaHei UI", points, style, GraphicsUnit.Point); }
        internal static Color Canvas(Control control)
        {
            while(control!=null){if(control.BackColor.A==255)return control.BackColor;control=control.Parent;}
            return Page;
        }
        internal static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height)); var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90); path.AddArc(r.Right-d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right-d, r.Bottom-d, d, d, 0, 90); path.AddArc(r.X, r.Bottom-d, d, d, 90, 90); path.CloseFigure(); return path;
        }
    }
    internal static class Brand
    {
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        internal static void Draw(Graphics g, RectangleF r, Color? status)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(r, r.Width * .25F))
            using (var brush = new SolidBrush(Theme.Ink)) g.FillPath(brush, path);
            var state = g.Save();
            g.TranslateTransform(r.X + r.Width/2, r.Y+r.Height/2); g.RotateTransform(-35);
            float u = r.Width / 64F;
            using(var pen = new Pen(Color.White, 5*u) { StartCap=LineCap.Round, EndCap=LineCap.Round })
            {
                g.DrawArc(pen, -20*u, -10*u, 27*u, 20*u, 55, 250);
                g.DrawArc(pen, -7*u, -10*u, 27*u, 20*u, 235, 250);
                g.DrawLine(pen,-6*u,0,6*u,0);
            }
            g.Restore(state);
            if (status.HasValue)
            {
                var dot=new RectangleF(r.Right-r.Width*.32F,r.Bottom-r.Width*.32F,r.Width*.28F,r.Width*.28F);
                using(var fill=new SolidBrush(status.Value))g.FillEllipse(fill,dot);
                using(var border=new Pen(Color.White,r.Width*.045F))g.DrawEllipse(border,dot);
            }
        }
        internal static Bitmap Bitmap(int size, Color? status)
        {
            var image = new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(image)) { g.Clear(Color.Transparent); Draw(g,new RectangleF(size*.04F,size*.04F,size*.92F,size*.92F),status); }
            return image;
        }
        internal static Icon CreateIcon(Color color)
        {
            using(var bitmap=Bitmap(Math.Max(32,SystemInformation.SmallIconSize.Width*2),color))
            {
                var handle=bitmap.GetHicon();
                try { using(var borrowed=Icon.FromHandle(handle))return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }
    internal sealed class BrandView : Control
    {
        internal BrandView() { SetStyle(ControlStyles.AllPaintingInWmPaint|ControlStyles.UserPaint|ControlStyles.OptimizedDoubleBuffer,true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Canvas(Parent));float size=Math.Min(Width,Height); Brand.Draw(e.Graphics,new RectangleF((Width-size)/2,(Height-size)/2,size,size),null);
        }
    }
    internal sealed class Card : Panel
    {
        internal Card() { BackColor=Color.White; DoubleBuffered=true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Canvas(Parent)); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var r=new RectangleF(.5F,.5F,Width-1,Height-1);
            using(var path=Theme.Round(r,22F*DeviceDpi/96F))
            using(var brush=new SolidBrush(BackColor))
            using(var pen=new Pen(Theme.Line)) { e.Graphics.FillPath(brush,path); e.Graphics.DrawPath(pen,path); }
        }
    }
    internal sealed class ActionButton : Button
    {
        internal bool Primary;
        private bool hover;
        internal ActionButton()
        {
            FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; Cursor=Cursors.Hand; Font=Theme.Font(10F,FontStyle.Bold);
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
        }
        public override Size GetPreferredSize(Size proposedSize)
        { var text=TextRenderer.MeasureText(Text,Font);return new Size(text.Width+(int)(28*DeviceDpi/96F),Math.Max(text.Height+(int)(22*DeviceDpi/96F),(int)(44*DeviceDpi/96F))); }
        protected override void OnMouseEnter(EventArgs e) { hover=true;Invalidate();base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover=false;Invalidate();base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Canvas(Parent)); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            Color fill=Primary ? (hover?Color.FromArgb(18,111,102):Theme.Accent) : (hover?Color.FromArgb(233,239,245):Color.White);
            if(!Enabled)fill=Color.FromArgb(225,233,237);
            using(var path=Theme.Round(new RectangleF(.5F,.5F,Width-1,Height-1),11F*DeviceDpi/96F))
            using(var brush=new SolidBrush(fill))
            using(var pen=new Pen(Primary?fill:Theme.Line)){e.Graphics.FillPath(brush,path);e.Graphics.DrawPath(pen,path);}
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,!Enabled?Theme.Muted:(Primary?Color.White:Theme.Ink),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
            if(Focused && ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-5,-5),Theme.Ink,fill);
        }
    }
    internal sealed class Switch : CheckBox
    {
        internal Switch() { AutoSize=false; Cursor=Cursors.Hand; ForeColor=Theme.Ink; Font=Theme.Font(10F); SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true); }
        public override Size GetPreferredSize(Size proposedSize)
        { var text=TextRenderer.MeasureText(Text,Font);return new Size(text.Width+(int)(64*DeviceDpi/96F),Math.Max(text.Height+(int)(20*DeviceDpi/96F),(int)(48*DeviceDpi/96F))); }
        protected override void OnCheckedChanged(EventArgs e) { Invalidate();base.OnCheckedChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Canvas(this)); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float scale=DeviceDpi/96F, w=38*scale,h=22*scale,x=Width-w-1,y=(Height-h)/2;
            using(var path=Theme.Round(new RectangleF(x,y,w,h),h/2))
            using(var brush=new SolidBrush(Checked?Theme.Accent:Color.FromArgb(202,212,222)))e.Graphics.FillPath(brush,path);
            float d=h-6*scale;
            using(var brush=new SolidBrush(Color.White))e.Graphics.FillEllipse(brush,Checked?x+w-d-3*scale:x+3*scale,y+3*scale,d,d);
            TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(0,0,(int)x-10,Height),ForeColor,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
            if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-2,-2));
        }
    }
    internal sealed class StatusBadge : Label
    {
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Canvas(Parent));e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            using(var path=Theme.Round(new RectangleF(0,0,Width,Height),Height/2F))
            using(var brush=new SolidBrush(BackColor))e.Graphics.FillPath(brush,path);
        }
    }
    internal sealed class StatusArtwork : Button
    {
        internal Color Accent=Theme.Accent;
        private bool hover;
        internal StatusArtwork()
        {
            FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;TabStop=true;Cursor=Cursors.Hand;
            AccessibleRole=AccessibleRole.PushButton;AccessibleDescription="点击连接或断开校园网";
            SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
        }
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);if(Width<=0 || Height<=0)return;
            float d=Math.Min(Width,Height)*.92F;
            using(var shape=new GraphicsPath())
            {shape.AddEllipse((Width-d)/2,(Height-d)/2,d,d);var previous=Region;Region=new Region(shape);if(previous!=null)previous.Dispose();}
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            float d=Math.Min(Width,Height)*.76F,x=(Width-d)/2,y=(Height-d)/2;
            Color tone=Enabled?Accent:Theme.Muted;
            using(var b=new SolidBrush(Color.FromArgb(hover?26:13,tone)))e.Graphics.FillEllipse(b,x-d*.09F,y-d*.09F,d*1.18F,d*1.18F);
            using(var b=new LinearGradientBrush(new RectangleF(x,y,d,d),Color.FromArgb(hover?240:247,252,251),Color.FromArgb(228,244,240),90F))e.Graphics.FillEllipse(b,x,y,d,d);
            using(var p=new Pen(Color.FromArgb(48,tone),Math.Max(1,d*.012F)))e.Graphics.DrawEllipse(p,x,y,d,d);
            if(Focused && ShowFocusCues)using(var p=new Pen(tone,Math.Max(1,d*.014F)){DashStyle=DashStyle.Dot})e.Graphics.DrawEllipse(p,x-d*.06F,y-d*.06F,d*1.12F,d*1.12F);
            var state=e.Graphics.Save(); e.Graphics.TranslateTransform(Width/2F,Height/2F);
            float u=d/100F;
            using(var p=new Pen(tone,4.5F*u){StartCap=LineCap.Round,EndCap=LineCap.Round})
            {
                e.Graphics.DrawArc(p,-19*u,-19*u,38*u,38*u,-45,270);
                e.Graphics.DrawLine(p,0,-25*u,0,-3*u);
            }
            e.Graphics.Restore(state);
        }
    }
}
