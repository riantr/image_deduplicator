using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Deduplicator.Algorithms;
using Deduplicator.Models;
using Deduplicator.Services;
using OpenCvSharp;

namespace Deduplicator.Tests;

/// <summary>
/// Lance/HOG 特征诊断。
///
/// <para><b>用途</b>：验证 HOG 工作尺寸能否缩小以提速。
/// 结论是<b>不能</b>——详见 QualityCheck 的实测数据。
/// 保留此工具是为了避免后人重复尝试同一个已被证伪的优化。</para>
///
/// <para><b>实测结论</b>（60 张 400×300 图）：
/// <code>
/// 尺寸  特征耗时   重复对    独立图相似度
/// 256    4.53ms    1.0000    0.8074
/// 192    2.62ms    1.0000    0.8493   ← 贴到 0.85 阈值
/// 160    2.6ms  1.0000    0.8744
/// 128    2.4ms     1.0000    0.8967
/// </code>
/// 缩小后重复对仍为 1.0（看似无损），但独立图相似度单调上升。
/// 缩到 192 时实测检出组数从 11 组涨到 16 组 —— 多出的 5 组全是误报。</para>
///
/// <para><b>原因</b>：HOG 描述子的区分度依赖分辨率。
/// 缩小会让梯度直方图趋于平滑，不同图像的特征互相靠拢。
/// 与 SIFT/ORB 不同 —— 那两个算法对缩小不敏感，所以能放心从 400 降到 256。</para>
/// </summary>
public static class HogCheck
{
    public static void Run(string dir)
    {
        var files = Directory.GetFiles(dir, "*.png");

        Console.WriteLine("=== HOG 特征耗时（按工作尺寸）===");
        foreach (int maxSize in new[] { 256, 192, 128, 96 })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            foreach (var f in files.Take(60))
            {
                using var m = Cv2.ImRead(f);
                if (m.Empty()) continue;
                using var g = m.Channels() == 1 ? m.Clone() : m.CvtColor(ColorConversionCodes.BGR2GRAY);

                // 模拟 Lance 的缩放逻辑
                Mat? resized = null;
                int h = g.Rows, w = g.Cols;
                if (Math.Max(h, w) > maxSize)
                {
                    double scale = (double)maxSize / Math.Max(h, w);
                    resized = g.Resize(new Size((int)(w * scale), (int)(h * scale)));
                    h = resized.Rows; w = resized.Cols;
                }

                int winW = Math.Max(16, w / 8 * 8);
                int winH = Math.Max(16, h / 8 * 8);
                using var hog = new HOGDescriptor(
                    new Size(winW, winH), new Size(16, 16),
                    new Size(8, 8), new Size(8, 8), 9);
                hog.Compute(resized ?? g);
                n++;
                resized?.Dispose();
            }
            sw.Stop();
            Console.WriteLine($"  maxSize={maxSize,4}  {sw.Elapsed.TotalMilliseconds,8:F0}ms / {n} 张  单张 {sw.Elapsed.TotalMilliseconds / n,6:F2}ms");
        }

        Console.WriteLine("\n=== 不同尺寸图像的 HOG 特征维度 ===");
        var log = new LogService { EnableFileLog = false };
        var dedup = new DeduplicationService(log);
        var set = dedup.ScanImages(dir, "filename");
        dedup.CalculateFeaturesAsync(set, AlgorithmType.Lance, new AlgorithmParameters()).GetAwaiter().GetResult();

        var dims = set.Where(x => x.LanceVector != null)
                      .Select(x => x.LanceVector!.Length)
                      .GroupBy(d => d)
                      .Select(g => new { Dim = g.Key, Count = g.Count() })
                      .OrderBy(x => x.Dim)
                      .ToList();

        Console.WriteLine($"  Lance 目标维度 = 256，实际维度分布:");
        foreach (var d in dims)
            Console.WriteLine($"    {d.Dim,5} 维 × {d.Count} 张");

        bool uniform = dims.Count == 1;
        Console.WriteLine(uniform ? "  ✓ 维度统一" : $"  ✗ 维度不统一（{dims.Count} 种）");

        foreach (var img in set) img.Dispose();

        QualityCheck(dir, set.Count);
    }

    /// <summary>
    /// 验证缩小 HOG 工作尺寸后相似度区分能力是否受损
 /// </summary>
    private static void QualityCheck(string dir, int count)
    {
    Console.WriteLine("\n=== HOG 工作尺寸对相似度质量的影响 ===");
    Console.WriteLine("  尺寸   重复对(g0)   重复对(g5)  独立(u0/u1)  独立(u0/u2)");

        var all = Directory.GetFiles(dir, "*.png");
        var g0a = all.FirstOrDefault(f => f.Contains("g000_a")) ?? "";
        var g0b = all.FirstOrDefault(f => f.Contains("g000_b")) ?? "";
        var g5a = all.FirstOrDefault(f => f.Contains("g005_a")) ?? "";
        var g5b = all.FirstOrDefault(f => f.Contains("g005_b")) ?? "";
        var u0 = all.FirstOrDefault(f => f.Contains("u0000")) ?? "";
        var u1 = all.FirstOrDefault(f => f.Contains("u0001")) ?? "";
        var u2 = all.FirstOrDefault(f => f.Contains("u0002")) ?? "";

   if (new[] { g0a, g0b, g5a, g5b, u0, u1, u2 }.Any(string.IsNullOrEmpty))
        {
 Console.WriteLine("  样本不足，跳过");
    return;
        }

        foreach (int maxSize in new[] { 256, 192, 160, 128 })
  {
    var m0a = LoadHog(g0a, maxSize);
         var m0b = LoadHog(g0b, maxSize);
        var m5a = LoadHog(g5a, maxSize);
      var m5b = LoadHog(g5b, maxSize);
            var mu0 = LoadHog(u0, maxSize);
            var mu1 = LoadHog(u1, maxSize);
            var mu2 = LoadHog(u2, maxSize);

   double sG0 = Cos(m0a, m0b);
            double sG5 = Cos(m5a, m5b);
            double sU1 = Cos(mu0, mu1);
         double sU2 = Cos(mu0, mu2);

     Console.WriteLine($"  {maxSize,4}   {sG0,10:F4}   {sG5,10:F4}   {sU1,10:F4}   {sU2,10:F4}");
   }
    }

    private static float[] LoadHog(string path, int maxSize)
    {
        using var m = Cv2.ImRead(path);
        using var g = m.Channels() == 1 ? m.Clone() : m.CvtColor(ColorConversionCodes.BGR2GRAY);

        int h = g.Rows, w = g.Cols;
        Mat? resized = null;
        if (Math.Max(h, w) > maxSize)
        {
            double scale = (double)maxSize / Math.Max(h, w);
            resized = g.Resize(new Size((int)(w * scale), (int)(h * scale)));
            h = resized.Rows; w = resized.Cols;
        }

        int winW = Math.Max(16, w / 8 * 8);
        int winH = Math.Max(16, h / 8 * 8);
        using var hog = new HOGDescriptor(
  new Size(winW, winH), new Size(16, 16),
 new Size(8, 8), new Size(8, 8), 9);
        float[] f = hog.Compute(resized ?? g);
        resized?.Dispose();
        return f;
    }

    private static double Cos(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
       na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
