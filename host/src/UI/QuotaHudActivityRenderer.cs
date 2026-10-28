using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using CodexToolsHost.Model;
using CodexToolsHost.Usage;
using CodexToolsHost.Protocol;

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
        { return RenderUsageFrame(size, quota, style, angle, refreshing, api, network, apiStale, apiAvailable, apiIsGo, opaque, activity, expanded, null, null); }
        internal Bitmap RenderUsageFrame(Size size, QuotaSnapshot quota, string style,
            float angle, bool refreshing, string api, string network, bool apiStale,
            bool apiAvailable, bool apiIsGo, bool opaque, UsageActivitySnapshot activity, bool expanded,
            OledUsageFrame usageFrame = null, AccountUsageSnapshot account = null)
        {
            if(size.Width<=0||size.Height<=0)throw new ArgumentOutOfRangeException("size");
            if (usageFrame == null) usageFrame = new OledUsageProjector().Create(false, quota, activity, null, 0, false, false, false, DateTimeOffset.UtcNow);
            if (style == "pixel") return OledPixelRenderer.RenderLive(size, usageFrame, null, expanded, activity == null ? null : activity.TokenSpeed);
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
                    bool accountView = usageFrame.View == 1;
                    bool measured=usageFrame.Has(OledUsageFrame.UsageKnown)&&!usageFrame.Has(OledUsageFrame.UsageStale);
                    string qualifier=usageFrame.Has(OledUsageFrame.Partial)?"≥":"";
                    string hour=accountView ? account != null && account.LifetimeTokens.HasValue ? FormatTokens(account.LifetimeTokens.Value) : "--"
                        : measured&&activity!=null?qualifier+FormatTokens(activity.HourTokens):"--";
                    Text(g,(accountView?account != null && account.IsStale?"账号累计(过期) · ":"账号累计 · ":"本机近 1 小时 · ")+hour+" tokens",label,muted,new RectangleF(18,82,247,24),StringAlignment.Near);
                    var tokenSpeed = activity == null ? null : activity.TokenSpeed;
                    string status=accountView ? !measured ? "日汇总不可用" : "截至 "+usageFrame.Month.ToString("00")+"-"+usageFrame.Day.ToString("00")
                        : tokenSpeed == null || !tokenSpeed.IsReady ? "测速准备中" : "并行 " + tokenSpeed.ActiveSessions + " · 待测 " + tokenSpeed.WaitingSessions;
                    Text(g,status,label,muted,new RectangleF(268,82,103,24),StringAlignment.Far);
                    DrawChartToggle(g,expanded);
                    if(expanded)DrawUsageChart(g,usageFrame,label,muted,style);
                    int footer=expanded?211:111;
                    g.DrawLine(separator,18,footer,402,footer);
                    g.DrawLine(separator,151,footer+12,151,footer+74);
                    g.DrawLine(separator,280,footer+12,280,footer+74);
                    Text(g,apiIsGo?"API / GO":"API",label,muted,new RectangleF(18,footer+5,126,24),StringAlignment.Near);
                    Text(g,accountView?"TOKEN /day":"输出 /s",label,muted,new RectangleF(162,footer+5,112,24),StringAlignment.Near);
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
                    Text(g,accountView ? "日用量 "+(measured?OledUsageFrame.FormatCount(usageFrame.Current):"--")
                        : (tokenSpeed == null ? "--/s" : tokenSpeed.CompactText()),value,ink,new RectangleF(162,footer+29,113,23),StringAlignment.Near);
                    Text(g,accountView ? account != null && account.ObservedAt != DateTimeOffset.MinValue ? "查询 "+account.ObservedAt.ToLocalTime().ToString("HH:mm") : "等待账号统计"
                        : "↑"+(measured?OledUsageFrame.FormatCount(usageFrame.Input,true):"--")+" ↓"+(measured?OledUsageFrame.FormatCount(usageFrame.Output,true):"--")+"/m",value,ink,new RectangleF(162,footer+51,113,23),StringAlignment.Near);
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
            int? remaining=quota.PrimaryWindowMinutes==10080?quota.PrimaryRemainingPercent:quota.SecondaryWindowMinutes==10080?quota.SecondaryRemainingPercent:null;
            DateTimeOffset? reset=quota.PrimaryWindowMinutes==10080?quota.PrimaryResetsAt:quota.SecondaryWindowMinutes==10080?quota.SecondaryResetsAt:null;
            Rectangle track=new Rectangle(18,37,338,8);
            Color color=style=="classic"?Color.FromArgb(247,68,99):Color.FromArgb(89,224,210);
            if(remaining.HasValue&&remaining.Value<20)color=Color.FromArgb(255,153,112);
            Text(g,"CODEX / 7D",label,muted,new RectangleF(18,8,240,24),StringAlignment.Near);
            Text(g,QuotaHudPresentation.FormatPercent(quota.IsStale?null:remaining),value,ink,new RectangleF(280,8,76,24),StringAlignment.Far);
            using(var trough=new SolidBrush(Color.FromArgb(150,5,10,19)))FillRounded(g,trough,track,4f);
            using(var fill=new SolidBrush(color))FillRounded(g,fill,FillRectangle(track,quota.IsStale?null:remaining),4f);
            Text(g,quota.IsStale?"额度数据过期":"重置 "+QuotaHudPresentation.FormatResetCountdown(reset,DateTimeOffset.UtcNow),label,muted,new RectangleF(18,49,338,23),StringAlignment.Near);
            DrawRefresh(g,RefreshHitBounds(new Rectangle(0,0,420,78)),angle,refreshing,quota.IsStale);
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
        private static void DrawUsageChart(Graphics g,OledUsageFrame frame,Font label,Brush muted,string style)
        {
            RectangleF plot=new RectangleF(66,119,334,59);
            bool known=frame.Has(OledUsageFrame.UsageKnown)&&!frame.Has(OledUsageFrame.UsageStale);
            Color color=style=="classic"?Color.FromArgb(116,174,255):Color.FromArgb(100,214,209);
            using(var pen=new Pen(color,1.5f))
            using(var fill=new SolidBrush(Color.FromArgb(22,color)))
            using(var grid=new Pen(Color.FromArgb(45,155,183,211)))
            {
                if(known)
                {
                    string[] ticks={OledUsageFrame.FormatCount(frame.Upper),frame.Upper==1?"0.5":frame.Upper==5?"2.5":OledUsageFrame.FormatCount(frame.Upper/2),"0"};
                    for(int i=0;i<3;i++)
                    {
                        float y=plot.Top+i*plot.Height/2;
                        g.DrawLine(grid,plot.Left,y,plot.Right,y);
                        Text(g,ticks[i],label,muted,new RectangleF(18,Math.Min(plot.Bottom-12,y-8),42,16),StringAlignment.Far);
                    }
                    var points=new List<PointF>();
                    for(int i=0;i<frame.Points.Length;i++)
                    {
                        if(frame.Points[i]==255){DrawChartSegment(g,points,plot.Bottom,pen,fill);points.Clear();continue;}
                        points.Add(new PointF(plot.Left+i*plot.Width/(frame.Points.Length-1),plot.Bottom-frame.Points[i]*plot.Height/34));
                    }
                    DrawChartSegment(g,points,plot.Bottom,pen,fill);
                    if(points.Count>0){PointF last=points[points.Count-1];using(var dot=new SolidBrush(color))g.FillEllipse(dot,last.X-2,last.Y-2,4,4);}
                }
                else Text(g,frame.View==1?"账号统计不可用或已过期":"本机统计不可用或已过期",label,muted,plot,StringAlignment.Center);
            }
            Text(g,frame.View==1?"最近14日":"−60m",label,muted,new RectangleF(18,183,76,22),StringAlignment.Near);
            Text(g,frame.View==1?"账号汇总 /day":"本机输入+输出 /min",label,muted,new RectangleF(100,183,218,22),StringAlignment.Center);
            Text(g,frame.View==1?frame.Month==0?"--":frame.Month.ToString("00")+"-"+frame.Day.ToString("00"):"现在",label,muted,new RectangleF(326,183,76,22),StringAlignment.Far);
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
