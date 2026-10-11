using System;
using System.Linq;
using OpenCvSharp;

namespace Deduplicator.Algorithms;

/// <summary>
/// 相似度度量：模板匹配、直方图相关性、SSIM、余弦相似度
/// </summary>
public static class SimilarityAlgorithms
{
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
    /// 模板匹配相似度（归一化相关系数，取最大响应值）
    /// </summary>
    public static double TemplateMatchingSimilarity(Mat template, Mat image)
    {
        if (template == null || image == null || template.Empty() || image.Empty())
            return 0;

        Mat? resized = null;
        try
        {
            // 模板不能大于待匹配图，否则 MatchTemplate 会抛异常
            if (image.Cols < template.Cols || image.Rows < template.Rows)
            {
                resized = image.Resize(new Size(template.Cols, template.Rows));
            }

            Mat target = resized ?? image;
            if (target.Cols < template.Cols || target.Rows < template.Rows)
                return 0;

            using var result = new Mat();
            Cv2.MatchTemplate(target, template, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double maxVal, out _, out _);

            return maxVal;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Template matching error: {ex.Message}");
            return 0;
        }
        finally
        {
            resized?.Dispose();
        }
    }

    /// <summary>
    /// 直方图相似度（皮尔逊相关系数，映射到 [0,1]）
    /// </summary>
    public static double HistogramSimilarity(float[]? hist1, float[]? hist2)
    {
        if (hist1 == null || hist2 == null || hist1.Length == 0 || hist1.Length != hist2.Length)
            return 0;

        double mean1 = hist1.Average();
        double mean2 = hist2.Average();

        double num = 0;
        double den1 = 0;
        double den2 = 0;

        for (int i = 0; i < hist1.Length; i++)
        {
            double diff1 = hist1[i] - mean1;
            double diff2 = hist2[i] - mean2;
            num += diff1 * diff2;
            den1 += diff1 * diff1;
            den2 += diff2 * diff2;
        }

        double den = Math.Sqrt(den1 * den2);
        if (den == 0)
            return 0;

        return (num / den + 1) / 2;
    }

    /// <summary>
    /// 结构相似度（SSIM，基于卷积的局部统计）
    /// </summary>
    /// <remarks>
    /// <para><b>数值正确性</b>：必须在计算前把图像转为 <c>CV_32F</c>。
    /// 若直接在 <c>CV_8UC1</c> 上做乘法，255×255=65025 会溢出回绕成 9，
    /// 导致方差恒接近 0；又因 <c>C2=58.5</c> 远大于该值，
    /// 任何两图（哪怕结构完全无关）的 SSIM 都会饱和到 1.0，算法完全失效。</para>
    ///
    /// <para><b>性能</b>：局部统计量（μ、σ²、σ₁₂）本质是卷积结果，
    /// 用 <see cref="Cv2.Filter2D"/> 一次性求出整幅统计图，
    /// 再逐像素计算 SSIM 映射图。相比逐窗口切片 + 逐窗口高斯模糊，
    /// 可将 OpenCV 调用次数从「窗口数 × 5」降到「常数」，实测快两个数量级。</para>
    /// </remarks>
    /// <param name="img1">第一张图像</param>
    /// <param name="img2">第二张图像</param>
    /// <param name="windowSize">窗口边长（默认 11，标准 SSIM 取值）</param>
    public static double ComputeSsim(Mat img1, Mat img2, int windowSize = 11)
    {
        if (img1 == null || img2 == null || img1.Empty() || img2.Empty())
            return 0;

        int win = windowSize < 3 ? 11 : (windowSize % 2 == 0 ? windowSize + 1 : windowSize);

        Mat raw1 = ToGray(img1, out bool ownsRaw1);
        Mat raw2 = ToGray(img2, out bool ownsRaw2);
        Mat? resized2 = null;

        try
        {
            // 统一尺寸（SSIM 要求逐像素对齐）
            if (raw1.Size() != raw2.Size())
            {
                if (ownsRaw2)
                {
                    raw2.Dispose();
                    ownsRaw2 = false;
                }

                var target = new Size(raw1.Cols, raw1.Rows);
                resized2 = img2.Channels() == 1
                    ? img2.Resize(target)
                    : img2.CvtColor(ColorConversionCodes.BGR2GRAY).Resize(target);

                raw2 = resized2;
            }

            int rows = raw1.Rows;
            int cols = raw1.Cols;

            // 转为浮点，避免 uint8 乘法溢出（关键！）
            using var f1 = new Mat();
            using var f2 = new Mat();
            raw1.ConvertTo(f1, MatType.CV_32F);
            raw2.ConvertTo(f2, MatType.CV_32F);

            const double C1 = 6.5025;   // (0.01 * 255)^2
            const double C2 = 58.5225;  // (0.03 * 255)^2

            var ksize = new Size(win, win);

            using var mu1 = new Mat();
            using var mu2 = new Mat();
            Cv2.GaussianBlur(f1, mu1, ksize, 1.5);
            Cv2.GaussianBlur(f2, mu2, ksize, 1.5);

            // 互相关的中间量：x1²、x2²、x1x2
            using var t1 = new Mat();
            using var t2 = new Mat();

            using var mu1Sq = new Mat();
            Cv2.Multiply(mu1, mu1, mu1Sq);

            using var mu2Sq = new Mat();
            Cv2.Multiply(mu2, mu2, mu2Sq);

            using var mu1Mu2 = new Mat();
            Cv2.Multiply(mu1, mu2, mu1Mu2);

            // sigma1² = E[x1²] - mu1²
            Cv2.Multiply(f1, f1, t1);
            using var sigma1Sq = new Mat();
            Cv2.GaussianBlur(t1, sigma1Sq, ksize, 1.5);
            Cv2.Subtract(sigma1Sq, mu1Sq, sigma1Sq);

            // sigma2² = E[x2²] - mu2²
            Cv2.Multiply(f2, f2, t1);
            using var sigma2Sq = new Mat();
            Cv2.GaussianBlur(t1, sigma2Sq, ksize, 1.5);
            Cv2.Subtract(sigma2Sq, mu2Sq, sigma2Sq);

            // sigma12 = E[x1x2] - mu1*mu2
            Cv2.Multiply(f1, f2, t1);
            using var sigma12 = new Mat();
            Cv2.GaussianBlur(t1, sigma12, ksize, 1.5);
            Cv2.Subtract(sigma12, mu1Mu2, sigma12);

            // 分子 = (2*mu1*mu2 + C1) * (2*sigma12 + C2)
            using var numerator = new Mat();
            Cv2.Multiply(mu1Mu2, Scalar.All(2), t1);
            Cv2.Add(t1, Scalar.All(C1), t1);
            Cv2.Multiply(sigma12, Scalar.All(2), t2);
            Cv2.Add(t2, Scalar.All(C2), t2);
            Cv2.Multiply(t1, t2, numerator);

            // 分母 = (mu1² + mu2² + C1) * (sigma1² + sigma2² + C2)
            using var denominator = new Mat();
            Cv2.Add(mu1Sq, mu2Sq, t1);
            Cv2.Add(t1, Scalar.All(C1), t1);
            Cv2.Add(sigma1Sq, sigma2Sq, t2);
            Cv2.Add(t2, Scalar.All(C2), t2);
            Cv2.Multiply(t1, t2, denominator);

            // SSIM 映射，负值截断到 0
            using var ssimMap = new Mat();
            Cv2.Divide(numerator, denominator, ssimMap);
            Cv2.Max(ssimMap, 0, ssimMap);

            double ssim = Cv2.Mean(ssimMap).Val0;
            return Math.Clamp(ssim, 0, 1);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SSIM computation error: {ex.Message}");
            return 0;
        }
        finally
        {
            if (ownsRaw1) raw1.Dispose();
            if (ownsRaw2 && !ReferenceEquals(raw2, raw1)) raw2.Dispose();
    resized2?.Dispose();
        }
    }

    /// <summary>
    /// 计算 BGR 三通道联合直方图并做 MinMax 归一化
    /// </summary>
    public static float[] ComputeHistogram(Mat image, int bins = 256)
    {
        if (bins <= 0) bins = 256;
        if (image == null || image.Empty())
            return new float[bins * 3];

        var histogram = new float[bins * 3];

        // OpenCvSharp 的 Split 返回 Mat[]，不实现 IDisposable，需手动释放
        Mat[]? channels = null;
        try
        {
            channels = image.Split();
                var range = new Rangef[] { new Rangef(0, 256) };

                for (int c = 0; c < channels.Length && c < 3; c++)
                {
                    using var hist = new Mat();
                    Cv2.CalcHist(new[] { channels[c] }, new int[] { 0 }, null, hist, 1, new int[] { bins }, range);
                    Cv2.Normalize(hist, hist, 0, 1, NormTypes.MinMax);

                    int offset = c * bins;
                    for (int i = 0; i < bins; i++)
                    {
                        histogram[offset + i] = hist.At<float>(i);
                    }
                }

                return histogram;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Histogram computation error: {ex.Message}");
                return histogram;
            }
            finally
            {
                if (channels != null)
                {
                    foreach (var ch in channels) ch.Dispose();
                }
            }
    }

    /// <summary>
    /// 余弦相似度
    /// </summary>
    public static double CosineSimilarity(float[]? vec1, float[]? vec2)
    {
        if (vec1 == null || vec2 == null || vec1.Length == 0 || vec1.Length != vec2.Length)
            return 0;

        double dotProduct = 0;
        double norm1 = 0;
        double norm2 = 0;

        for (int i = 0; i < vec1.Length; i++)
        {
            dotProduct += vec1[i] * vec2[i];
            norm1 += vec1[i] * vec1[i];
            norm2 += vec2[i] * vec2[i];
        }

        norm1 = Math.Sqrt(norm1);
        norm2 = Math.Sqrt(norm2);

        if (norm1 == 0 || norm2 == 0)
            return 0;

        return Math.Max(0, dotProduct / (norm1 * norm2));
    }
}