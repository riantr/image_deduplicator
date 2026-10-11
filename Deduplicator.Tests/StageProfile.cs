using System.Diagnostics;
using Deduplicator.Algorithms;
using Deduplicator.Models;
using Deduplicator.Services;
using OpenCvSharp;

namespace Deduplicator.Tests;

/// <summary>
/// 特征计算的分阶段剖析：解码 / 灰度 / 代理特征 / 核心算法
/// </summary>
public static class StageProfile
{
    public static void Run(string dir, int sampleCount = 60)
    {
        var files = Directory.GetFiles(dir, "*.png")
            .OrderBy(f => f).Take(sampleCount).ToArray();

        Console.WriteLine($"样本 {files.Length} 张");
        Console.WriteLine($"{"阶段",-26} {"总ms",8} {"单张ms",9}");
        Console.WriteLine(new string('─', 48));

        // 1. 文件解码
        var sw = Stopwatch.StartNew();
        var mats = new List<Mat>();
        foreach (var f in files)
        {
            var m = Cv2.ImRead(f);
            if (!m.Empty()) mats.Add(m);
        }
        sw.Stop();
        Report("Cv2.ImRead 解码", sw.ElapsedMilliseconds, mats.Count);
        Report("  含 CvtColor 灰度", MeasureGray(mats), mats.Count);
        Report("  含代理哈希+亮度", MeasureProxy(mats), mats.Count);
        Report("SIFT 特征", MeasureSift(mats), mats.Count);
        Report("ORB 特征", MeasureOrb(mats), mats.Count);
      Report("HOG(Lance 基)", MeasureHog(mats), mats.Count);
        Report("SIFT @工作尺寸480", MeasureSiftSized(mats, 480), mats.Count);
        Report("ORB  @工作尺寸480", MeasureOrbSized(mats, 480), mats.Count);
      Report("ORB  @原始尺寸", MeasureOrbSized(mats, 0), mats.Count);
        Report("SIFT @256", MeasureSiftSized(mats, 256), mats.Count);
 Report("SIFT @320", MeasureSiftSized(mats, 320), mats.Count);
     Report("SIFT @400(原始)", MeasureSiftSized(mats, 400), mats.Count);
        Report("ORB  @256", MeasureOrbSized(mats, 256), mats.Count);
Report("ORB  @320", MeasureOrbSized(mats, 320), mats.Count);

        foreach (var m in mats) m.Dispose();
    }

    static void Report(string name, long ms, int count)
    {
        double per = count == 0 ? 0 : (double)ms / count;
        Console.WriteLine($"{name,-26} {ms,8} {per,9:F2}");
    }

    static long MeasureGray(List<Mat> mats)
    {
        var sw = Stopwatch.StartNew();
        foreach (var m in mats)
        {
            using var g = m.Channels() == 1 ? m.Clone() : m.CvtColor(ColorConversionCodes.BGR2GRAY);
        }
        return sw.ElapsedMilliseconds;
    }

    static long MeasureProxy(List<Mat> mats)
    {
        var sw = Stopwatch.StartNew();
        foreach (var m in mats)
        {
            using var g = m.Channels() == 1 ? m.Clone() : m.CvtColor(ColorConversionCodes.BGR2GRAY);
            HashAlgorithms.ComputeProxyHash(g);
            HashAlgorithms.ComputeLumaStats(g);
        }
        return sw.ElapsedMilliseconds;
    }

    static long MeasureSift(List<Mat> mats)
    {
        var sw = Stopwatch.StartNew();
        foreach (var m in mats)
        {
            var (kp, desc) = FeatureAlgorithms.ComputeSiftFeatures(m);
            desc?.Dispose();
        }
        return sw.ElapsedMilliseconds;
    }

    static long MeasureOrb(List<Mat> mats)
    {
        var sw = Stopwatch.StartNew();
        foreach (var m in mats)
        {
            var (kp, desc) = FeatureAlgorithms.ComputeOrbFeatures(m);
            desc?.Dispose();
        }
        return sw.ElapsedMilliseconds;
    }

    static long MeasureSiftSized(List<Mat> mats, int size)
    {
        var sw = Stopwatch.StartNew();
        foreach (var m in mats)
     {
         using var g = m.Channels() == 1 ? m.Clone() : m.CvtColor(ColorConversionCodes.BGR2GRAY);
    Mat? resized = null;
      Mat work = g;
            if (size > 0)
    {
       resized = g.Resize(new Size(size, (int)(size * g.Rows / (double)g.Cols)));
       work = resized;
   }
            var (kp, desc) = FeatureAlgorithms.ComputeSiftFeatures(work);
 desc?.Dispose();
            resized?.Dispose();
        }
        return sw.ElapsedMilliseconds;
    }

    static long MeasureOrbSized(List<Mat> mats, int size)
    {
        var sw = Stopwatch.StartNew();
        foreach (var m in mats)
        {
      using var g = m.Channels() == 1 ? m.Clone() : m.CvtColor(ColorConversionCodes.BGR2GRAY);
  Mat? resized = null;
            Mat work = g;
            if (size > 0)
   {
     resized = g.Resize(new Size(size, (int)(size * g.Rows / (double)g.Cols)));
        work = resized;
   }
            var (kp, desc) = FeatureAlgorithms.ComputeOrbFeatures(work);
            desc?.Dispose();
      resized?.Dispose();
        }
    return sw.ElapsedMilliseconds;
    }

    static long MeasureHog(List<Mat> mats)
    {
    var sw = Stopwatch.StartNew();
        foreach (var m in mats)
        {
          using var g = m.Channels() == 1 ? m.Clone() : m.CvtColor(ColorConversionCodes.BGR2GRAY);
      using var hog = new HOGDescriptor(
    new Size(64, 128),
   new Size(16, 16),
      new Size(8, 8),
       new Size(8, 8),
     9);
    hog.Compute(g);
  }
        return sw.ElapsedMilliseconds;
    }
}
