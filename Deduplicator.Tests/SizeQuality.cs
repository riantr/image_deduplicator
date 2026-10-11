using Deduplicator.Algorithms;
using Deduplicator.Models;
using Deduplicator.Services;
using OpenCvSharp;

namespace Deduplicator.Tests;

/// <summary>
/// 验证工作尺寸对相似度区分能力的影响
/// </summary>
public static class SizeQuality
{
    public static void Run(string dir)
    {
var all = Directory.GetFiles(dir, "*.png");

        // 挑选：2 组已知重复 + 若干独立图
        var g0a = all.FirstOrDefault(f => f.Contains("g000_a"));
        var g0b = all.FirstOrDefault(f => f.Contains("g000_b"));
        var g5a = all.FirstOrDefault(f => f.Contains("g005_a"));
        var g5b = all.FirstOrDefault(f => f.Contains("g005_b"));
        var u0 = all.FirstOrDefault(f => f.Contains("u0000"));
        var u1 = all.FirstOrDefault(f => f.Contains("u0001"));
        var u2 = all.FirstOrDefault(f => f.Contains("u0002"));

        if (new[] { g0a, g0b, g5a, g5b, u0, u1, u2 }.Any(x => x == null))
        {
            Console.WriteLine("样本不足");
            return;
        }

        Console.WriteLine("工作尺寸      重复对(g0)   重复对(g5)  独立(u0/u1)  独立(u0/u2)");
        Console.WriteLine(new string('─', 66));

        foreach (int size in new[] { 0, 400, 320, 256, 192 })
        {
            // 前面的 null 检查已确保这些变量非空，此处用 ! 消除编译器提示
            var m0a = Load(g0a!, size); var m0b = Load(g0b!, size);
        var m5a = Load(g5a!, size); var m5b = Load(g5b!, size);
            var mu0 = Load(u0!, size); var mu1 = Load(u1!, size); var mu2 = Load(u2!, size);

            var (kp0a, d0a) = FeatureAlgorithms.ComputeSiftFeatures(m0a);
            var (kp0b, d0b) = FeatureAlgorithms.ComputeSiftFeatures(m0b);
            double sG0 = FeatureAlgorithms.SiftSimilarity(kp0a, d0a, kp0b, d0b, 0.6, 6);

            var (kp5a, d5a) = FeatureAlgorithms.ComputeSiftFeatures(m5a);
            var (kp5b, d5b) = FeatureAlgorithms.ComputeSiftFeatures(m5b);
            double sG5 = FeatureAlgorithms.SiftSimilarity(kp5a, d5a, kp5b, d5b, 0.6, 6);

            var (kpu0, du0) = FeatureAlgorithms.ComputeSiftFeatures(mu0);
            var (kpu1, du1) = FeatureAlgorithms.ComputeSiftFeatures(mu1);
            var (kpu2, du2) = FeatureAlgorithms.ComputeSiftFeatures(mu2);
            double sU1 = FeatureAlgorithms.SiftSimilarity(kpu0, du0, kpu1, du1, 0.6, 6);
            double sU2 = FeatureAlgorithms.SiftSimilarity(kpu0, du0, kpu2, du2, 0.6, 6);

            string label = size == 0 ? "原始" : size.ToString();
            Console.WriteLine(string.Format("{0,-12} {1,12:F4} {2,12:F4} {3,12:F4} {4,12:F4}",
   label, sG0, sG5, sU1, sU2));

            d0a?.Dispose(); d0b?.Dispose(); d5a?.Dispose(); d5b?.Dispose();
            du0?.Dispose(); du1?.Dispose(); du2?.Dispose();
            m0a.Dispose(); m0b.Dispose(); m5a.Dispose(); m5b.Dispose();
            mu0.Dispose(); mu1.Dispose(); mu2.Dispose();
        }
    }

    static Mat Load(string path, int size)
    {
        var m = Cv2.ImRead(path);
        if (size > 0 && m.Width != size)
        {
            var h = (int)(size * m.Rows / (double)m.Cols);
            var r = m.Resize(new Size(size, h));
            m.Dispose();
            return r;
        }
        return m;
    }
}
