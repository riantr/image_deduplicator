using Deduplicator.Models;
using Deduplicator.Services;

namespace Deduplicator.Tests;

/// <summary>
/// SSIM 相似度分布诊断
/// </summary>
public static class SsimDiag
{
    public static void Run(string dir)
    {
        var log = new LogService { EnableFileLog = false };
        var svc = new DeduplicationService(log);
        var set = svc.ScanImages(dir, "filename");
        svc.CalculateFeaturesAsync(set, AlgorithmType.Ssim, new AlgorithmParameters()).GetAwaiter().GetResult();

        if (set.Count < 6)
        {
            Console.WriteLine($"样本不足 ({set.Count})");
            return;
        }

        // 自动挑选样本：全量两两计算，统计分布
        var vals = new List<double>();
        int limit = Math.Min(30, set.Count);
        for (int i = 0; i < limit; i++)
        {
            for (int j = i + 1; j < limit; j++)
            {
                vals.Add(svc.CalculateSimilarityPublic(set[i], set[j], AlgorithmType.Ssim, new AlgorithmParameters()));
            }
        }

        vals.Sort();
        Console.WriteLine($"前 {limit} 张两两 SSIM（共 {vals.Count} 对）:");
        Console.WriteLine($"  min={vals.First():F4}  p25={vals[vals.Count / 4]:F4}  "
            + $"中位={vals[vals.Count / 2]:F4}  p75={vals[vals.Count * 3 / 4]:F4}  max={vals.Last():F4}");
        Console.WriteLine($"  >=0.95: {vals.Count(v => v >= 0.95)} 对");
        Console.WriteLine($"  >=0.85: {vals.Count(v => v >= 0.85)} 对");
        Console.WriteLine($"  >=0.75: {vals.Count(v => v >= 0.75)} 对");
        Console.WriteLine($"  >=0.60: {vals.Count(v => v >= 0.60)} 对");

        foreach (var i in set) i.Dispose();
    }
}
