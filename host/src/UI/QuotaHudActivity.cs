using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using CodexToolsHost.Usage;

namespace CodexToolsHost.UI
{
    public sealed partial class QuotaHudForm
    {
        private readonly LocalUsageService _usage;
        private readonly Timer _activityTimer = new Timer();
        private readonly ToolTip _activityTooltip = new ToolTip { InitialDelay=350, ReshowDelay=100, AutoPopDelay=12000, ShowAlways=true };
        private UsageActivitySnapshot _activitySnapshot;
        private long _activityBucket=long.MinValue;
        private string _chartSaveError;
        public event Action ChartExpansionChanged;

        private Rectangle QuotaBounds() { return new Rectangle(0,0,_usage==null?320:420,78); }
        private void InitializeActivity()
        {
            if(_usage==null)return;
            _activityTimer.Interval=1000;
            _activityTimer.Tick+=delegate{if(!_shutdown&&Visible&&RefreshActivity(false))RequestRender();};
            _usage.Changed+=OnUsageChanged;
            RefreshActivity(true);
        }
        private void OnUsageChanged()
        {
            // Check HWND before InvokeRequired: it reports false on background threads before
            // handle creation. OnVisibleChanged reads the newest report in that case.
            if(_shutdown||IsDisposed||!IsHandleCreated)return;
            RunOnUiThread(delegate{if(!_shutdown&&Visible&&RefreshActivity(false))RequestRender();});
        }
        private bool RefreshActivity(bool force)
        {
            if(_usage==null||_shutdown)return false;
            DateTimeOffset now=DateTimeOffset.UtcNow;
            long bucket=now.UtcTicks/TimeSpan.FromSeconds(10).Ticks;
            if(!force&&bucket==_activityBucket)return false;
            _activityBucket=bucket;
            try{_activitySnapshot=UsageActivity.Create(_usage.Report,now);}
            catch(OverflowException)
            {
                _activitySnapshot=new UsageActivitySnapshot { AsOf=now,IsStale=true,Status="计数异常",
                    Points=new List<UsageActivityPoint>().AsReadOnly() };
            }
            return true;
        }
        public bool ToggleChart()
        {
            if(_usage==null||_shutdown||IsDisposed)return false;
            bool previous=_config.QuotaHudChartExpanded;
            _config.QuotaHudChartExpanded=!previous;
            try{_config.Save();}
            catch(Exception)
            {
                _config.QuotaHudChartExpanded=previous;
                _chartSaveError="折叠状态保存失败；请检查配置目录权限后重试。";
                if(Visible&&IsHandleCreated)_activityTooltip.Show(_chartSaveError,this,ScaleToClient(QuotaHudRenderer.ActivityToggleBounds()).Location,6000);
                return false;
            }
            _chartSaveError=null;
            ApplyDisplaySettings();
            if(ChartExpansionChanged!=null)ChartExpansionChanged();
            return true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if(_usage==null||_shutdown)return;
            string text=null;
            if(ScaleToClient(QuotaHudRenderer.ActivityToggleBounds()).Contains(e.Location))
                text=_chartSaveError??(_config.QuotaHudChartExpanded?"隐藏曲线；保留底部 Token 输入/输出":"展开最近一小时 Token 曲线");
            else if(ScaleToClient(QuotaHudRenderer.ActivityFooterBounds(_config.QuotaHudChartExpanded)).Contains(e.Location))
            {
                text=(_apiDisplayText??"API --")+"\r\n网络 "+(_networkDisplayText??"--")+" /秒\r\n";
                if(_activitySnapshot!=null)
                {
                    bool measured=_activitySnapshot.IsReady&&!_activitySnapshot.IsStale&&_activitySnapshot.Status!="扫描中";
                    string prefix=_activitySnapshot.IsPartial?"≥":"";
                    text+="Token 输入 "+(measured?prefix+_activitySnapshot.InputTokensPerMinute.ToString("N0",CultureInfo.InvariantCulture):"--")
                        +" /分钟，输出 "+(measured?prefix+_activitySnapshot.OutputTokensPerMinute.ToString("N0",CultureInfo.InvariantCulture):"--")+" /分钟\r\n";
                    text+="状态："+_activitySnapshot.Status+"；最近记录："+(_activitySnapshot.LastEventAt.HasValue?_activitySnapshot.LastEventAt.Value.ToLocalTime().ToString("MM-dd HH:mm:ss"):"无")+"\r\n";
                }
                text+="最近60秒本机已上报记录；输入含缓存，输出含推理。\r\n新会话发现最多约60秒；无新记录不代表模型停止生成。";
            }
            if(_activityTooltip.GetToolTip(this)!=(text??""))_activityTooltip.SetToolTip(this,text);
        }
    }
}
