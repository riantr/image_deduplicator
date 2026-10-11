using Deduplicator.Models;
using Deduplicator.Services;

namespace Deduplicator.Tests;

/// <summary>
/// 去重算法测试：特征计算、相似度、预筛无漏报、内存占用
/// </summary>
public static class DeduplicationTests
{
    private static readonly AlgorithmType[] AllAlgorithms =
    {
        AlgorithmType.FileHash, AlgorithmType.AHash, AlgorithmType.DHash, AlgorithmType.PHash,
        AlgorithmType.FastDup, AlgorithmType.Histogram, AlgorithmType.Template,
        AlgorithmType.Ssim, AlgorithmType.Sift, AlgorithmType.Surf, AlgorithmType.Lance
    };

    public static void Run(string workDir)
    {
        var log = new LogService { EnableFileLog = false };
        var svc = new DeduplicationService(log);

        ScanTest(svc, workDir);
        FeatureTest(svc, workDir);
        SimilarityTest(svc, workDir);
        PrefilterTest(svc, workDir);
        MemoryTest(svc, workDir);
    }

    private static AlgorithmParameters Params(AlgorithmType algo) => new()
    {
        Threshold = algo is AlgorithmType.Sift or AlgorithmType.Surf ? 0.60 : 0.85
    };

    // ---------- 扫描 ----------
    private static void ScanTest(DeduplicationService svc, string dir)
    {
        Assert.Section("目录扫描");

        var (ms, images) = Bench.Time(() => svc.ScanImages(dir, "filename"));

        Assert.Equal(TestData.GeneratedFileCount, images.Count, "扫描到预期数量的图像");
        Assert.True(images.All(i => i.FileSize > 0), "所有图像记录了文件大小",
            images.FirstOrDefault(i => i.FileSize <= 0)?.FilePath);
        Assert.That(ms < 5000, $"扫描耗时可接受（{ms}ms）");

        foreach (var img in images) img.Dispose();
    }

    // ---------- 特征计算 ----------
    private static void FeatureTest(DeduplicationService svc, string dir)
    {
        Assert.Section("特征计算（全部算法）");

        foreach (var algo in AllAlgorithms)
        {
            var set = svc.ScanImages(dir, "filename");
            var p = Params(algo);

            long ms = Bench.Time(() =>
   svc.CalculateFeaturesAsync(set, algo, p).GetAwaiter().GetResult());

            int ok = set.Count(i => i.HasFeature(algo));

            // SIFT/ORB 在纯色/平滑图上本就无角点，特征缺失属预期；
            // 其余算法必须全部成功
            if (algo is AlgorithmType.Sift or AlgorithmType.Surf)
            {
                Assert.That(ok > 0, $"{algo}: 特征提取完成且未崩溃（{ok}/{set.Count} 有特征）");
            }
            else
            {
                Assert.Equal(set.Count, ok, $"{algo}: 全部图像特征计算成功（{ms}ms）");
            }

            foreach (var img in set) img.Dispose();
        }
    }

    // ---------- 相似度正确性 ----------
    private static void SimilarityTest(DeduplicationService svc, string dir)
    {
        Assert.Section("相似度正确性");

        var set = svc.ScanImages(dir, "filename");
        var p = new AlgorithmParameters { Threshold = 0.85 };

        svc.CalculateFeaturesAsync(set, AlgorithmType.FileHash, p).GetAwaiter().GetResult();

        var a1 = set.First(i => i.FilePath.EndsWith("orig_a.png", StringComparison.Ordinal));
        var a1copy = set.First(i => i.FilePath.EndsWith("copy_a.png", StringComparison.Ordinal));
        var a2 = set.First(i => i.FilePath.EndsWith("orig_b.png", StringComparison.Ordinal));

        // 完全相同的副本：FileHash 相似度必须为 1
        Assert.Equal(1.0, svc.CalculateSimilarityPublic(a1, a1copy, AlgorithmType.FileHash, p),
            "FileHash: 字节相同的副本相似度为 1");

        // 不同文件：必须为 0
        Assert.Equal(0.0, svc.CalculateSimilarityPublic(a1, a2, AlgorithmType.FileHash, p),
            "FileHash: 不同文件相似度为 0");

        // 感知哈希：相同副本也必须判定为高相似
        foreach (var algo in new[] { AlgorithmType.AHash, AlgorithmType.DHash, AlgorithmType.PHash })
        {
            var hs = svc.ScanImages(dir, "filename");
            svc.CalculateFeaturesAsync(hs, algo, p).GetAwaiter().GetResult();

            var x = hs.First(i => i.FilePath.EndsWith("orig_a.png", StringComparison.Ordinal));
            var y = hs.First(i => i.FilePath.EndsWith("copy_a.png", StringComparison.Ordinal));

            double sim = svc.CalculateSimilarityPublic(x, y, algo, p);
            Assert.True(sim >= 0.99, $"{algo}: 字节相同的副本相似度应为 1（实际 {sim:F4}）");

            foreach (var i in hs) i.Dispose();
        }

        foreach (var img in set) img.Dispose();
    }

    // ---------- 预筛：无漏报 ----------
    private static void PrefilterTest(DeduplicationService svc, string dir)
    {
        Assert.Section("候选预筛：效率与无漏报");

        foreach (var algo in new[] { AlgorithmType.FileHash, AlgorithmType.AHash,
                                     AlgorithmType.DHash, AlgorithmType.PHash,
                                     AlgorithmType.Histogram, AlgorithmType.Lance })
        {
            var set = svc.ScanImages(dir, "filename");
            var p = Params(algo);

            svc.CalculateFeaturesAsync(set, algo, p).GetAwaiter().GetResult();

            var candidates = CandidatePrefilter.BuildCandidates(set, algo, p);
            long brute = (long)set.Count * (set.Count - 1) / 2;

            var candidateSet = new HashSet<(int, int)>(candidates);
            double reduction = brute == 0 ? 0 : (1 - (double)candidates.Count / brute) * 100;
            Console.WriteLine($"       {algo,-10} 暴力 {brute,7:N0} → 候选 {candidates.Count,7:N0} （-{reduction,5:F1}%）");

            // 暴力找出所有超阈值对，确认全部包含在候选集中（无漏报）
            int missing = 0;
            for (int i = 0; i < set.Count; i++)
            {
                for (int j = i + 1; j < set.Count; j++)
                {
                    double sim = svc.CalculateSimilarityPublic(set[i], set[j], algo, p);
                    if (sim >= p.Threshold && !candidateSet.Contains((i, j)))
                        missing++;
                }
            }

            Assert.Equal(0, missing, $"{algo}: 预筛无漏报");

            foreach (var img in set) img.Dispose();
        }
    }

    // ---------- 内存 ----------
    private static void MemoryTest(DeduplicationService svc, string dir)
    {
        Assert.Section("内存占用");

        var set = svc.ScanImages(dir, "filename");
        svc.CalculateFeaturesAsync(set, AlgorithmType.PHash, new AlgorithmParameters())
            .GetAwaiter().GetResult();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        long managed = GC.GetTotalMemory(true);

        Console.WriteLine($"       {set.Count} 张图特征常驻：{managed / 1024.0:F0} KB");

        // 原实现常驻全分辨率 Mat，仅此一项就需 图片数 × 宽度×高度×3 字节
        Assert.That(managed < 8 * 1024 * 1024,
            $"特征常驻内存远小于图像数据（{managed / 1024.0 / 1024:F2}MB）");

        foreach (var img in set) img.Dispose();
    }
}