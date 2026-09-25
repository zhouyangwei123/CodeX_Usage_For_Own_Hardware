using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Protocol;
using CodexToolsHost.Tests;
using CodexToolsHost.UI;

internal static class Baseline
{
    [STAThread] private static int Main(string[] args)
    {
        string output = args[0];
        AppConfig config = AppConfig.Load(output, output);
        var source = new FakeCodexSource();
        var api = new FakeDeepSeekSource();
        ConstructorInfo ctor = typeof(BridgeService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new Type[] { typeof(AppConfig), typeof(IDeviceLink),
                typeof(CodexToolsHost.Quota.ICodexStatusSource),
                typeof(CodexToolsHost.Quota.DesktopLogStatusMonitor),
                typeof(CodexToolsHost.Quota.IDeepSeekSource) }, null);
        using (var bridge = (BridgeService)ctor.Invoke(new object[] {
            config, new MockDeviceLink { EmitInfoOnConnect = false }, source, null, api }))
        using (var form = new QuotaHudForm(config, source, api, delegate { }, bridge))
        {
            int[] scales = {60,75,90,100};
            int?[] values = {null,0,1,50,100};
            foreach (int scale in scales)
            foreach (int? value in values)
            {
                config.QuotaHudScalePercent = scale;
                form.ApplyDisplaySettings();
                form.Location = new Point(-20000,-20000);
                typeof(QuotaHudForm).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(form, new QuotaSnapshot(value,value,null,null,DateTimeOffset.UtcNow,!value.HasValue));
                using (var bitmap = new Bitmap(form.Width,form.Height,PixelFormat.Format32bppArgb))
                {
                    form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));
                    int fractional = 0;
                    // HWND Region is a binary membership mask. The solid window background is
                    // present even where the renderer's own antialiased panel has partial coverage.
                    for (int y=0;y<bitmap.Height;y++) for(int x=0;x<bitmap.Width;x++)
                    {
                        Color pixel = bitmap.GetPixel(x,y);
                        byte alpha = form.Region.IsVisible(x,y) ? (byte)255 : (byte)0;
                        bitmap.SetPixel(x,y,Color.FromArgb(alpha,pixel.R,pixel.G,pixel.B));
                        if(alpha>0 && alpha<255) fractional++;
                    }
                    bitmap.Save(Path.Combine(output,"before-"+scale+"-"+(value.HasValue?value.Value.ToString():"empty")+".png"));
                    Console.WriteLine("baseline scale={0} quota={1} fractionalAlpha={2}",scale,value,fractional);
                    if(fractional!=0) return 1;
                }
            }
            form.BeginShutdown();
        }
        return 0;
    }
}
