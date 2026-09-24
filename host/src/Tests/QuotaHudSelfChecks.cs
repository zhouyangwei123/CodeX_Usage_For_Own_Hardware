using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using CodexToolsHost.Core;
using CodexToolsHost.Model;
using CodexToolsHost.Monitor;
using CodexToolsHost.Protocol;
using CodexToolsHost.Quota;
using CodexToolsHost.UI;

namespace CodexToolsHost.Tests
{
    internal static class QuotaHudSelfChecks
    {
        public static Dictionary<string, object> Run(string tempRoot)
        {
            var result = new Dictionary<string, object>();
            Type presentation = Type.GetType(
                "CodexToolsHost.UI.QuotaHudPresentation, CodexToolsHost");
            bool presentationExists = presentation != null;
            bool percentUnknown = presentationExists
                && (string)presentation.GetMethod("FormatPercent").Invoke(
                    null, new object[] { null }) == "--%";
            bool percentClamped = presentationExists
                && (string)presentation.GetMethod("FormatPercent").Invoke(
                    null, new object[] { 120 }) == "100%";
            bool apiMoney = presentationExists
                && (string)presentation.GetMethod("FormatApiBalance").Invoke(
                    null, new object[] { "DeepSeek", "CNY", 12345L, true, false, false })
                    == "DeepSeek \u00b7 CNY 123.45";
            bool apiStalePreservesValue = presentationExists
                && ((string)presentation.GetMethod("FormatApiBalance").Invoke(
                    null, new object[] { "DeepSeek", "CNY", 12345L, true, true, false }))
                    .IndexOf("123.45", StringComparison.Ordinal) >= 0;
            bool apiUnlimited = presentationExists
                && ((string)presentation.GetMethod("FormatApiBalance").Invoke(
                    null, new object[] { "OpenRouter", "USD", 0L, true, false, true }))
                    .IndexOf("UNLIMITED", StringComparison.OrdinalIgnoreCase) >= 0;
            MethodInfo formatNetworkSpeed = presentationExists
                ? presentation.GetMethod("FormatNetworkSpeed", new Type[] {
                    typeof(bool), typeof(uint), typeof(uint) }) : null;
            bool networkUnavailable = formatNetworkSpeed != null
                && (string)formatNetworkSpeed.Invoke(null,
                    new object[] { false, 0u, 0u }) == "↑-- ↓--";
            bool networkUnits = formatNetworkSpeed != null
                && (string)formatNetworkSpeed.Invoke(null,
                    new object[] { true, 512u, 2u }) == "↑2K ↓512K"
                && (string)formatNetworkSpeed.Invoke(null,
                    new object[] { true, 1536u, 1024u }) == "↑1.0M ↓1.5M"
                && (string)formatNetworkSpeed.Invoke(null,
                    new object[] { true, 1024u * 1024u, 1024u * 1024u * 2u })
                    == "↑2.0G ↓1.0G";

            PropertyInfo hudVisible = typeof(AppConfig).GetProperty("QuotaHudVisible");
            PropertyInfo hudScale = typeof(AppConfig).GetProperty("QuotaHudScalePercent");
            PropertyInfo hudOpacity = typeof(AppConfig).GetProperty("QuotaHudOpacity");
            PropertyInfo hudTopMost = typeof(AppConfig).GetProperty("QuotaHudTopMost");
            AppConfig defaults = AppConfig.CreateDefault();
            bool defaultsVisible = hudVisible != null
                && (bool)hudVisible.GetValue(defaults, null);
            bool displaySettingsDefaults = hudScale != null && hudOpacity != null
                && hudTopMost != null
                && (int)hudScale.GetValue(defaults, null) == 75
                && Math.Abs((double)hudOpacity.GetValue(defaults, null) - 0.90d) < 0.001d
                && (bool)hudTopMost.GetValue(defaults, null);
            bool positionsRoundTrip = typeof(AppConfig).GetProperty("QuotaHudX") != null
                && typeof(AppConfig).GetProperty("QuotaHudY") != null;
            bool noSeparateApiConfig = typeof(AppConfig).GetProperty("ApiBalanceVisible") == null
                && typeof(AppConfig).GetProperty("ApiBalanceX") == null
                && typeof(AppConfig).GetProperty("ApiBalanceY") == null;
            bool configRoundTripOk = CheckConfigRoundTrip(tempRoot);

            bool dataContractOk = presentationExists && percentUnknown && percentClamped
                && apiMoney && apiStalePreservesValue && apiUnlimited
                && networkUnavailable && networkUnits
                && defaultsVisible && positionsRoundTrip && noSeparateApiConfig
                && displaySettingsDefaults && configRoundTripOk;
            result["quotaHudDataContractOk"] = dataContractOk;
            result["quotaHudPresentationExists"] = presentationExists;
            result["quotaHudPercentFormattingOk"] = percentUnknown && percentClamped;
            result["quotaHudApiFormattingOk"] = apiMoney && apiStalePreservesValue && apiUnlimited;
            result["quotaHudNetworkFormattingOk"] = networkUnavailable && networkUnits;
            result["quotaHudConfigContractOk"] = defaultsVisible && positionsRoundTrip
                && noSeparateApiConfig && configRoundTripOk;
            result["quotaHudConfigRoundTripOk"] = configRoundTripOk;
            result["quotaHudDisplayConfigContractOk"] = displaySettingsDefaults
                && configRoundTripOk;

            Type renderer = Type.GetType(
                "CodexToolsHost.UI.QuotaHudRenderer, CodexToolsHost");
            bool fixedCapsule = false;
            bool layoutOk = false;
            if (renderer != null)
            {
                object instance = Activator.CreateInstance(renderer, true);
                PropertyInfo preferredSize = renderer.GetProperty("PreferredSize");
                fixedCapsule = preferredSize != null
                    && (Size)preferredSize.GetValue(instance, null) == new Size(320, 78);
                MethodInfo calculate = renderer.GetMethod("CalculateLayout");
                if (calculate != null)
                {
                    object layout = calculate.Invoke(instance, new object[] {
                        new Rectangle(0, 0, 320, 78),
                        new QuotaSnapshot(75, 40, null, null, DateTimeOffset.UtcNow, false)
                    });
                    PropertyInfo primaryText = layout.GetType().GetProperty("PrimaryText");
                    PropertyInfo secondaryText = layout.GetType().GetProperty("SecondaryText");
                    PropertyInfo primaryTrack = layout.GetType().GetProperty("PrimaryTrack");
                    PropertyInfo primaryFill = layout.GetType().GetProperty("PrimaryFill");
                    PropertyInfo secondaryTrack = layout.GetType().GetProperty("SecondaryTrack");
                    PropertyInfo secondaryFill = layout.GetType().GetProperty("SecondaryFill");
                    layoutOk = primaryText != null && secondaryText != null
                        && primaryTrack != null && primaryFill != null
                        && secondaryTrack != null && secondaryFill != null
                        && (string)primaryText.GetValue(layout, null) == "75%"
                        && (string)secondaryText.GetValue(layout, null) == "40%"
                        && ((Rectangle)primaryFill.GetValue(layout, null)).Width
                            == ((Rectangle)primaryTrack.GetValue(layout, null)).Width * 75 / 100
                        && ((Rectangle)secondaryFill.GetValue(layout, null)).Width
                            == ((Rectangle)secondaryTrack.GetValue(layout, null)).Width * 40 / 100;

                    object empty = calculate.Invoke(instance, new object[] {
                        new Rectangle(0, 0, 320, 78), QuotaSnapshot.EmptyStale()
                    });
                    PropertyInfo emptyPrimaryText = empty.GetType().GetProperty("PrimaryText");
                    PropertyInfo emptySecondaryText = empty.GetType().GetProperty("SecondaryText");
                    PropertyInfo emptyPrimaryFill = empty.GetType().GetProperty("PrimaryFill");
                    PropertyInfo emptySecondaryFill = empty.GetType().GetProperty("SecondaryFill");
                    layoutOk = layoutOk && emptyPrimaryText != null && emptySecondaryText != null
                        && emptyPrimaryFill != null && emptySecondaryFill != null
                        && (string)emptyPrimaryText.GetValue(empty, null) == "--%"
                        && (string)emptySecondaryText.GetValue(empty, null) == "--%"
                        && ((Rectangle)emptyPrimaryFill.GetValue(empty, null)).Width == 0
                        && ((Rectangle)emptySecondaryFill.GetValue(empty, null)).Width == 0;
                }
                IDisposable disposable = instance as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
            result["quotaHudRendererExists"] = renderer != null;
            result["quotaHudFixedCapsuleOk"] = fixedCapsule;
            result["quotaHudLayoutOk"] = layoutOk;

            Type hudFormType = Type.GetType(
                "CodexToolsHost.UI.QuotaHudForm, CodexToolsHost");
            FieldInfo apiHeightField = hudFormType == null ? null
                : hudFormType.GetField("ApiHeight",
                    BindingFlags.Static | BindingFlags.NonPublic);
            bool twoLineHeightOk = apiHeightField != null
                && (int)apiHeightField.GetValue(null) >= 54;
            result["quotaHudTwoLineHeightOk"] = twoLineHeightOk;
            bool goTextFitOk = CheckGoTextFit(hudFormType, tempRoot);
            result["quotaHudGoTextFitOk"] = goTextFitOk;
            Type apiFormType = Type.GetType(
                "CodexToolsHost.UI.ApiBalanceForm, CodexToolsHost");
            bool hudLifecycleOk;
            bool hudScaledRenderOk;
            bool hudWindowContractOk = CheckWindowContract(hudFormType,
                new Type[] { typeof(AppConfig), typeof(ICodexStatusSource),
                    typeof(IDeepSeekSource), typeof(Action), typeof(BridgeService) }, tempRoot,
                    out hudLifecycleOk, out hudScaledRenderOk);
            bool apiWindowContractOk = apiFormType == null;
            result["quotaHudWindowContractOk"] = hudWindowContractOk;
            result["quotaHudPcMetricsLifecycleOk"] = hudLifecycleOk;
            result["quotaHudScaledRenderOk"] = hudScaledRenderOk;
            result["apiBalanceWindowContractOk"] = apiWindowContractOk;
            bool refreshClickContractOk = hudFormType != null
                && hudFormType.GetMethod("OnMouseUp",
                    BindingFlags.Instance | BindingFlags.NonPublic) != null;
            result["quotaHudRefreshClickContractOk"] = refreshClickContractOk;

            bool trayQuotaHudContractOk = CheckTrayContract(tempRoot);
            result["trayQuotaHudContractOk"] = trayQuotaHudContractOk;
            return result;
        }

        private static bool CheckTrayContract(string tempRoot)
        {
            BridgeService bridge = null;
            TrayApp app = null;
            Form settings = null;
            try
            {
                AppConfig config = AppConfig.Load(tempRoot, tempRoot);
                MockDeviceLink link = new MockDeviceLink { EmitInfoOnConnect = false };
                bridge = new BridgeService(config, link, new FakeCodexSource(), null,
                    new FakeDeepSeekSource());
                app = new TrayApp(config, bridge);

                FieldInfo hudField = typeof(TrayApp).GetField("_quotaHud",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo apiField = typeof(TrayApp).GetField("_apiBalance",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                QuotaHudForm hud = hudField == null ? null : hudField.GetValue(app) as QuotaHudForm;
                bool windowsVisible = hud != null && apiField == null
                    && !hud.IsDisposed && hud.Visible;

                FieldInfo iconField = typeof(TrayApp).GetField("_icon",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo settingsField = typeof(TrayApp).GetField("_settingsForm",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                NotifyIcon icon = iconField == null ? null : iconField.GetValue(app) as NotifyIcon;
                ContextMenuStrip menu = icon == null ? null : icon.ContextMenuStrip;
                bool menuOk = menu != null
                    && CountMenuText(menu.Items, "显示额度与 API 余额") == 1
                    && CountMenuText(menu.Items, "血条尺寸") == 1
                    && CountMenuText(menu.Items, "血条透明度") == 1
                    && CountMenuText(menu.Items, "始终置顶") == 1
                    && ContainsMenuText(menu.Items, "60%")
                    && ContainsMenuText(menu.Items, "75%")
                    && ContainsMenuText(menu.Items, "90%")
                    && ContainsMenuText(menu.Items, "100%")
                    && ContainsMenuText(menu.Items, "35%")
                    && ContainsMenuText(menu.Items, "50%")
                    && ContainsMenuText(menu.Items, "70%")
                    && !ContainsMenuText(menu.Items, "显示额度血条")
                    && !ContainsMenuText(menu.Items, "显示 API 余额")
                    && !ContainsMenuText(menu.Items, "样式")
                    && !ContainsMenuText(menu.Items, "Style A")
                    && !ContainsMenuText(menu.Items, "Style B")
                    && !ContainsMenuText(menu.Items, "游戏 HUD");

                MethodInfo onDoubleClick = typeof(NotifyIcon).GetMethod("OnDoubleClick",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (onDoubleClick != null && icon != null)
                    onDoubleClick.Invoke(icon, new object[] { EventArgs.Empty });
                settings = settingsField == null ? null : settingsField.GetValue(app) as Form;
                bool doubleClickOk = settings != null && !settings.IsDisposed
                    && ContainsControlText(settings, "网速：");
                if (settings != null) settings.Close();
                return windowsVisible && menuOk && doubleClickOk;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (settings != null && !settings.IsDisposed) settings.Dispose();
                if (app != null) app.Dispose();
                else if (bridge != null) bridge.Dispose();
            }
        }

        private static bool ContainsMenuText(ToolStripItemCollection items, string text)
        {
            if (items == null) return false;
            foreach (ToolStripItem item in items)
            {
                if (string.Equals(item.Text, text, StringComparison.Ordinal)) return true;
                ToolStripMenuItem menu = item as ToolStripMenuItem;
                if (menu != null && ContainsMenuText(menu.DropDownItems, text)) return true;
            }
            return false;
        }

        private static bool ContainsControlText(Control control, string text)
        {
            if (control == null) return false;
            if (string.Equals(control.Text, text, StringComparison.Ordinal)) return true;
            foreach (Control child in control.Controls)
            {
                if (ContainsControlText(child, text)) return true;
            }
            return false;
        }

        private static bool CheckConfigRoundTrip(string tempRoot)
        {
            string root = Path.Combine(tempRoot,
                "QuotaHudConfigRoundtrip-" + Guid.NewGuid().ToString("N"));
            try
            {
                AppConfig config = AppConfig.Load(root, root);
                config.QuotaHudVisible = false;
                config.QuotaHudX = 101;
                config.QuotaHudY = 202;
                PropertyInfo scale = typeof(AppConfig).GetProperty("QuotaHudScalePercent");
                PropertyInfo opacity = typeof(AppConfig).GetProperty("QuotaHudOpacity");
                PropertyInfo topMost = typeof(AppConfig).GetProperty("QuotaHudTopMost");
                if (scale == null || opacity == null || topMost == null) return false;
                scale.SetValue(config, 60, null);
                opacity.SetValue(config, 0.50d, null);
                topMost.SetValue(config, false, null);
                config.Save();

                AppConfig loaded = AppConfig.Load(root, root);
                return !loaded.QuotaHudVisible
                    && loaded.QuotaHudX == 101 && loaded.QuotaHudY == 202
                    && loaded.ConfigVersion == 9
                    && (int)scale.GetValue(loaded, null) == 60
                    && Math.Abs((double)opacity.GetValue(loaded, null) - 0.50d) < 0.001d
                    && !(bool)topMost.GetValue(loaded, null);
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch (Exception) { }
            }
        }

        private static bool CheckWindowContract(Type formType, Type[] parameterTypes,
            string tempRoot, out bool lifecycleOk, out bool scaledRenderOk)
        {
            lifecycleOk = false;
            scaledRenderOk = false;
            if (formType == null || !typeof(Form).IsAssignableFrom(formType)) return false;
            ConstructorInfo constructor = formType.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, parameterTypes, null);
            MethodInfo shutdown = formType.GetMethod("BeginShutdown",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (constructor == null || shutdown == null) return false;
            Form form = null;
            BridgeService bridge = null;
            try
            {
                AppConfig config = AppConfig.Load(tempRoot, tempRoot);
                config.QuotaHudScalePercent = 100;
                config.QuotaHudOpacity = 0.35d;
                config.QuotaHudTopMost = false;
                config.Save();
                config = AppConfig.Load(tempRoot, tempRoot);
                /* 每次窗口合同都从固定基线开始；缩放循环可继续在 tempRoot 写入渲染图。 */
                config.QuotaHudScalePercent = 75;
                config.QuotaHudOpacity = 0.90d;
                config.QuotaHudTopMost = true;
                bridge = new BridgeService(config, new MockDeviceLink { EmitInfoOnConnect = false },
                    new FakeCodexSource(), null, new FakeDeepSeekSource());
                form = (Form)constructor.Invoke(new object[] { config, new FakeCodexSource(),
                    new FakeDeepSeekSource(), new Action(delegate { }), bridge });
                IntPtr ignored = form.Handle;
                bool shapeOk = !form.ShowInTaskbar
                    && form.FormBorderStyle == FormBorderStyle.None
                    && form.TopMost
                    && form.ClientSize == new Size(240, 104)
                    && form.AutoScaleMode == AutoScaleMode.None
                    && !form.MaximizeBox && !form.MinimizeBox
                    && form.MinimumSize == form.Size
                    && form.MaximumSize == form.Size;
                MethodInfo applySettings = formType.GetMethod("ApplyDisplaySettings",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (config == null || applySettings == null) return false;
                lifecycleOk = CheckPcMetricsLifecycle(formType, constructor, config);
                scaledRenderOk = CheckScaledWindowStates(form, config, applySettings, tempRoot);
                shutdown.Invoke(form, null);
                return shapeOk && lifecycleOk && scaledRenderOk;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (form != null) form.Dispose();
                if (bridge != null) bridge.Dispose();
            }
        }

        private static bool CheckGoTextFit(Type formType, string tempRoot)
        {
            if (formType == null) return false;
            Form form = null;
            BridgeService bridge = null;
            try
            {
                AppConfig config = AppConfig.Load(tempRoot, tempRoot);
                bridge = new BridgeService(config,
                    new MockDeviceLink { EmitInfoOnConnect = false },
                    new FakeCodexSource(), null, new FakeDeepSeekSource());
                form = new QuotaHudForm(config, new FakeCodexSource(),
                    new FakeDeepSeekSource(), new Action(delegate { }), bridge);
                FieldInfo fontField = formType.GetField("_apiGoFont",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Font font = fontField == null ? null : fontField.GetValue(form) as Font;
                if (font == null || font.Size > 7.5f) return false;
                using (Bitmap bitmap = new Bitmap(10, 10))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    SizeF measured = graphics.MeasureString(
                        "GO 5h余100%  7d余60%", font);
                    return measured.Width <= 126f;
                }
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (form != null) form.Dispose();
                if (bridge != null) bridge.Dispose();
            }
        }

        private static bool CheckScaledWindowStates(Form form, AppConfig config,
            MethodInfo applySettings, string tempRoot)
        {
            int[] percents = { 60, 75, 90, 100 };
            Size[] expectedClients = {
                new Size(192, 83), new Size(240, 104), new Size(288, 124), new Size(320, 138) };
            bool allOk = true;
            for (int i = 0; i < percents.Length; i++)
            {
                Region previousRegion = form.Region;
                config.QuotaHudScalePercent = percents[i];
                config.QuotaHudOpacity = i == 0 ? 0.50d : 0.90d;
                config.QuotaHudTopMost = i != 0;
                applySettings.Invoke(form, null);
                allOk = allOk && form.ClientSize == expectedClients[i]
                    && form.MinimumSize == form.Size && form.MaximumSize == form.Size
                    && form.Region != null
                    && IsRegionDisposed(previousRegion)
                    && Math.Abs(form.ClientSize.Width / 320f - percents[i] / 100f) < 0.001f;
                using (Graphics graphics = form.CreateGraphics())
                {
                    Rectangle bounds = Rectangle.Round(form.Region.GetBounds(graphics));
                    allOk = allOk && bounds == new Rectangle(0, 0, form.Width, form.Height);
                }
                allOk = allOk && SaveAndCheckRender(form, tempRoot, percents[i]);
            }
            return allOk && Math.Abs(form.Opacity - 0.90d) < 0.001d && form.TopMost;
        }

        private static bool SaveAndCheckRender(Form form, string tempRoot, int percent)
        {
            string path = Path.Combine(tempRoot, "quota-hud-" + percent + ".png");
            using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(path);
                Color background = Color.FromArgb(22, 27, 36);
                Color panel = Color.FromArgb(18, 22, 30);
                bool exactColors = PixelEquals(bitmap, ScalePoint(form, 70, 92), panel)
                    && PixelEquals(bitmap, ScalePoint(form, 240, 92), panel)
                    && PixelEquals(bitmap, ScalePoint(form, 160, 105), background)
                    && PixelEquals(bitmap, ScalePoint(form, 2, 86), background);

                int expectedRadius = Math.Max(1,
                    (int)Math.Round(13f * Math.Min(
                        form.ClientSize.Width / 320f, form.ClientSize.Height / 138f)));
                int topInset = MeasureTopRegionInset(form.Region,
                    Math.Min(1, Math.Max(0, form.Height - 1)), form.Width);
                int expectedInset = ExpectedTopInset(expectedRadius, 1);
                bool roundedRegion = form.Region != null
                    && !form.Region.IsVisible(0, 0)
                    && form.Region.IsVisible(expectedRadius, 1)
                    && Math.Abs(topInset - expectedInset) <= 1;
                return exactColors && roundedRegion;
            }
        }

        private static bool CheckPcMetricsLifecycle(Type formType, ConstructorInfo constructor,
            AppConfig config)
        {
            Form form = null;
            BridgeService bridge = null;
            PcMonitorService monitor = null;
            try
            {
                var provider = new MutablePcMetricsProvider(
                    NetworkSnapshot(512u, 2u));
                monitor = new PcMonitorService(provider, 500);
                monitor.RefreshNow();
                bridge = new BridgeService(config,
                    new MockDeviceLink { EmitInfoOnConnect = false },
                    new FakeCodexSource(), null, new FakeDeepSeekSource(), monitor);
                form = (Form)constructor.Invoke(new object[] { config, new FakeCodexSource(),
                    new FakeDeepSeekSource(), new Action(delegate { }), bridge });

                FieldInfo textField = formType.GetField("_networkDisplayText",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (textField == null || form.IsHandleCreated
                    || (string)textField.GetValue(form) != "↑2K ↓512K"
                    || CountPcMetricsSubscribers(bridge) != 1)
                    return false;

                provider.Snapshot = NetworkSnapshot(1536u, 1024u);
                PublishPcMetricsFromWorker(bridge, monitor);
                bool hiddenDidNotTouchUi = (string)textField.GetValue(form) == "↑2K ↓512K";
                IntPtr ignored = form.Handle;
                bool firstShowUsesLatest = (string)textField.GetValue(form) == "↑1.0M ↓1.5M";

                provider.Snapshot = NetworkSnapshot(4096u, 3072u);
                PublishPcMetricsFromWorker(bridge, monitor);
                bool queuedBeforePump = (string)textField.GetValue(form) == "↑1.0M ↓1.5M";
                bool marshaledToUi = PumpUntil(delegate
                {
                    return (string)textField.GetValue(form) == "↑3.0M ↓4.0M";
                }, 1000);

                provider.Snapshot = NetworkSnapshot(8192u, 7168u);
                monitor.RefreshNow();
                MethodInfo showFromTray = formType.GetMethod("ShowFromTray",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (showFromTray == null) return false;
                showFromTray.Invoke(form, null);
                bool trayShowUsesLatest = (string)textField.GetValue(form) == "↑7.0M ↓8.0M";

                provider.Snapshot = NetworkSnapshot(16384u, 15360u);
                PublishPcMetricsFromWorker(bridge, monitor);
                form.Dispose();
                bool unsubscribed = CountPcMetricsSubscribers(bridge) == 0;
                Application.DoEvents();
                PublishBridgeEventFromWorker(bridge, monitor.Current);

                return hiddenDidNotTouchUi && firstShowUsesLatest && queuedBeforePump
                    && marshaledToUi && trayShowUsesLatest && unsubscribed;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (form != null) form.Dispose();
                if (bridge != null) bridge.Dispose();
                else if (monitor != null) monitor.Dispose();
            }
        }

        private static PcMetricsSnapshot NetworkSnapshot(uint download, uint upload)
        {
            return new PcMetricsSnapshot
            {
                NetworkSpeedAvailable = true,
                NetworkDownloadKiBPerSecond = download,
                NetworkUploadKiBPerSecond = upload,
                SampledAtUtc = DateTimeOffset.UtcNow
            };
        }

        private static void PublishPcMetricsFromWorker(BridgeService bridge,
            PcMonitorService monitor)
        {
            Thread worker = new Thread(new ThreadStart(delegate
            {
                monitor.RefreshNow();
                PublishBridgeEvent(bridge, monitor.Current);
            }));
            worker.IsBackground = true;
            worker.Start();
            if (!worker.Join(2000)) throw new TimeoutException("PC metrics worker timed out");
        }

        private static void PublishBridgeEventFromWorker(BridgeService bridge,
            PcMetricsSnapshot snapshot)
        {
            Thread worker = new Thread(new ThreadStart(
                delegate { PublishBridgeEvent(bridge, snapshot); }));
            worker.IsBackground = true;
            worker.Start();
            if (!worker.Join(2000)) throw new TimeoutException("PC metrics event timed out");
        }

        private static void PublishBridgeEvent(BridgeService bridge, PcMetricsSnapshot snapshot)
        {
            MethodInfo publish = typeof(BridgeService).GetMethod("OnPcMetricsChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (publish == null) throw new MissingMethodException("OnPcMetricsChanged");
            publish.Invoke(bridge, new object[] { snapshot });
        }

        private static int CountPcMetricsSubscribers(BridgeService bridge)
        {
            FieldInfo field = typeof(BridgeService).GetField("PcMetricsChanged",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Delegate subscribers = field == null ? null : field.GetValue(bridge) as Delegate;
            return subscribers == null ? 0 : subscribers.GetInvocationList().Length;
        }

        private static bool PumpUntil(Func<bool> condition, int timeoutMs)
        {
            int started = Environment.TickCount;
            while (!condition())
            {
                Application.DoEvents();
                if ((int)(Environment.TickCount - started) >= timeoutMs) return false;
                Thread.Sleep(5);
            }
            return true;
        }

        private static Point ScalePoint(Form form, int canonicalX, int canonicalY)
        {
            int x = Math.Min(form.Width - 1,
                Math.Max(0, (int)Math.Round(canonicalX * form.ClientSize.Width / 320f)));
            int y = Math.Min(form.Height - 1,
                Math.Max(0, (int)Math.Round(canonicalY * form.ClientSize.Height / 138f)));
            return new Point(x, y);
        }

        private static bool PixelEquals(Bitmap bitmap, Point point, Color expected)
        {
            Color actual = bitmap.GetPixel(point.X, point.Y);
            return actual.ToArgb() == expected.ToArgb();
        }

        private static int MeasureTopRegionInset(Region region, int y, int width)
        {
            if (region == null) return -1;
            for (int x = 0; x < width; x++)
                if (region.IsVisible(x, y)) return x;
            return -1;
        }

        private static int ExpectedTopInset(int radius, int y)
        {
            double dy = radius - y;
            return (int)Math.Ceiling(radius - Math.Sqrt(
                Math.Max(0d, radius * radius - dy * dy)));
        }

        private static bool IsRegionDisposed(Region region)
        {
            if (region == null) return true;
            try
            {
                region.IsVisible(0, 0);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }

        private sealed class MutablePcMetricsProvider : IPcMetricsProvider
        {
            public MutablePcMetricsProvider(PcMetricsSnapshot snapshot)
            {
                Snapshot = snapshot;
            }

            public PcMetricsSnapshot Snapshot { get; set; }

            public PcMetricsSnapshot Read()
            {
                return Snapshot;
            }
        }

        private static int CountMenuText(ToolStripItemCollection items, string text)
        {
            if (items == null) return 0;
            int count = 0;
            foreach (ToolStripItem item in items)
            {
                if (string.Equals(item.Text, text, StringComparison.Ordinal)) count++;
                ToolStripMenuItem menu = item as ToolStripMenuItem;
                if (menu != null) count += CountMenuText(menu.DropDownItems, text);
            }
            return count;
        }
    }
}
