using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexToolsHost.Model;
using CodexToolsHost.UI;

internal static class Runner
{
    private static int _checks;
    private static MethodInfo _render;
    [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);

    private static void Check(bool condition, string name)
    {
        if(!condition) throw new Exception(name);
        _checks++;
    }
    private static Bitmap Frame(QuotaHudRenderer renderer, int scale, int? quota, string style, bool opaque, bool refresh, bool knownWindows = false)
    {
        return (Bitmap)_render.Invoke(renderer,new object[] {
            QuotaHudPresentation.ScaleSize(new Size(320,138),scale),
            new QuotaSnapshot(quota,quota,null,null,DateTimeOffset.UtcNow,!quota.HasValue,
                knownWindows ? (int?)300 : null, knownWindows ? (int?)10080 : null, ""),
            style,45f,refresh,"GO 5h余100%  7d余60%\r\n   月余42%  5h→02:12:34","↑128K ↓1.2M",false,true,true,opaque});
    }
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            string output=args[0]; Directory.CreateDirectory(output);
            _render=typeof(QuotaHudRenderer).GetMethod("RenderFrame");
            Check(_render!=null,"Renderer must provide a premultiplied alpha frame, not a binary window Region");
            using(var renderer=new QuotaHudRenderer())
            {
                using(var unknown=Frame(renderer,100,50,"glass",false,false))
                using(var known=Frame(renderer,100,50,"glass",false,false,true))
                {
                    bool different=false;
                    for(int y=8;y<26;y++)for(int x=15;x<150;x++)
                        if(unknown.GetPixel(x,y)!=known.GetPixel(x,y))different=true;
                    Check(different,"unknown window duration must not be fabricated as 5H / 7D");
                }
                foreach(string style in new[]{"glass","minimal","classic"})
                foreach(int scale in new[]{60,75,90,100})
                foreach(int? quota in new int?[]{null,0,1,50,100,-1,101})
                using(var bitmap=Frame(renderer,scale,quota,style,false,false))
                {
                    Check(bitmap.PixelFormat==PixelFormat.Format32bppPArgb,"premultiplied format");
                    Check(bitmap.GetPixel(0,0).A==0,"transparent outer corner");
                    int fractional=0;
                    for(int y=0;y<Math.Min(16,bitmap.Height);y++)
                    for(int x=0;x<Math.Min(18,bitmap.Width);x++)
                    { byte a=bitmap.GetPixel(x,y).A; if(a>0&&a<255)fractional++; }
                    Check(fractional>=3,"fractional rounded perimeter "+style+" "+scale);
                    AssertPremultiplied(bitmap);
                    if(!quota.HasValue||quota.Value>=0&&quota.Value<=100)
                        bitmap.Save(Path.Combine(output,style+"-"+scale+"-"+(quota.HasValue?quota.Value.ToString():"empty")+".png"));
                    var layout=renderer.CalculateLayout(new Rectangle(0,0,320,78),
                        new QuotaSnapshot(quota,quota,null,null,DateTimeOffset.UtcNow,false));
                    int percent=quota.HasValue?Math.Max(0,Math.Min(100,quota.Value)):0;
                    Check(layout.PrimaryFill.Width==layout.PrimaryTrack.Width*percent/100,"proportionate fill");
                }
                using(var glass=Frame(renderer,100,50,"glass",false,false))
                using(var minimal=Frame(renderer,100,50,"minimal",false,false))
                using(var classic=Frame(renderer,100,50,"classic",false,false))
                using(var fallback=Frame(renderer,100,50,"glass",true,false))
                {
                    Check(glass.GetPixel(160,80).ToArgb()!=minimal.GetPixel(160,80).ToArgb(),"glass and minimal differentiated");
                    Check(classic.GetPixel(25,30).ToArgb()!=glass.GetPixel(25,30).ToArgb(),"classic recognized by treatment");
                    for(int y=0;y<fallback.Height;y++)for(int x=0;x<fallback.Width;x++)
                        Check(fallback.GetPixel(x,y).A==255,"opaque fallback remains readable");
                    fallback.Save(Path.Combine(output,"fallback.png"));
                    NativeUploads(glass);
                }
            }
            Console.WriteLine("PASS HUD render/native assertions="+_checks);return 0;
        }
        catch(Exception e){Console.Error.WriteLine("FAIL "+e);return 1;}
    }
    private static void AssertPremultiplied(Bitmap bitmap)
    {
        var data=bitmap.LockBits(new Rectangle(Point.Empty,bitmap.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
        try
        {
            byte[] row=new byte[bitmap.Width*4];
            for(int y=0;y<bitmap.Height;y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,row.Length);
                for(int x=0;x<row.Length;x+=4)
                    Check(row[x]<=row[x+3]&&row[x+1]<=row[x+3]&&row[x+2]<=row[x+3],"RGB must be premultiplied by alpha");
            }
        }
        finally{bitmap.UnlockBits(data);}
    }
    private static void NativeUploads(Bitmap bitmap)
    {
        Type surface=typeof(QuotaHudRenderer).Assembly.GetType("CodexToolsHost.UI.LayeredWindowSurface");
        Check(surface!=null,"native alpha surface exists");
        MethodInfo present=surface.GetMethod("TryPresent");
        Check(present!=null,"native alpha upload available");
        int before;
        using(var window=new HiddenLayeredWindow())
        {
            IntPtr handle=window.Handle;
            Check((bool)present.Invoke(null,new object[]{handle,new Point(-20000,-20000),bitmap,(byte)255}),"first offscreen upload");
            // WinForms initializes process-wide font/window resources on its first handle.
            // Measure steady state after that one-time allocation, then exercise 500 uploads.
            before=GetGuiResources(Process.GetCurrentProcess().Handle,0);
            for(int i=0;i<500;i++)
                Check((bool)present.Invoke(null,new object[]{handle,new Point(-20000,-20000),bitmap,(byte)(i%2==0?255:89)}),"offscreen native upload");
            int after=GetGuiResources(Process.GetCurrentProcess().Handle,0);
            Check(after-before<=4,"GDI handles stable after 500 uploads: "+before+" -> "+after);
            Check(!(bool)present.Invoke(null,new object[]{IntPtr.Zero,Point.Empty,bitmap,(byte)255}),"invalid HWND failure reported");
            for(int i=0;i<50;i++)
                Check(!(bool)present.Invoke(null,new object[]{new IntPtr(1),Point.Empty,bitmap,(byte)255}),"native failure path releases DIB/DC");
            Check(GetGuiResources(Process.GetCurrentProcess().Handle,0)==after,"native failure GDI resources stable");
            Console.WriteLine("GDI resources baseline="+before+" after500="+after);
        }
        GC.Collect();GC.WaitForPendingFinalizers();
        int final=GetGuiResources(Process.GetCurrentProcess().Handle,0);
        Check(final-before<=1,"native resources released after dispose");
        Console.WriteLine("GDI resources afterDispose="+final);
    }
    private sealed class HiddenLayeredWindow:Form
    {
        public HiddenLayeredWindow(){ShowInTaskbar=false;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;Location=new Point(-20000,-20000);}
        protected override CreateParams CreateParams {get{var p=base.CreateParams;p.ExStyle|=0x80000|0x8000000|0x80;return p;}}
    }
}
