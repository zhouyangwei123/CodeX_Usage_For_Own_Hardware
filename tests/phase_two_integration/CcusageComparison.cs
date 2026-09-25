using System;
using System.IO;
using System.Web.Script.Serialization;
using CodexToolsHost.Usage;
internal static class CcusageComparison
{
    static int Main(string[] args)
    {
        using (var native = new LocalUsageService(args[0]))
        {
            native.RefreshAsync().GetAwaiter().GetResult();
            UsageReport imported = CcusageImport.ReadFile(args[1]);
            UsageRow left = native.Report.Totals, right = imported.Totals;
            bool passed = native.Report.FilesPending == 0 && left.InputTokens == 1280000 && left.CachedInputTokens == 940000
                && left.OutputTokens == 86000 && left.TotalTokens == 1366000
                && left.InputTokens == right.InputTokens && left.CachedInputTokens == right.CachedInputTokens
                && left.OutputTokens == right.OutputTokens && left.TotalTokens == right.TotalTokens;
            string result = new JavaScriptSerializer().Serialize(new { passed = passed, ccusageVersion = CcusageImport.ReferenceVersion,
                inputIncludingCache = right.InputTokens, cachedInput = right.CachedInputTokens, output = right.OutputTokens,
                total = right.TotalTokens, syntheticFixtureOnly = true });
            File.WriteAllText(args[2], result); Console.WriteLine(result); return passed ? 0 : 1;
        }
    }
}
