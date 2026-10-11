using System;
using System.Numerics;
using OpenCvSharp;

namespace Deduplicator.Algorithms;

/// <summary>
/// 感知哈希算法（AHash / DHash / PHash）
///
/// 约定：所有 Compute* 方法返回固定长度的 0/1 字节数组，长度为 hashSize * hashSize。
/// 为了支持快速预筛（LSH），另提供 <see cref="ToUInt64"/> 将哈希打包为无符号整数；
/// 当 hashSize 为 8（64 位）时可无损打包，此时可直接使用 <see cref="HammingDistance(ulong, ulong)"/>。
/// </summary>
public static class HashAlgorithms
{
    /// <summary>
    /// 灰度化（若已是单通道则克隆，避免释放调用方持有的 Mat）
    /// </summary>
    private static Mat ToGray(Mat image, out bool ownsGray)
    {
        if (image.Channels() == 1)
        {
            ownsGray = false;
            return image;
        }
        ownsGray = true;
        return image.CvtColor(ColorConversionCodes.BGR2GRAY);
    }

    /// <summary>
    /// 感知哈希（PHash）：基于 DCT 低频系数，对亮度变化鲁棒
    /// </summary>
    public static byte[] ComputePHash(Mat image, int hashSize = 8)
    {
        if (hashSize <= 0) hashSize = 8;

        if (image == null || image.Empty())
            return new byte[hashSize * hashSize];

        Mat gray = ToGray(image, out bool ownsGray);
        using var resized = gray.Resize(new Size(hashSize + 1, hashSize + 1), interpolation: InterpolationFlags.Linear);
        if (ownsGray) gray.Dispose();

        using var floatMat = new Mat();
        resized.ConvertTo(floatMat, MatType.CV_32F);

        using var dct = new Mat();
        Cv2.Dct(floatMat, dct);

        using var dctLow = dct[new Rect(0, 0, hashSize, hashSize)];

        double mean = Cv2.Mean(dctLow).Val0;
        var phash = new byte[hashSize * hashSize];
        int index = 0;

        for (int i = 0; i < hashSize; i++)
        {
            for (int j = 0; j < hashSize; j++)
            {
                phash[index++] = dctLow.At<float>(i, j) > mean ? (byte)1 : (byte)0;
            }
        }

        return phash;
    }

    /// <summary>
    /// 均值哈希（AHash）：最简单，对噪声敏感
    /// </summary>
    public static byte[] ComputeAHash(Mat image, int hashSize = 8)
    {
        if (hashSize <= 0) hashSize = 8;

        if (image == null || image.Empty())
            return new byte[hashSize * hashSize];

        Mat gray = ToGray(image, out bool ownsGray);
        using var resized = gray.Resize(new Size(hashSize, hashSize), interpolation: InterpolationFlags.Linear);
        if (ownsGray) gray.Dispose();

        Scalar mean = Cv2.Mean(resized);
        double avg = mean.Val0;
        var ahash = new byte[hashSize * hashSize];
        int index = 0;

        for (int i = 0; i < hashSize; i++)
        {
            for (int j = 0; j < hashSize; j++)
            {
                ahash[index++] = resized.At<byte>(i, j) > avg ? (byte)1 : (byte)0;
            }
        }

        return ahash;
    }

    /// <summary>
    /// 差值哈希（DHash）：基于相邻像素梯度，对亮度/对比度变化鲁棒
    /// </summary>
    public static byte[] ComputeDHash(Mat image, int hashSize = 8)
    {
        if (hashSize <= 0) hashSize = 8;

        if (image == null || image.Empty())
            return new byte[hashSize * hashSize];

        Mat gray = ToGray(image, out bool ownsGray);
        using var resized = gray.Resize(new Size(hashSize + 1, hashSize), interpolation: InterpolationFlags.Linear);
        if (ownsGray) gray.Dispose();

        var dhash = new byte[hashSize * hashSize];
        int index = 0;

        for (int i = 0; i < hashSize; i++)
        {
            for (int j = 0; j < hashSize; j++)
            {
                dhash[index++] = resized.At<byte>(i, j + 1) > resized.At<byte>(i, j) ? (byte)1 : (byte)0;
            }
        }

        return dhash;
    }

    /// <summary>
    /// 计算两个哈希的汉明距离（0/1 数组版本）
    /// </summary>
    /// <returns>不同位的数量；长度不一致时返回 <see cref="int.MaxValue"/>（视为完全不相似）
    /// </returns>
    public static int HammingDistance(byte[]? hash1, byte[]? hash2)
    {
        if (hash1 == null || hash2 == null || hash1.Length == 0 || hash1.Length != hash2.Length)
            return int.MaxValue;

        int distance = 0;
        for (int i = 0; i < hash1.Length; i++)
        {
            if (hash1[i] != hash2[i])
                distance++;
        }

        return distance;
    }

    /// <summary>
    /// 将 0/1 字节数组打包为无符号整数（长度超过 64 时截断，仅用于预筛）
    /// </summary>
    public static ulong ToUInt64(byte[]? hash)
    {
        if (hash == null)
            return 0UL;

        ulong value = 0UL;
        int count = Math.Min(hash.Length, 64);
        for (int i = 0; i < count; i++)
        {
            if (hash[i] != 0)
                value |= 1UL << i;
        }
        return value;
    }

    /// <summary>
    /// 快速汉明距离（利用 CPU 单指令多比特计数，约为朴素循环的 10 倍速度）
    /// </summary>
    public static int HammingDistance(ulong a, ulong b)
    {
        return BitOperations.PopCount(a ^ b);
    }

/// <summary>
    /// 计算代理感知哈希（用于慢速算法的预筛）。
    /// </summary>
    /// <remarks>
    /// <para>做法：把图像缩放到 8×8 灰度网格，以网格均值为基准生成 64 位哈希。</para>
    /// <para>与 DHash/PHash 的区别在于<b>目的不同</b>：这里不需要最强的区分度，
    /// 而需要「结构相似 ⟹ 哈希接近」这一<b>单向</b>保证，用作预筛的必要条件。
    /// 因此刻意选更粗的网格（8×8），使噪声影响被平均掉，
    /// 保证真正相似的图必然落在相近的哈希邻域内。</para>
    /// </remarks>
    /// <param name="grayImage">已灰度化的图像</param>
    /// <param name="gridSize">网格边长（默认 8，产生 64 位哈希）</param>
    public static ulong ComputeProxyHash(Mat grayImage, int gridSize = 8)
    {
        if (grayImage == null || grayImage.Empty() || gridSize <= 0)
            return 0UL;

        try
        {
            using var small = grayImage.Resize(
        new Size(gridSize, gridSize),
   interpolation: InterpolationFlags.Area);

      Scalar mean = Cv2.Mean(small);
            double avg = mean.Val0;

            ulong hash = 0UL;
            int bit = 0;

            for (int y = 0; y < gridSize && bit < 64; y++)
            {
      for (int x = 0; x < gridSize && bit < 64; x++)
       {
                    if (small.At<byte>(y, x) > avg)
     hash |= 1UL << bit;

   bit++;
      }
            }

    return hash;
        }
        catch (Exception ex)
        {
    System.Diagnostics.Debug.WriteLine($"代理哈希计算失败: {ex.Message}");
      return 0UL;
        }
    }

    /// <summary>
    /// 计算图像的平均亮度与对比度（归一化到 0-1），用于极廉价的一维剪枝
    /// </summary>
    public static (double mean, double contrast) ComputeLumaStats(Mat grayImage)
    {
        if (grayImage == null || grayImage.Empty())
  return (0, 0);

        try
        {
            using var small = grayImage.Resize(new Size(64, 64), interpolation: InterpolationFlags.Area);

   using var meanMat = new Mat();
   using var stdMat = new Mat();
        Cv2.MeanStdDev(small, meanMat, stdMat);

            double mean = meanMat.At<double>(0, 0) / 255.0;
    double std = stdMat.At<double>(0, 0) / 255.0;

   return (Math.Clamp(mean, 0, 1), Math.Clamp(std, 0, 1));
        }
  catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"亮度统计失败: {ex.Message}");
     return (0, 0);
        }
    }

    /// <summary>
    /// 将哈希按位切分为若干段，用于 LSH 分桶预筛
    /// </summary>
    /// <remarks>
    /// <b>关键</b>：各段必须<b>完整覆盖全部 64 位</b>。若段数与每段位数不自洽
/// （例如 10 段 × 6 位 = 60 位），末尾若干位将永远不参与索引，
/// 而图像间的差异恰好落在这些位时会被漏判为不相似。
    /// 因此这里按 <c>shift = index * 64 / segments</c> 精确划分，
    /// 保证第 i 段覆盖 [i*64/segments, (i+1)*64/segments) 区间。
    /// </remarks>
    /// <param name="hash">打包后的哈希</param>
    /// <param name="segments">切分段数</param>
    /// <param name="segmentIndex">段序号（从 0 开始）</param>
    public static ulong ExtractSegment(ulong hash, int segments, int segmentIndex)
    {
        if (segments <= 1)
            return hash;

        if (segmentIndex < 0 || segmentIndex >= segments)
            return 0UL;

        int start = (int)((long)segmentIndex * 64 / segments);
        int end = (int)((long)(segmentIndex + 1) * 64 / segments);

        if (end <= start)
            return 0UL;

        int bits = end - start;
     ulong mask = bits >= 64 ? ulong.MaxValue : (1UL << bits) - 1;

        return (hash >> start) & mask;
    }
}