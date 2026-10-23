using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using CodexToolsHost.Usage;

namespace CodexToolsHost.UI
{
    public sealed partial class QuotaHudForm
    {
        private readonly LocalUsageService _usage;
        private readonly Timer _activityTimer = new Timer();
        private UsageActivitySnapshot _activitySnapshot;
        private long _activityBucket=long.MinValue;
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
                if(Visible&&IsHandleCreated)MessageBox.Show(this,"折叠状态保存失败；请检查配置目录权限后重试。","保存设置失败",MessageBoxButtons.OK,MessageBoxIcon.Information);
                return false;
            }
            ApplyDisplaySettings();
            if(ChartExpansionChanged!=null)ChartExpansionChanged();
            return true;
        }
    }
}
