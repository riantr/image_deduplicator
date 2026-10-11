using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using OpenCvSharp.Features2D;

namespace Deduplicator.Algorithms;

/// <summary>
/// 局部特征算法（SIFT / ORB）与全局描述子（Lance/HOG）
/// </summary>
public static class FeatureAlgorithms
{
    /// <summary>
    /// Lowe ratio test：好的匹配应当明显优于次优匹配
    /// </summary>
    private const double LoweRatio = 0.75;

    /// <summary>
    /// 灰度化（若已是单通道则复用，避免释放调用方持有的 Mat）
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
    /// 计算 SIFT 特征
    /// </summary>
    /// <param name="maxFeatures">最大特征点数（&lt;= 0 表示不限制）</param>
    public static (KeyPoint[]? keyPoints, Mat? descriptors) ComputeSiftFeatures(Mat image, int maxFeatures = 500)
  {
        if (image == null || image.Empty())
  return (null, null);

        try
        {
            Mat gray = ToGray(image, out bool ownsGray);
     try
    {
     /*
        * 工作尺寸：限制最长边。
      *
        * 实测（60 张 400×300 图，单张耗时）：
        *   原始 400 → 17.68ms   320 → 9.82ms   256 → 8.90ms
         * 而相似度质量在 400/320/256 下**完全一致**
    *（重复对恒为 1.0000，独立图恒为 0.0000）。
      *
        * 因此取 256 作为工作尺寸：开销降低约 50%，而对去重结果无影响。
      * 详见 Deduplicator.Tests 的 --quality 工具。
              */
      Mat? downscaled = null;
  Mat work = gray;

    int longest = Math.Max(gray.Rows, gray.Cols);
         const int MaxWorkingSize = 256;

         if (longest > MaxWorkingSize)
{
       double scale = (double)MaxWorkingSize / longest;
          int newW = Math.Max(1, (int)(gray.Cols * scale));
        int newH = Math.Max(1, (int)(gray.Rows * scale));
       downscaled = gray.Resize(new OpenCvSharp.Size(newW, newH),
         interpolation: InterpolationFlags.Area);
      work = downscaled;
      }

      try
      {
        int nFeatures = maxFeatures > 0 ? maxFeatures : 0;

        using var sift = SIFT.Create(nFeatures, 3, 0.04, 10, 1.6);
        KeyPoint[]? keyPoints;
  using var descriptors = new Mat();
  sift.DetectAndCompute(work, null, out keyPoints, descriptors);

          if (descriptors.Empty() || keyPoints == null || keyPoints.Length == 0)
  {
   // 首次未提取到特征时放宽 contrastThreshold 再试一次
      using var relaxed = SIFT.Create(nFeatures, 3, 0.01, 10, 1.6);
     relaxed.DetectAndCompute(work, null, out keyPoints, descriptors);
        }

  if (descriptors.Empty() || keyPoints == null || keyPoints.Length == 0)
    {
      // 平滑图像（如纯色渐变）本就无角点，返回空特征交由上层降级处理
     return (null, null);
     }

        /*
      * 关键：descriptors 由 using 管理，返回前必须深拷贝。
  * 若直接交出 using 变量的引用，方法返回时它会被释放，
         * 调用方拿到的就是悬垂指针 —— 访问时直接触发 AccessViolationException。
         */
            return (keyPoints, descriptors.Clone());
   }
      finally
     {
  downscaled?.Dispose();
    }
    }
   finally
     {
 if (ownsGray) gray.Dispose();
    }
        }
        catch (Exception ex)
        {
         System.Diagnostics.Debug.WriteLine($"SIFT feature computation error: {ex.Message}");
    return (null, null);
        }
    }

    /// <summary>
    /// 计算 ORB 特征（原代码中 Surf 分支实际调用的是 ORB）
    /// </summary>
public static (KeyPoint[]? keyPoints, Mat? descriptors) ComputeOrbFeatures(Mat image, int maxFeatures = 500)
    {
     if (image == null || image.Empty())
            return (null, null);

try
        {
    Mat gray = ToGray(image, out bool ownsGray);
        try
        {
       /*
        * 工作尺寸：限制最长边到 256。
         *
    * 实测数据（60 张 400×300 图）：
         *   原始 400 → 7.43ms   320 → 5.52ms   256 → 4.00ms
         * 而相似度质量在 400/320/256 下完全一致（--quality 工具验证）。
       *
    * 早期版本是把小图「放大」到 480，理由是「ORB 对尺度敏感」。
       * 实测证明该操作对 400×300 的输入毫无收益（10.92 vs 10.70ms），
         * 纯属浪费；真正有效的是限制上限。
         */
      Mat? resized = null;
      Mat work = gray;

            int longest = Math.Max(gray.Rows, gray.Cols);
            const int MaxWorkingSize = 256;

            if (longest > MaxWorkingSize)
     {
       double scale = (double)MaxWorkingSize / longest;
       int newW = Math.Max(1, (int)(gray.Cols * scale));
        int newH = Math.Max(1, (int)(gray.Rows * scale));
      resized = gray.Resize(new OpenCvSharp.Size(newW, newH),
       interpolation: InterpolationFlags.Area);
                work = resized;
        }

    try
      {
    using var orb = ORB.Create();
        if (maxFeatures > 0)
          orb.MaxFeatures = maxFeatures;
   orb.EdgeThreshold = 15;   // 放宽边缘阈值，小图/低对比图也能提取特征
       orb.PatchSize = 15;
            orb.FastThreshold = 5;

            KeyPoint[]? keyPoints;
      using var descriptors = new Mat();
    orb.DetectAndCompute(work, null, out keyPoints, descriptors);

       if (descriptors.Empty() || keyPoints == null || keyPoints.Length == 0)
            {
  // 放宽 FastThreshold 再试一次
            using var relaxed = ORB.Create();
           if (maxFeatures > 0) relaxed.MaxFeatures = maxFeatures;
        relaxed.EdgeThreshold = 5;
    relaxed.PatchSize = 15;
         relaxed.FastThreshold = 1;
        relaxed.DetectAndCompute(work, null, out keyPoints, descriptors);
        }

  if (descriptors.Empty() || keyPoints == null || keyPoints.Length == 0)
    return (null, null);

         // 同上：using 管理的 Mat 必须克隆后才能交出所有权
            return (keyPoints, descriptors.Clone());
   }
       finally
  {
        resized?.Dispose();
     }
     }
  finally
        {
            if (ownsGray) gray.Dispose();
        }
        }
        catch (Exception ex)
    {
      System.Diagnostics.Debug.WriteLine($"ORB feature computation error: {ex.Message}");
            return (null, null);
        }
    }

/// <summary>
    /// 基于匹配数量与匹配质量计算相似度
    /// </summary>
    private static double ScoreMatches(List<DMatch> goodMatches, int minFeatures, double avgDistance, double distanceCeiling)
    {
   if (minFeatures <= 0 || distanceCeiling <= 0)
            return 0;

        double matchRatio = (double)goodMatches.Count / minFeatures;
        double distanceScore = Math.Max(0, 1 - avgDistance / distanceCeiling);
        double similarity = matchRatio * 0.6 + distanceScore * 0.4;

        return Math.Min(1.0, similarity * 1.5);
    }

    /// <summary>
    /// 特征点数差异过大时直接判定为不相似，避免无意义的昂贵匹配。
    ///
    /// <para>图像内容差异大时，ORB/SIFT 提取到的特征点数往往相差悬殊。
    /// 先做这道廉价剪枝，可省下大量 BF/FLANN 匹配开销。
    /// 注意阈值较宽松（0.2），仅剪掉明显不可能的两图，不影响召回。</para>
 /// </summary>
    private static bool FeatureCountMismatch(int rows1, int rows2)
    {
        int min = Math.Min(rows1, rows2);
   int max = Math.Max(rows1, rows2);

        if (min <= 0) return true;

     return max > min * 5;
    }

    /// <summary>
    /// 应用 Lowe ratio test
    /// </summary>
    private static List<DMatch> RatioTest(DMatch[][] matches)
    {
        var goodMatches = new List<DMatch>(matches.Length);
        for (int i = 0; i < matches.Length; i++)
        {
            if (matches[i].Length >= 2 && matches[i][0].Distance < LoweRatio * matches[i][1].Distance)
                goodMatches.Add(matches[i][0]);
        }
        return goodMatches;
    }

    /// <summary>
    /// SIFT 特征相似度（基于 FLANN 匹配 + ratio test）
    /// </summary>
    public static double SiftSimilarity(KeyPoint[]? kp1, Mat? des1, KeyPoint[]? kp2, Mat? des2, double threshold = 0.6, int minMatch = 6)
    {
if (des1 == null || des2 == null || des1.Empty() || des2.Empty() || des1.Rows < 2 || des2.Rows < 2)
         return 0;

        if (FeatureCountMismatch(des1.Rows, des2.Rows))
            return 0;

        try
   {
            using var flann = new FlannBasedMatcher();
            DMatch[][] matches = flann.KnnMatch(des1, des2, k: 2);

            var goodMatches = RatioTest(matches);
            if (goodMatches.Count == 0)
                return 0;

            int minFeatures = Math.Min(des1.Rows, des2.Rows);
            int actualMinMatch = Math.Max(3, Math.Min(minMatch, minFeatures));

            if (goodMatches.Count < actualMinMatch)
                return 0;

            double avgDistance = goodMatches.Average(m => m.Distance);
            return ScoreMatches(goodMatches, minFeatures, avgDistance, 300);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SIFT similarity error: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// ORB 特征相似度（基于 BF/Hamming 匹配 + ratio test）
    /// </summary>
public static double OrbSimilarity(KeyPoint[]? kp1, Mat? des1, KeyPoint[]? kp2, Mat? des2, double threshold = 0.6, int minMatch = 6)
    {
  if (des1 == null || des2 == null || des1.Empty() || des2.Empty() || des1.Rows < 2 || des2.Rows < 2)
      return 0;

        if (FeatureCountMismatch(des1.Rows, des2.Rows))
     return 0;

        try
     {
 using var bf = new BFMatcher(NormTypes.Hamming, crossCheck: false);
  DMatch[][] matches = bf.KnnMatch(des1, des2, k: 2);

     var goodMatches = RatioTest(matches);
      if (goodMatches.Count == 0)
      return 0;

            int minFeatures = Math.Min(des1.Rows, des2.Rows);
            int actualMinMatch = Math.Max(3, Math.Min(minMatch, minFeatures / 2));

      if (goodMatches.Count < actualMinMatch)
          return 0;

        double avgDistance = goodMatches.Average(m => m.Distance);
   // ORB 汉明距离典型范围 0-255（32 字节描述子）
  return ScoreMatches(goodMatches, minFeatures, avgDistance, 128);
        }
        catch (Exception ex)
        {
     System.Diagnostics.Debug.WriteLine($"ORB similarity error: {ex.Message}");
      return 0;
  }
    }

    /// <summary>
    /// 计算 Lance/HOG 全局描述子
    /// </summary>
    /// <remarks>
    /// 尺寸换算说明：HOG 要求 winSize 宽高均为 8 的倍数，且不小于一个 block（16x16）。
    /// 原实现直接用 gray.Cols/8*8 作为宽度，当图像较小时可能得到非法尺寸而抛异常。
    /// </remarks>
    public static float[] ComputeLanceVector(Mat image, int dim = 128)
    {
        if (dim <= 0) dim = 128;
        if (image == null || image.Empty())
            return new float[dim];

        Mat? resized = null;
        try
        {
            // 限制最大边长，兼顾速度与描述子稳定性
            int h = image.Rows;
            int w = image.Cols;
            int targetSize = Math.Max(h, w);

            if (targetSize > 256)
            {
                double scale = 256.0 / targetSize;
                int newH = Math.Max(1, (int)(h * scale));
                int newW = Math.Max(1, (int)(w * scale));
                resized = image.Resize(new Size(newW, newH));
                h = resized.Rows;
                w = resized.Cols;
            }

            Mat gray = ToGray(resized ?? image, out bool ownsGray);
            try
            {
                // HOG 的 winSize 宽高需为 8 的倍数，且至少容纳一个 16x16 block
                int winW = Math.Max(16, w / 8 * 8);
                int winH = Math.Max(16, h / 8 * 8);

                using var hog = new HOGDescriptor(
                    new Size(winW, winH),
                    new Size(16, 16),
                    new Size(8, 8),
                    new Size(8, 8),
                    9);

float[] features = hog.Compute(gray);
       if (features == null || features.Length == 0)
      return new float[dim];

      float[] result;
        if (features.Length > dim)
       {
    /*
   * 原实现取「绝对值最大的 dim 维」，这会丢弃结构信息、导致不同图像的描述子
        * 也高度相似（实测 cosine ≈ 0.99998），使去重完全失效。
   * 改用 PCA 投影到低维：保留方差最大的主成分，兼顾区分度与维度。
  */
          result = DimensionReducer.Project(features, dim);
        }
        else if (features.Length < dim)
        {
    result = new float[dim];
   Array.Copy(features, result, features.Length);
   }
   else
        {
            result = features;
        }

                double sumSquares = 0;
                for (int i = 0; i < result.Length; i++)
                    sumSquares += result[i] * result[i];
                double norm = Math.Sqrt(sumSquares);

                if (norm > 0)
                {
                    for (int i = 0; i < result.Length; i++)
                        result[i] /= (float)norm;
                }

                return result;
            }
            finally
            {
                if (ownsGray) gray.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Lance vector computation error: {ex.Message}");
            return new float[dim];
        }
        finally
        {
            resized?.Dispose();
        }
    }
}