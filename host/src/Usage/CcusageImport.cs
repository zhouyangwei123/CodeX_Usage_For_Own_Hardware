using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexToolsHost.Usage
{
    public static class CcusageImport
    {
        public const string ReferenceVersion = "20.0.22";
        public const int MaximumBytes = 16 * 1024 * 1024;
        public const string CommandHelp = "npx --yes ccusage@20.0.22 codex daily --json --offline --no-cost";
        public static UsageReport ReadFile(string path)
        {
            using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length > MaximumBytes) throw new FormatException("JSON 文件超过 16 MiB 上限。");
                using (StreamReader reader = new StreamReader(file, Encoding.UTF8, true)) return Parse(reader.ReadToEnd());
            }
        }
        public static UsageReport Parse(string json)
        {
            if (json == null || json.Length > MaximumBytes) throw new FormatException("JSON 文件超过上限或为空。");
            try
            {
                Dictionary<string, object> root = new JavaScriptSerializer { MaxJsonLength = MaximumBytes, RecursionLimit = 48 }.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) throw new FormatException();
                string[] keys = new[] { "daily", "monthly", "sessions" }.Where(root.ContainsKey).ToArray();
                if (keys.Length != 1) throw new FormatException();
                string kind = keys[0];
                object[] entries = root[kind] as object[];
                if (entries == null || entries.Length > 50000) throw new FormatException();
                List<UsageRow> rows = new List<UsageRow>();
                bool missingPricing = false;
                foreach (object item in entries)
                {
                    Dictionary<string, object> entry = item as Dictionary<string, object>;
                    Dictionary<string, object> models = UsageScanner.Object(entry, "models");
                    if (entry == null || models == null || entry.ContainsKey("agent")) throw new FormatException();
                    DateTime day = DateTime.MinValue;
                    DateTimeOffset activity = default(DateTimeOffset);
                    string session = "日汇总";
                    if (kind == "daily")
                    {
                        if (!DateTime.TryParseExact(UsageScanner.Text(entry, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) throw new FormatException();
                    }
                    else if (kind == "monthly")
                    {
                        if (!DateTime.TryParseExact(UsageScanner.Text(entry, "month"), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) throw new FormatException();
                        session = "月汇总";
                    }
                    else
                    {
                        // Never import working directories or raw session paths into the UI/export.
                        string id = UsageScanner.Text(entry, "sessionId");
                        if (string.IsNullOrWhiteSpace(id)) throw new FormatException();
                        session = "导入会话 " + (rows.Count + 1).ToString(CultureInfo.InvariantCulture);
                        if (!DateTimeOffset.TryParse(UsageScanner.Text(entry, "lastActivity"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out activity)) throw new FormatException();
                        day = activity.LocalDateTime.Date;
                    }
                    foreach (KeyValuePair<string, object> pair in models)
                    {
                        Dictionary<string, object> model = pair.Value as Dictionary<string, object>;
                        object missing;
                        if (model != null && model.TryGetValue("missingPricing", out missing) && missing is bool && (bool)missing) missingPricing = true;
                        long uncached = UsageScanner.Number(model, "inputTokens", true);
                        long cache = UsageScanner.Number(model, "cacheReadTokens", false);
                        long creation = UsageScanner.Number(model, "cacheCreationTokens", false);
                        long output = UsageScanner.Number(model, "outputTokens", true);
                        long reasoning = UsageScanner.Number(model, "reasoningOutputTokens", false);
                        long input = checked(uncached + cache + creation);
                        if (reasoning > output) throw new FormatException();
                        long total = checked(input + output);
                        if (model.ContainsKey("totalTokens") && UsageScanner.Number(model, "totalTokens", false) != total) throw new FormatException();
                        rows.Add(new UsageRow { Day = day, LastActivity = activity, SessionId = session,
                            Model = UsageScanner.SafeLabel(pair.Key, 100) ?? "未知模型", InputTokens = input, CachedInputTokens = cache,
                            OutputTokens = output, ReasoningOutputTokens = reasoning });
                    }
                }
                decimal? estimate = null;
                Dictionary<string, object> totals = UsageScanner.Object(root, "totals");
                object rawCost, rawUnpriced;
                if (totals != null && totals.TryGetValue("unpricedModels", out rawUnpriced) && rawUnpriced is object[] && ((object[])rawUnpriced).Length > 0) missingPricing = true;
                if (!missingPricing && totals != null && totals.TryGetValue("costUSD", out rawCost) && !(rawCost is bool))
                {
                    decimal cost;
                    if (decimal.TryParse(Convert.ToString(rawCost, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out cost) && cost >= 0 && cost < 1000000000000000m) estimate = cost;
                }
                List<string> warnings = new List<string> { "ccusage JSON 独立导入，不与本机数据相加。兼容参考版本 20.0.22。",
                    estimate.HasValue ? "导入全量 API 等价估算：$" + estimate.Value.ToString("0.######", CultureInfo.InvariantCulture) + "（未按当前日期筛选，非订阅费用）。" : "费用未知；缺少完整价格或未导出费用，不能视为免费。" };
                if (kind == "sessions") warnings.Add("会话导入仅含整体合计，不能按日期拆分；日期为最近活动日期。");
                if (kind == "monthly") warnings.Add("月汇总不能还原每日数据，仅显示导入的整月合计。");
                return new UsageReport { Source = "ccusage JSON（独立导入）", IsImported = true, PeriodKind = kind,
                    Rows = rows.AsReadOnly(), Warnings = warnings.AsReadOnly(), UpdatedAt = DateTimeOffset.Now, ImportedApiEstimateUsd = estimate };
            }
            catch (ArgumentException) { throw new FormatException("无法读取 JSON，请导入 ccusage Codex 专用 daily / monthly / session 输出。"); }
            catch (InvalidOperationException) { throw new FormatException("无法读取 JSON，请导入 ccusage Codex 专用输出。"); }
            catch (OverflowException) { throw new FormatException("Token 计数超出范围。"); }
            catch (FormatException) { throw new FormatException("格式或计数不兼容；请使用帮助中的固定版本 Codex 专用命令。"); }
        }
    }
}
