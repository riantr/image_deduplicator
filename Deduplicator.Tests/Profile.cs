using System.Diagnostics;
using Deduplicator.Models;
using Deduplicator.Services;

namespace Deduplicator.Tests;

/// <summary>
/// 算法性能剖析：找出真正的时间瓶颈
/// </summary>
public static class Profile
{
    public static void Run(string dir)
    {
        var log = new LogService { EnableFileLog = false };
        var svc = new DeduplicationService(log);

        var algorithms = new[]
        {
            AlgorithmType.FileHash, AlgorithmType.AHash, AlgorithmType.DHash, AlgorithmType.PHash,
            AlgorithmType.Lance, AlgorithmType.Histogram, AlgorithmType.FastDup,
            AlgorithmType.Template, AlgorithmType.Ssim, AlgorithmType.Sift, AlgorithmType.Surf
        };

        Console.WriteLine($"样本: {Directory.GetFiles(dir, "*.png").Length} 张 (400x300)");
        Console.WriteLine();
        Console.WriteLine($"{"算法",-12} {"特征ms",10} {"比对ms",10} {"比对量",12} {"组数",6}");
        Console.WriteLine(new string('─', 58));

        var results = new List<(AlgorithmType Algo, long FeatMs, long CmpMs, int Candidates, int Groups)>();

        foreach (var algo in algorithms)
        {
            var set = svc.ScanImages(dir, "filename");
            var p = new AlgorithmParameters
            {
                Threshold = algo is AlgorithmType.Sift or AlgorithmType.Surf ? 0.60 : 0.85
            };

            var sw = Stopwatch.StartNew();
            svc.CalculateFeaturesAsync(set, algo, p).GetAwaiter().GetResult();
            long featMs = sw.ElapsedMilliseconds;

            int candidates = CandidatePrefilter.BuildCandidates(set, algo, p).Count;

            sw.Restart();
            var res = svc.CompareFeaturesAsync(set, algo, p).GetAwaiter().GetResult();
            long cmpMs = sw.ElapsedMilliseconds;

            results.Add((algo, featMs, cmpMs, candidates, res.Count));

            foreach (var img in set) img.Dispose();
            GC.Collect(); GC.WaitForPendingFinalizers();
        }

        // 按比对耗时排序，突出瓶颈
        foreach (var r in results.OrderByDescending(r => r.CmpMs))
        {
            Console.WriteLine($"{r.Algo,-12} {r.FeatMs,10} {r.CmpMs,10} {r.Candidates,12:N0} {r.Groups,6}");
        }

        Console.WriteLine();
        long totalCmp = results.Sum(r => r.CmpMs);
        var top = results.OrderByDescending(r => r.CmpMs).Take(3).ToList();
        Console.WriteLine($"比对总耗时 {totalCmp}ms，最耗时三个：{string.Join(", ", top.Select(t => $"{t.Algo}({t.CmpMs}ms)"))}");
        Console.WriteLine($"三者占比 {top.Sum(t => t.CmpMs) * 100.0 / Math.Max(1, totalCmp):F1}%");
    }
}
