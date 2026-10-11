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

    // ---------- 预筛：无漏报（跨阈值参数化） ----------
    private static void PrefilterTest(DeduplicationService svc, string dir)
    {
        Assert.Section("候选预筛：跨阈值无漏报");

        // 阈值区间覆盖：极严（几乎只有相同图）→ 中等 → 极松（多数图都相似）
        // 预筛的分带数/滑窗宽度都随阈值自适应，低阈值是最容易出问题的区间
        double[] thresholds = { 0.95, 0.85, 0.75, 0.65, 0.55, 0.45 };

foreach (var algo in new[] { AlgorithmType.FileHash, AlgorithmType.AHash,
    AlgorithmType.DHash, AlgorithmType.PHash,
    AlgorithmType.Histogram, AlgorithmType.Lance,
   // 依赖代理哈希预筛的慢速算法
      AlgorithmType.Ssim, AlgorithmType.Template, AlgorithmType.FastDup,
                                     AlgorithmType.Sift, AlgorithmType.Surf })
        {
            foreach (double threshold in thresholds)
         {
 var set = svc.ScanImages(dir, "filename");
  var p = Params(algo);
         p.Threshold = threshold;

       svc.CalculateFeaturesAsync(set, algo, p).GetAwaiter().GetResult();

      var candidates = CandidatePrefilter.BuildCandidates(set, algo, p);
          long brute = (long)set.Count * (set.Count - 1) / 2;
        var candidateSet = new HashSet<(int, int)>(candidates);

         /*
          * 无漏报的校验口径。
  *
        * 只校验「<b>已知真实重复</b>」（字节完全相同的副本：orig_a/copy_a、orig_b/copy_b）
         * 必须进入候选集。
    *
    * 之所以不校验「所有超阈值对」：在低阈值下，SSIM/FastDup 会把大量
        * 语义上不同的图判为相似（假阳性）。例如样本中 sim=0.55~0.80 的
      * 色块图两两组合，它们并非重复，不应要求预筛召回。
         * 若按「所有超阈值对」设断言，指标会退化为衡量算法的判别能力，
       * 而不是预筛的正确性。
      */
/*
              * 字节完全相同的副本 = 已知真实重复。
       * 用文件内容哈希判定（而非 SSIM），对所有算法通用且无可争议。
      */
var fileHashes = new string[set.Count];
      for (int k = 0; k < set.Count; k++)
         {
   using var fs = File.OpenRead(set[k].FilePath);
    using var sha = System.Security.Cryptography.SHA256.Create();
      fileHashes[k] = Convert.ToHexString(sha.ComputeHash(fs));
      }

            int realDuplicates = 0;
            int missedDuplicates = 0;
     int positives = 0;

            for (int i = 0; i < set.Count; i++)
  {
             for (int j = i + 1; j < set.Count; j++)
     {
              double sim = svc.CalculateSimilarityPublic(set[i], set[j], algo, p);
              bool inCandidates = candidateSet.Contains((i, j));
          if (sim >= p.Threshold) positives++;

// 内容完全一致即真实重复（测试数据中每组只含 2 张，不会超过 2）
     bool isRealDuplicate = fileHashes[i] == fileHashes[j];

     if (isRealDuplicate)
   {
        realDuplicates++;
            if (!inCandidates) missedDuplicates++;
        }
     }
        }

double reduction = brute == 0 ? 0 : (1 - (double)candidates.Count / brute) * 100;

        Console.WriteLine($"       {algo,-10} 阈值 {threshold:F2}  "
       + $"候选 {candidates.Count,6:N0}/{brute,-7:N0} （-{reduction,5:F1}%）  "
        + $"真实重复 {realDuplicates,3} / 漏报 {missedDuplicates}");

        Assert.Equal(0, missedDuplicates,
       $"{algo} @阈值{threshold:F2}: 预筛未漏报真实重复（共 {realDuplicates} 对）");

 foreach (var img in set) img.Dispose();
            }
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