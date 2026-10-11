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
    /// 结构相似度（SSIM）
    /// </summary>
    /// <remarks>
    /// 使用窗口大小 <paramref name="windowSize"/>（默认 11，标准 SSIM 取值）。
    /// 所有中间 Mat 均由 using 管理，避免原实现中 <c>gray1 * gray1</c> 产生的未释放临时对象。
    /// </remarks>
    public static double ComputeSsim(Mat img1, Mat img2, int windowSize = 11)
    {
        if (img1 == null || img2 == null || img1.Empty() || img2.Empty())
            return 0;

        if (windowSize < 3) windowSize = 11;

        Mat? resized2 = null;
        Mat gray1 = ToGray(img1, out bool ownsGray1);
        Mat gray2 = ToGray(img2, out bool ownsGray2);

        try
        {
            if (gray1.Size() != gray2.Size())
            {
                // 释放旧的 gray2，再生成新的尺寸版本
                if (ownsGray2) gray2.Dispose();
                gray2 = resized2 = img2.Channels() == 1 ? img2.Clone() : img2.CvtColor(ColorConversionCodes.BGR2GRAY);
                resized2 = gray2.Resize(gray1.Size());
                gray2.Dispose();
                gray2 = resized2;
                ownsGray2 = true;
            }

            // 高斯窗口大小需为奇数
            int ksize = windowSize % 2 == 0 ? windowSize + 1 : windowSize;
            var ksizeSize = new Size(ksize, ksize);

            using var mu1 = new Mat();
            using var mu2 = new Mat();
            Cv2.GaussianBlur(gray1, mu1, ksizeSize, 1.5);
            Cv2.GaussianBlur(gray2, mu2, ksizeSize, 1.5);

            using var mu1Sq = new Mat();
            using var mu2Sq = new Mat();
            using var mu1Mu2 = new Mat();
            Cv2.Multiply(mu1, mu1, mu1Sq);
            Cv2.Multiply(mu2, mu2, mu2Sq);
            Cv2.Multiply(mu1, mu2, mu1Mu2);

            using var sigma1Sq = new Mat();
            using var sigma2Sq = new Mat();
            using var sigma12 = new Mat();

            using (var sq1 = new Mat())
            {
                Cv2.Multiply(gray1, gray1, sq1);
                Cv2.GaussianBlur(sq1, sigma1Sq, ksizeSize, 1.5);
            }
            Cv2.Subtract(sigma1Sq, mu1Sq, sigma1Sq);

            using (var sq2 = new Mat())
            {
                Cv2.Multiply(gray2, gray2, sq2);
                Cv2.GaussianBlur(sq2, sigma2Sq, ksizeSize, 1.5);
            }
            Cv2.Subtract(sigma2Sq, mu2Sq, sigma2Sq);

            using (var prod = new Mat())
            {
                Cv2.Multiply(gray1, gray2, prod);
                Cv2.GaussianBlur(prod, sigma12, ksizeSize, 1.5);
            }
            Cv2.Subtract(sigma12, mu1Mu2, sigma12);

            // SSIM 常量，L=255
            const double C1 = 6.5025;
            const double C2 = 58.5225;

            using var numerator = new Mat();

            // 分子 = (2*mu1*mu2 + C1) * (2*sigma12 + C2)
            using (var num1 = new Mat())
            using (var num2 = new Mat())
            {
                Cv2.Multiply(mu1Mu2, mu1Mu2, num1);
                Cv2.Multiply(num1, Scalar.All(2), num1);
                Cv2.Add(num1, Scalar.All(C1), num1);

                Cv2.Multiply(sigma12, sigma12, num2);
                Cv2.Multiply(num2, Scalar.All(2), num2);
                Cv2.Add(num2, Scalar.All(C2), num2);

                Cv2.Multiply(num1, num2, numerator);
            }

            // 分母 = (mu1^2 + mu2^2 + C1) * (sigma1^2 + sigma2^2 + C2)
            using var denominator = new Mat();
            using (var den1 = new Mat())
            using (var den2 = new Mat())
            {
                Cv2.Add(mu1Sq, mu2Sq, den1);
                Cv2.Add(den1, Scalar.All(C1), den1);

                Cv2.Add(sigma1Sq, sigma2Sq, den2);
                Cv2.Add(den2, Scalar.All(C2), den2);

                Cv2.Multiply(den1, den2, denominator);
            }

            using var ssimMap = new Mat();
            Cv2.Divide(numerator, denominator, ssimMap);

            double ssim = Cv2.Mean(ssimMap).Val0;
            return Math.Max(0, Math.Min(1, ssim));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SSIM computation error: {ex.Message}");
            return 0;
        }
        finally
        {
            if (ownsGray1) gray1.Dispose();
            if (ownsGray2 && !ReferenceEquals(gray2, gray1)) gray2.Dispose();
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