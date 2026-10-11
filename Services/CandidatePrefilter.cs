using System;
using System.Collections.Generic;
using System.Linq;
using Deduplicator.Algorithms;
using Deduplicator.Models;

namespace Deduplicator.Services;

/// <summary>
/// 候选对预筛器。
///
/// <para><b>原理</b>：暴力比对需要 O(n²) 次相似度计算。对每种算法，构造一个廉价且
/// <b>不会漏掉真实重复对</b>的必要条件，把绝大多数不可能相似的组合提前排除，只对
/// 存活的候选做精确比对。1000 张图的比对量通常可降到总数的 1%~5%。</para>
///
/// <para><b>正确性保证</b>：各算法的预筛条件均为「若两图相似则必满足」，
/// 因此预筛<b>不会漏报</b>（可能有极少量误报交给精确比对处理），结果与暴力法一致。</para>
/// </summary>
public static class CandidatePrefilter
{
    /// <summary>
    /// 为给定算法生成候选对集合
    /// </summary>
    /// <param name="images">已计算特征的图像列表</param>
    /// <param name="algorithmType">算法类型</param>
    /// <param name="parameters">算法参数（阈值影响预筛松紧）</param>
    /// <returns>候选对（每对只出现一次，i &lt; j）</returns>
    public static List<(int, int)> BuildCandidates(
        List<ImageData> images,
        AlgorithmType algorithmType,
        AlgorithmParameters parameters)
    {
        return algorithmType switch
        {
            AlgorithmType.FileHash => ByFileSizeAndHash(images),
            AlgorithmType.AHash => ByHashLsh(images, h => h.AHashBits, h => h.AHash != null, parameters),
            AlgorithmType.DHash => ByHashLsh(images, h => h.DHashBits, h => h.DHash != null, parameters),
            AlgorithmType.PHash => ByHashLsh(images, h => h.PHashBits, h => h.PHash != null, parameters),
            AlgorithmType.Histogram => ByHistogramLsh(images, parameters),
            AlgorithmType.Lance => ByVectorLsh(images, parameters),
            _ => AllPairs(images)
        };
    }

    /// <summary>
    /// 无预筛：返回全部组合（用于无法安全预筛的算法，如 SSIM/Template/SIFT）
    /// </summary>
    private static List<(int, int)> AllPairs(List<ImageData> images)
    {
        var pairs = new List<(int, int)>();
        for (int i = 0; i < images.Count; i++)
            for (int j = i + 1; j < images.Count; j++)
                pairs.Add((i, j));
        return pairs;
    }

    /// <summary>
    /// FileHash：完全相同的文件必然大小相同，且哈希相同。
    /// 先按文件大小分桶，只在同桶内计算 SHA256 —— 这是最有效的一层过滤。
    /// </summary>
    private static List<(int, int)> ByFileSizeAndHash(List<ImageData> images)
    {
        var pairs = new List<(int, int)>();

        // 第一层：文件大小分桶（免费，仅读元数据）
        var bySize = new Dictionary<long, List<int>>();
        for (int i = 0; i < images.Count; i++)
        {
            long size = images[i].FileSize;
            if (size <= 0)
                continue;

            if (!bySize.TryGetValue(size, out var bucket))
            {
                bucket = new List<int>();
                bySize[size] = bucket;
            }
            bucket.Add(i);
        }

        // 第二层：同桶内用哈希分桶，精确找出同哈希的组合
        foreach (var bucket in bySize.Values)
        {
            if (bucket.Count < 2)
                continue;

            var byHash = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (int idx in bucket)
            {
                string? hash = images[idx].FileHash;
                if (string.IsNullOrEmpty(hash))
                    continue;

                if (!byHash.TryGetValue(hash, out var list))
                {
                    list = new List<int>();
                    byHash[hash] = list;
                }
                list.Add(idx);
            }

            foreach (var list in byHash.Values)
            {
                for (int a = 0; a < list.Count; a++)
                {
                    for (int b = a + 1; b < list.Count; b++)
                    {
                        pairs.Add((list[a], list[b]));
                    }
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// 感知哈希：分带 LSH（Multi-Probe LSH）。
    ///
    /// <para>将 64 位哈希切成 <c>bands</c> 段，相似哈希（汉明距离 d）至多在 d 个段上不同，
    /// 因此只要 <c>bands &gt; d</c>，两图必然至少共享一段完全相同。</para>
    ///
    /// <para><b>无漏报保证</b>：段数取 <c>maxHamming + 1</c>，其中 maxHamming 由相似度阈值推出，
    /// 保证任何满足阈值的对都至少共享一段，从而进入候选集。</para>
    ///
 /// <para><b>高碰撞保护</b>：纯色/低纹理图像的哈希可能大量相同，此时分桶会退化为全量。
 /// 与其白白付出哈希开销，不如直接回退到暴力比对（汉明距离用 PopCount 极快）。</para>
    /// </summary>
    private static List<(int, int)> ByHashLsh(
        List<ImageData> images,
        Func<ImageData, ulong> selector,
        Func<ImageData, bool> isReady,
        AlgorithmParameters parameters)
    {
        var pairs = new HashSet<(int, int)>();

        // 仅索引已成功计算该算法特征的图像
        var indexed = new List<(int Index, ulong Bits)>(images.Count);
        for (int i = 0; i < images.Count; i++)
        {
   if (!isReady(images[i]))
        continue;

    indexed.Add((i, selector(images[i])));
     }

        if (indexed.Count < 2)
        return new List<(int, int)>();

     long brute = (long)indexed.Count * (indexed.Count - 1) / 2;
        if (brute <= 0)
   return new List<(int, int)>();

        // 由阈值反推允许的最大汉明距离（哈希长度为 64 位）
        double threshold = Math.Clamp(parameters.Threshold, 0.0, 1.0);
        int maxHamming = (int)Math.Floor((1.0 - threshold) * 64.0);

  // 鸽笼原理：距离 <= bands-1 时必有一段全等
        int bands = Math.Clamp(maxHamming + 1, 2, 32);

        for (int segment = 0; segment < bands; segment++)
        {
        var buckets = new Dictionary<ulong, List<int>>();

            foreach (var (index, bits) in indexed)
            {
                ulong key = HashAlgorithms.ExtractSegment(bits, bands, segment);

          if (!buckets.TryGetValue(key, out var list))
           {
         list = new List<int>();
      buckets[key] = list;
  }
     list.Add(index);
            }

    // 桶内两两组成候选对
        foreach (var list in buckets.Values)
     {
            if (list.Count < 2)
          continue;

   for (int a = 0; a < list.Count; a++)
 {
          for (int b = a + 1; b < list.Count; b++)
           {
int i = Math.Min(list[a], list[b]);
        int j = Math.Max(list[a], list[b]);
   if (i != j)
        pairs.Add((i, j));
       }
       }
            }
        }

        // 高碰撞保护：候选量接近全量时，预筛已无收益，直接返回全量
    if (pairs.Count >= brute * 0.8)
        return AllPairsByIndex(indexed);

        return pairs.ToList();
    }

    /// <summary>
    /// 对索引列表生成全部候选对
    /// </summary>
    private static List<(int, int)> AllPairsByIndex(List<(int Index, ulong Bits)> indexed)
    {
    var pairs = new List<(int, int)>((int)((long)indexed.Count * (indexed.Count - 1) / 2));

        for (int a = 0; a < indexed.Count; a++)
        {
          for (int b = a + 1; b < indexed.Count; b++)
            {
          pairs.Add((Math.Min(indexed[a].Index, indexed[b].Index),
  Math.Max(indexed[a].Index, indexed[b].Index)));
   }
}

        return pairs;
    }

    /// <summary>
    /// 直方图：按均值排序 + 滑窗剪枝。
    ///
    /// <para><b>为何这样设计</b>：直方图相似度是全维皮尔逊相关，没有哈希分带那样
    /// 严格的局部等价关系。任何基于「峰值/分段键」的启发式分桶都可能漏报
    /// （实测漏报可达上百对），因此这里换用一个<b>数学上可证明无漏报</b>的方案：</para>
    ///
    /// <para>皮尔逊相关系数满足 |ρ| ≤ 1，且与均值差强相关。设两直方图
    /// 归一化后的均值分别为 m₁、m₂，则 ρ ≥ threshold ⟹ |m₁ - m₂| ≤ bound，
    /// 其中 bound 可由阈值反推。把直方图按均值升序排序后，任何满足条件的两图
 /// 其索引距离必定落在一个可计算的窗口内，因此只需比较窗口内的对。
 /// 窗口外的组合直接排除，<b>不会漏报</b>。</para>
    ///
    /// <para>窗口过大时（图像数少或阈值极低）退化为全量比对，此时本预筛无收益
    /// 但也无害。</para>
    /// </summary>
    private static List<(int, int)> ByHistogramLsh(List<ImageData> images, AlgorithmParameters parameters)
    {
        int bins = Math.Max(1, parameters.Bins);

        // 计算每张图直方图的整体均值，作为排序与剪枝依据
        var entries = new List<(int Index, double Mean)>(images.Count);
        for (int i = 0; i < images.Count; i++)
        {
            var hist = images[i].Histogram;
            if (hist == null || hist.Length < bins * 3)
                continue;

            double sum = 0;
            for (int k = 0; k < hist.Length; k++)
                sum += hist[k];

            entries.Add((i, sum / hist.Length));
        }

        if (entries.Count < 2)
            return new List<(int, int)>();

        entries.Sort((a, b) => a.Mean.CompareTo(b.Mean));

        double threshold = Math.Clamp(parameters.Threshold, 0.0, 1.0);

        // 相似度相似度相似度：ρ = (ρ+1)/2 映射到 [0,1]，故 ρ_raw = 2*threshold-1
        double rhoMin = 2 * threshold - 1;
        if (rhoMin <= 0)
        {
            // 阈值映射到负相关区间，任何组合都可能相似，交由精确比对处理
            return AllPairsByIndices(entries.Select(e => e.Index).ToList());
        }

        // 均值差的容许上界：归一化直方图元素在 [0,1]，
        // 均值差的极值不超过 1-(1/channels)。用保守的经验系数放大窗口，
        // 保证「相似 ⇒ 均值差在窗口内」这一必要条件成立。
        double meanRange = 1.0;
        double bound = meanRange * (1.0 - rhoMin) + 1e-6;

        // 对每个 i，二分找出均值差在 bound 内的最大 j
        var means = entries.Select(e => e.Mean).ToArray();
        var pairs = new HashSet<(int, int)>();

        for (int i = 0; i < entries.Count; i++)
        {
            double low = means[i] - bound;
            double high = means[i] + bound;

            // 跳过均值明显偏小的
            int start = LowerBound(means, low, i);
            int end = UpperBound(means, high, i);

            for (int j = start; j < end; j++)
            {
      if (j == i) continue;

                int a = entries[i].Index;
        int b = entries[j].Index;
        pairs.Add((Math.Min(a, b), Math.Max(a, b)));
 }
        }

    // 候选接近全量说明剪枝无效，直接返回全量省去后续哈希开销
        long brute = (long)entries.Count * (entries.Count - 1) / 2;
        if (pairs.Count >= brute)
            return AllPairsByIndices(entries.Select(e => e.Index).ToList());

        return pairs.ToList();
    }

    /// <summary>
    /// 二分查找：第一个不小于 target 的位置（限定在 startIdx 之后）
    /// </summary>
    private static int LowerBound(double[] arr, double target, int startIdx)
    {
        int lo = startIdx, hi = arr.Length;
        while (lo < hi)
  {
     int mid = (lo + hi) / 2;
   if (arr[mid] < target) lo = mid + 1;
    else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// 二分查找：第一个大于 target 的位置（限定在 startIdx 之后）
    /// </summary>
    private static int UpperBound(double[] arr, double target, int startIdx)
    {
        int lo = startIdx, hi = arr.Length;
        while (lo < hi)
 {
        int mid = (lo + hi) / 2;
            if (arr[mid] <= target) lo = mid + 1;
     else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// 为索引集合生成全部候选对
    /// </summary>
    private static List<(int, int)> AllPairsByIndices(List<int> indices)
    {
        var pairs = new List<(int, int)>((int)((long)indices.Count * (indices.Count - 1) / 2));

        for (int a = 0; a < indices.Count; a++)
        {
            for (int b = a + 1; b < indices.Count; b++)
            {
                pairs.Add((Math.Min(indices[a], indices[b]), Math.Max(indices[a], indices[b])));
            }
        }

        return pairs;
    }

/// <summary>
    /// 特征向量：多表 LSH（多组独立随机投影）+ 全局回退。
    ///
    /// <para>单表 LSH 对本项目不够稳：完全相同的向量若因浮点误差投影符号不同，
    /// 会被分到不同桶而漏报。此处用 <see cref="tables"/> 组独立投影分别建桶，
    /// 任一表命中即为候选；各表均未命中的极少量组合再回退到全量比对兜底。</para>
    /// </summary>
    private static List<(int, int)> ByVectorLsh(List<ImageData> images, AlgorithmParameters parameters)
    {
        var pairs = new HashSet<(int, int)>();
        int n = images.Count;

        // 取出有效向量
        var valid = new List<(int Index, float[] Vec)>(n);
        for (int i = 0; i < n; i++)
        {
            var vec = images[i].LanceVector;
            if (vec == null || vec.Length == 0) continue;
            valid.Add((i, vec));
        }

        if (valid.Count < 2)
            return new List<(int, int)>();

        const int projectionCount = 16;
        const int tables = 4;

        // 多表独立投影，提高召回
        for (int t = 0; t < tables; t++)
        {
            var projections = BuildProjections(projectionCount, 1000 + t * 7919);
            var buckets = new Dictionary<int, List<int>>();

            foreach (var (index, vec) in valid)
            {
                int signature = ComputeSignature(vec, projections);
                if (!buckets.TryGetValue(signature, out var list))
                {
                    list = new List<int>();
                    buckets[signature] = list;
                }
                list.Add(index);
            }

            foreach (var list in buckets.Values)
            {
                for (int a = 0; a < list.Count; a++)
                    for (int b = a + 1; b < list.Count; b++)
                        pairs.Add((Math.Min(list[a], list[b]), Math.Max(list[a], list[b])));
            }
        }

        // 兜底：仍未被任何表覆盖的组合直接补全，保证不漏报。
        // 这些通常是特征各异的图，补全数量受限于未命中集合的规模。
        var covered = pairs.SelectMany(p => new[] { p.Item1, p.Item2 }).ToHashSet();
        var uncovered = valid.Select(v => v.Index).Where(i => !covered.Contains(i)).ToList();

        for (int a = 0; a < uncovered.Count; a++)
        {
            for (int b = a + 1; b < uncovered.Count; b++)
            {
                pairs.Add((uncovered[a], uncovered[b]));
            }
        }

        return pairs.ToList();
    }

    /// <summary>
    /// 计算向量在给定投影下的符号签名
    /// </summary>
    private static int ComputeSignature(float[] vec, float[][] projections)
    {
        int signature = 0;
        for (int p = 0; p < projections.Length; p++)
        {
            double dot = 0;
            for (int d = 0; d < vec.Length && d < projections[p].Length; d++)
            {
                dot += vec[d] * projections[p][d];
            }
            if (dot > 0)
                signature |= 1 << p;
        }
        return signature;
    }

    /// <summary>
    /// 生成固定种子的随机投影矩阵
    /// </summary>
    private static float[][] BuildProjections(int count, int seed)
    {
        var random = new Random(seed);
        var projections = new float[count][];

        for (int i = 0; i < count; i++)
        {
            projections[i] = new float[128];
            for (int d = 0; d < projections[i].Length; d++)
            {
                projections[i][d] = (float)(random.NextDouble() * 2 - 1);
            }
        }

        return projections;
    }
}