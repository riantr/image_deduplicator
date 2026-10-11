using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace Deduplicator.Algorithms;

/// <summary>
/// 图像文件扫描与像素级辅助计算
/// </summary>
public static class ImageHelper
{
    private static readonly string[] ImageExtensions =
    { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif", ".webp", ".gif" };

    /// <summary>
    /// 递归扫描目录下的图像文件
    /// </summary>
    /// <remarks>
    /// 优化点：
    /// <list type="bullet">
    ///   <item>只做文件枚举，不解码像素 —— 原实现在此阶段就解码全部图像，是 OOM 的直接原因；</item>
    ///   <item>使用 HashSet + OrdinalIgnoreCase 判定扩展名，替代每文件一次 ToLower 分配与线性查找；</item>
    ///   <item>附带返回文件大小，供 FileHash 算法做廉价预筛。</item>
    /// </list>
    /// </remarks>
    public static List<ImageFileInfo> GetImageFiles(string directory, string sortBy = "filename")
    {
        var result = new List<ImageFileInfo>();

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return result;

        var extensionSet = new HashSet<string>(ImageExtensions, StringComparer.OrdinalIgnoreCase);

        try
        {
            // 无效访问异常（断开的符号链接等）不应中断整个扫描
            var files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (!extensionSet.Contains(Path.GetExtension(file)))
                    continue;

                long size = 0;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch
                {
                    // 无法读取大小时按 0 处理
                }

                result.Add(new ImageFileInfo(file, size));
            }

            if (sortBy == "filesize")
            {
                result.Sort((a, b) => a.FileSize.CompareTo(b.FileSize));
            }
            else
            {
                result.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"扫描目录失败: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// 提取纹理信息最丰富的区块，用于模板匹配
    /// </summary>
    /// <remarks>
    /// 原实现每次调用都 new Random() 且只采样 20 个点，导致结果不可复现；
    /// 此处使用固定种子，并扩大采样数以提升稳定性。
    /// </remarks>
    public static (Mat? template, Point position) GetTextureRichBlock(Mat image, int blockSize = 200, int seed = 0)
    {
        if (image == null || image.Empty())
            return (null, new Point(0, 0));

        if (blockSize <= 0) blockSize = 200;

        int height = image.Rows;
        int width = image.Cols;

        if (height < blockSize || width < blockSize)
            return (image.Clone(), new Point(0, 0));

        const int margin = 20;
        int maxX = width - blockSize - margin;
        int maxY = height - blockSize - margin;
        int minX = margin;
        int minY = margin;

        if (maxX <= minX || maxY <= minY)
            return (image.Clone(), new Point(0, 0));

        Mat gray = image.Channels() == 3 ? image.CvtColor(ColorConversionCodes.BGR2GRAY) : image.Clone();

        try
        {
            double bestScore = 0;
            Mat? bestBlock = null;
            Point bestPosition = new Point(0, 0);

            // 固定种子保证同一图像每次得到相同区块
            var random = new Random(seed);

            // 采样点数量随图像面积自适应，但设上限避免大图过慢
            int area = (width - blockSize) * (height - blockSize);
            int samplePoints = Math.Clamp(area / (blockSize * blockSize), 20, 60);

            using var mean = new Mat();
            using var stddev = new Mat();

            for (int i = 0; i < samplePoints; i++)
            {
                int x = random.Next(minX, maxX);
                int y = random.Next(minY, maxY);
                var roi = new Rect(x, y, blockSize, blockSize);

                using var grayBlock = new Mat(gray, roi);
                Cv2.MeanStdDev(grayBlock, mean, stddev);
                double score = stddev.At<double>(0, 0);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestBlock?.Dispose();
                    bestBlock = new Mat(image, roi).Clone();
                    bestPosition = new Point(x, y);
                }
            }

            // 整图对比度过低时退回中心区块
            if (bestScore < 100 || bestBlock == null)
            {
                int x = (width - blockSize) / 2;
                int y = (height - blockSize) / 2;
                var roi = new Rect(x, y, blockSize, blockSize);
                bestBlock?.Dispose();
                bestBlock = new Mat(image, roi).Clone();
                bestPosition = new Point(x, y);
            }

            return (bestBlock, bestPosition);
        }
        finally
        {
            gray.Dispose();
        }
    }

    /// <summary>
    /// 计算色彩丰富度（饱和度标准差 + 色相熵），用作图像质量指标
    /// </summary>
    public static double CalculateColorRichness(Mat image)
    {
        if (image == null || image.Empty())
            return 0;

        using var hsv = image.CvtColor(ColorConversionCodes.BGR2HSV);
        using var saturation = hsv.ExtractChannel(1);
        using var hue = hsv.ExtractChannel(0);

        using var mean = new Mat();
        using var stddev = new Mat();
        Cv2.MeanStdDev(saturation, mean, stddev);
        double saturationStd = stddev.At<double>(0, 0);

        using var histHue = new Mat();
        Cv2.CalcHist(new[] { hue }, new int[] { 0 }, null, histHue, 1, new int[] { 180 },
                    new Rangef[] { new Rangef(0, 180) });
        Cv2.Normalize(histHue, histHue, 0, 1, NormTypes.MinMax);

        double entropy = 0;
        for (int i = 0; i < histHue.Rows; i++)
        {
            float p = histHue.At<float>(i);
            if (p > 0)
                entropy -= p * Math.Log2(p);
        }

        return saturationStd * 10 + entropy * 5;
    }

    /// <summary>
    /// 读取图像为 Mat（调用方负责释放）。
    /// </summary>
    /// <remarks>
    /// 优先使用 ImRead（更快、不额外分配），失败时回退到从字节解码以兼容特殊字符路径。
    /// </remarks>
    public static Mat LoadMat(string filePath, ImreadModes mode = ImreadModes.Color)
    {
        var mat = Cv2.ImRead(filePath, mode);

        if (mat.Empty())
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                mat = Cv2.ImDecode(bytes, mode);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"图像解码异常 {filePath}: {ex.Message}");
                mat.Dispose();
                return new Mat();
            }
        }

        return mat;
    }
}

/// <summary>
/// 图像文件的轻量描述（路径 + 大小），扫描阶段不解码像素
/// </summary>
public readonly record struct ImageFileInfo(string Path, long FileSize);