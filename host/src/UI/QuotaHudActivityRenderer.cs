using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using CodexToolsHost.Model;
using CodexToolsHost.Usage;

namespace CodexToolsHost.UI
{
    internal sealed partial class QuotaHudRenderer
    {
        internal static Size ActivityWindowSize(bool expanded) { return new Size(420, expanded ? 300 : 200); }
        internal static Rectangle ActivityToggleBounds() { return new Rectangle(377, 81, 28, 28); }
        internal static Rectangle ActivityFooterBounds(bool expanded) { return new Rectangle(14, expanded ? 215 : 115, 392, 76); }
        internal static string FormatTokens(long value)
        {
            if (value >= 1000000000L) return (value / 1000000000d).ToString("0.#", CultureInfo.InvariantCulture) + "B";
            if (value >= 1000000L) return (value / 1000000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000L) return (value / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            return value.ToString(CultureInfo.InvariantCulture);
        }
        internal Bitmap RenderActivityFrame(Size size, QuotaSnapshot quota, string style,
            float angle, bool refreshing, string api, string network, bool apiStale,
            bool apiAvailable, bool apiIsGo, bool opaque, UsageActivitySnapshot activity, bool expanded)
        {
            if(size.Width<=0||size.Height<=0)throw new ArgumentOutOfRangeException("size");
            style=opaque?"minimal":NormalizeStyle(style);
            Size standard=ActivityWindowSize(expanded);
            float scale=size.Width/(float)standard.Width;
            var bitmap=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppPArgb);
            try
            {
                using(var g=Graphics.FromImage(bitmap))
                using(var label=new Font("Segoe UI",Math.Max(11f,10.5f/scale),FontStyle.Regular,GraphicsUnit.Pixel))
                using(var value=new Font("Segoe UI",Math.Max(13f,11f/scale),FontStyle.Regular,GraphicsUnit.Pixel))
                using(var percent=new Font("Segoe UI",Math.Max(14f,12f/scale),FontStyle.Bold,GraphicsUnit.Pixel))
                using(var muted=new SolidBrush(Color.FromArgb(176,194,214)))
                using(var ink=new SolidBrush(Color.FromArgb(241,247,253)))
                using(var separator=new Pen(Color.FromArgb(48,169,196,219)))
                {
                    g.Clear(opaque?Color.FromArgb(15,20,28):Color.Transparent);
                    g.ScaleTransform(scale,size.Height/(float)standard.Height);ConfigureGraphics(g);
                    DrawPanel(g,new RectangleF(1,1,standard.Width-2,standard.Height-2),style,opaque);
                    DrawActivityQuota(g,quota,angle,refreshing,style,label,percent,muted,ink);
                    g.DrawLine(separator,18,79,402,79);
                    bool measured=activity!=null&&activity.IsReady&&!activity.IsStale&&activity.Status!="扫描中";
                    string qualifier=activity!=null&&activity.IsPartial?"≥":"";
                    string hour=measured?qualifier+FormatTokens(activity.HourTokens):"--";
                    Text(g,"近 1 小时 · "+hour+" tokens",label,muted,new RectangleF(18,82,247,24),StringAlignment.Near);
                    string status=activity==null?"扫描中":activity.Status=="正常"?
                        (activity.InputTokensPerMinute+activity.OutputTokensPerMinute==0?"无新记录":"10s/点"):activity.Status;
                    Text(g,status,label,muted,new RectangleF(268,82,103,24),StringAlignment.Far);
                    DrawChartToggle(g,expanded);
                    if(expanded)DrawActivityChart(g,activity,label,muted,style);
                    int footer=expanded?211:111;
                    g.DrawLine(separator,18,footer,402,footer);
                    g.DrawLine(separator,151,footer+12,151,footer+74);
                    g.DrawLine(separator,280,footer+12,280,footer+74);
                    Text(g,apiIsGo?"API / GO":"API",label,muted,new RectangleF(18,footer+5,126,24),StringAlignment.Near);
                    Text(g,"TOKEN /min",label,muted,new RectangleF(162,footer+5,112,24),StringAlignment.Near);
                    Text(g,"NET /s",label,muted,new RectangleF(292,footer+5,111,24),StringAlignment.Near);
                    string[] lines=(api??"--").Replace("\r","").Split('\n');
                    for(int i=0;i<lines.Length;i++)
                    {
                        if(apiIsGo) lines[i]=lines[i].Replace("GO ","").Replace("余","").Replace("5h→","").Replace("  "," ").Trim();
                        else if(i==0)
                        {
                            int separatorAt=lines[i].IndexOf(" · ",StringComparison.Ordinal);
                            if(separatorAt>=0)lines[i]=lines[i].Substring(separatorAt+3);
                        }
                    }
                    Color apiColor=apiStale?Color.FromArgb(166,180,198):apiAvailable?Color.FromArgb(125,231,194):Color.FromArgb(255,171,132);
                    using(var dot=new SolidBrush(apiColor))g.FillEllipse(dot,138,footer+14,4,4);
                    for(int i=0;i<2;i++)Text(g,i<lines.Length?lines[i].Trim():"",value,ink,new RectangleF(18,footer+29+i*22,128,23),StringAlignment.Near);
                    Text(g,"↑入 "+(measured?qualifier+FormatTokens(activity.InputTokensPerMinute):"--"),value,ink,new RectangleF(162,footer+29,113,23),StringAlignment.Near);
                    Text(g,"↓出 "+(measured?qualifier+FormatTokens(activity.OutputTokensPerMinute):"--"),value,ink,new RectangleF(162,footer+51,113,23),StringAlignment.Near);
                    string[] speeds=(network??"↑-- ↓--").Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries);
                    for(int i=0;i<2;i++)Text(g,i<speeds.Length?speeds[i]:"--",value,ink,new RectangleF(292,footer+29+i*22,111,23),StringAlignment.Near);
                }
                return bitmap;
            }
            catch{bitmap.Dispose();throw;}
        }
        private void DrawActivityQuota(Graphics g,QuotaSnapshot quota,float angle,bool refreshing,string style,Font label,Font value,Brush muted,Brush ink)
        {
            if(quota==null)quota=QuotaSnapshot.EmptyStale();
            QuotaHudLayout layout=CalculateLayout(new Rectangle(0,0,420,78),quota);
            Color primary=style=="classic"?Color.FromArgb(247,68,99):Color.FromArgb(89,224,210);
            Color secondary=style=="classic"?Color.FromArgb(78,145,255):Color.FromArgb(155,163,255);
            if(quota.PrimaryRemainingPercent.HasValue&&quota.PrimaryRemainingPercent.Value<20)primary=Color.FromArgb(255,153,112);
            Rectangle[] tracks={layout.PrimaryTrack,layout.SecondaryTrack};Rectangle[] fills={layout.PrimaryFill,layout.SecondaryFill};
            string[] labels={WindowLabel(quota.PrimaryWindowMinutes,"主窗口"),WindowLabel(quota.SecondaryWindowMinutes,"次窗口")};
            string[] percentages={layout.PrimaryText,layout.SecondaryText};Color[] colors={primary,secondary};
            using(var trough=new SolidBrush(Color.FromArgb(150,5,10,19)))for(int i=0;i<2;i++)
            {
                Text(g,labels[i],label,muted,new RectangleF(tracks[i].X,tracks[i].Y-24,tracks[i].Width-72,23),StringAlignment.Near);
                Text(g,percentages[i],value,ink,new RectangleF(tracks[i].Right-65,tracks[i].Y-24,65,23),StringAlignment.Far);
                FillRounded(g,trough,tracks[i],3f);
                using(var fill=new SolidBrush(colors[i]))FillRounded(g,fill,fills[i],3f);
            }
            DrawRefresh(g,layout.RefreshHit,angle,refreshing,quota.IsStale);
        }
        private static void DrawChartToggle(Graphics g,bool expanded)
        {
            Rectangle hit=ActivityToggleBounds();
            using(var fill=new SolidBrush(Color.FromArgb(35,154,187,215)))FillRounded(g,fill,hit,7);
            using(var pen=new Pen(Color.FromArgb(215,232,246),1.7f))
            {
                int x=hit.Left+8,y=hit.Top+10;
                g.DrawLines(pen,expanded?new[]{new Point(x,y+5),new Point(x+6,y),new Point(x+12,y+5)}:
                    new[]{new Point(x,y),new Point(x+6,y+5),new Point(x+12,y)});
            }
        }
        private static void DrawActivityChart(Graphics g,UsageActivitySnapshot activity,Font label,Brush muted,string style)
        {
            RectangleF plot=new RectangleF(20,119,380,59);
            Color color=style=="classic"?Color.FromArgb(116,174,255):Color.FromArgb(100,214,209);
            var points=new List<PointF>();long maximum=1;
            if(activity!=null&&activity.Points!=null)
                foreach(var p in activity.Points)if(p.HasData)maximum=Math.Max(maximum,checked(p.InputTokens+p.OutputTokens));
            double ceiling=maximum*1.12;
            using(var line=new Pen(color,1.5f))
            using(var fill=new SolidBrush(Color.FromArgb(22,color)))
            {
                if(activity!=null&&activity.Points!=null&&activity.Points.Count>0)
                {
                    DateTimeOffset end=activity.Points[activity.Points.Count-1].Time;
                    foreach(var p in activity.Points)
                    {
                        if(!p.HasData){DrawChartSegment(g,points,plot.Bottom,line,fill);points.Clear();continue;}
                        float x=plot.Left+(float)((p.Time-end.AddHours(-1)).TotalSeconds/3600d*plot.Width);
                        float y=plot.Bottom-(float)((p.InputTokens+p.OutputTokens)/ceiling*plot.Height);
                        points.Add(new PointF(Math.Max(plot.Left,Math.Min(plot.Right,x)),Math.Max(plot.Top,Math.Min(plot.Bottom,y))));
                    }
                    DrawChartSegment(g,points,plot.Bottom,line,fill);
                }
            }
            using(var baseline=new Pen(Color.FromArgb(45,155,183,211)))g.DrawLine(baseline,plot.Left,plot.Bottom,plot.Right,plot.Bottom);
            if(activity==null||!activity.IsReady||activity.IsStale||activity.Status=="扫描中")
                Text(g,activity==null?"正在读取本机用量":activity.Status,label,muted,plot,StringAlignment.Center);
            Text(g,"−60m",label,muted,new RectangleF(18,183,76,22),StringAlignment.Near);
            Text(g,"输入+输出 /min",label,muted,new RectangleF(112,183,196,22),StringAlignment.Center);
            Text(g,"现在",label,muted,new RectangleF(326,183,76,22),StringAlignment.Far);
        }
        private static void DrawChartSegment(Graphics g,List<PointF> points,float baseline,Pen pen,Brush fill)
        {
            if(points.Count<2)return;
            PointF[] line=points.ToArray();
            using(var area=new GraphicsPath())
            {
                area.AddLines(line);area.AddLine(line[line.Length-1],new PointF(line[line.Length-1].X,baseline));
                area.AddLine(new PointF(line[line.Length-1].X,baseline),new PointF(line[0].X,baseline));area.CloseFigure();g.FillPath(fill,area);
            }
            g.DrawLines(pen,line);
        }
    }
}
