using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Deduplicator.Algorithms;
using Deduplicator.Models;
using OpenCvSharp;

namespace Deduplicator.Services;

/// <summary>
/// 去重服务：扫描、特征计算、相似度比对与去重执行。
///
/// <para><b>关键设计</b>：图像在特征计算阶段<b>临时解码、算完立即释放</b>，
/// 常驻内存中只有紧凑特征（哈希 64bit / 直方图 768 float 等）。
/// 这把内存占用从「O(图片数 × 像素数)」降到「O(图片数 × 特征维度)」。</para>
/// </summary>
public class DeduplicationService
{
    private readonly LogService _logService;
    private CancellationTokenSource? _cancellationTokenSource;

    public DeduplicationService(LogService logService)
    {
        _logService = logService;
    }

    /// <summary>
    /// 扫描目录并建立图像索引（不解码像素）
    /// </summary>
    public List<ImageData> ScanImages(string directory, string sortBy = "filename")
    {
        _logService.LogInfo($"开始扫描图像，目录: {directory}，排序: {sortBy}");

        var files = ImageHelper.GetImageFiles(directory, sortBy);

        if (files.Count == 0)
        {
            _logService.LogWarning($"未找到图像文件: {directory}");
            return new List<ImageData>();
        }

        _logService.LogInfo($"找到 {files.Count} 个图像文件");

        var images = new List<ImageData>(files.Count);
        foreach (var file in files)
        {
            images.Add(new ImageData
            {
                FilePath = file.Path,
                FileSize = file.FileSize
            });
        }

        return images;
    }

    /// <summary>
    /// 并行计算特征。每张图独立解码 -> 计算 -> 释放，峰值内存仅与并行度相关。
    /// </summary>
    public async Task CalculateFeaturesAsync(
        IReadOnlyList<ImageData> images,
        AlgorithmType algorithmType,
        AlgorithmParameters parameters,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (images.Count == 0)
            return;

        _logService.LogInfo($"开始计算特征: {algorithmType}（共 {images.Count} 张，并行度 {Environment.ProcessorCount}）");

        int completed = 0;
        int failed = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        try
        {
            await Task.Run(() =>
            {
                Parallel.For(0, images.Count, options, index =>
                {
                    var data = images[index];
                    try
                    {
                        ComputeFeature(data, algorithmType, parameters);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        _logService.LogWarning($"计算特征失败: {Path.GetFileName(data.FilePath)} - {ex.Message}");
                    }
                    finally
                    {
                        int done = Interlocked.Increment(ref completed);
                        // 每 25 张或完成时报告一次，避免过于频繁的 UI 刷新
                        if (done % 25 == 0 || done == images.Count)
                            progress?.Report((int)(done * 100L / images.Count));
                    }
                });
            }, cancellationToken).ConfigureAwait(false);

            _logService.LogInfo($"特征计算完成: 成功 {images.Count - failed} 张，失败 {failed} 张");
        }
        catch (OperationCanceledException)
        {
            _logService.LogWarning("特征计算已取消");
            throw;
        }
    }

    /// <summary>
    /// 计算单张图像的特征（临时解码，用完即释放）
    /// </summary>
    private void ComputeFeature(ImageData data, AlgorithmType algorithmType, AlgorithmParameters parameters)
    {
        // FileHash 无需解码像素
        if (algorithmType == AlgorithmType.FileHash)
        {
            data.FileHash = ComputeFileHash(data.FilePath);
            return;
        }

        // 其余算法需要像素：临时解码
        using var image = ImageHelper.LoadMat(data.FilePath);
        if (image.Empty())
            return;

        // 图像质量指标（用于挑选最佳原图），所有算法通用
        data.ColorRichness = ImageHelper.CalculateColorRichness(image);

        switch (algorithmType)
        {
            case AlgorithmType.AHash:
                data.AHash = HashAlgorithms.ComputeAHash(image, parameters.HashSize);
                data.AHashBits = HashAlgorithms.ToUInt64(data.AHash);
                break;

            case AlgorithmType.DHash:
                data.DHash = HashAlgorithms.ComputeDHash(image, parameters.HashSize);
                data.DHashBits = HashAlgorithms.ToUInt64(data.DHash);
                break;

            case AlgorithmType.PHash:
                data.PHash = HashAlgorithms.ComputePHash(image, parameters.HashSize);
                data.PHashBits = HashAlgorithms.ToUInt64(data.PHash);
                break;

            case AlgorithmType.FastDup:
            {
                int thumbSize = Math.Max(8, parameters.ThumbSize);
                using var resized = image.Resize(new Size(thumbSize, thumbSize), interpolation: InterpolationFlags.Area);
                data.Thumbnail = resized.Clone();
                break;
            }

            case AlgorithmType.Histogram:
                data.Histogram = SimilarityAlgorithms.ComputeHistogram(image, parameters.Bins);
                break;

            case AlgorithmType.Template:
            {
                var (template, _) = ImageHelper.GetTextureRichBlock(image, parameters.BlockSize);
                if (template != null)
                {
                    data.Template?.Dispose();
                    data.Template = template;
                }
                break;
            }

            case AlgorithmType.Ssim:
            {
                // SSIM 只需缩略尺寸即可稳定比较，保留全分辨率没有必要
                var small = ResizeMaxSide(image, 512);
                data.SsimImage?.Dispose();
                data.SsimImage = small;
                break;
            }

            case AlgorithmType.Sift:
            {
                var (kp, des) = FeatureAlgorithms.ComputeSiftFeatures(image, parameters.Features);
                data.SiftKeyPoints = kp;
                data.SiftDescriptors?.Dispose();
                data.SiftDescriptors = des;
                break;
            }

            case AlgorithmType.Surf:
            {
                var (kp, des) = FeatureAlgorithms.ComputeOrbFeatures(image, parameters.Features);
                data.SurfKeyPoints = kp;
                data.SurfDescriptors?.Dispose();
                data.SurfDescriptors = des;
                break;
            }

            case AlgorithmType.Lance:
                data.LanceVector = FeatureAlgorithms.ComputeLanceVector(image, parameters.Dim);
                break;
        }
    }

    /// <summary>
    /// 等比缩放到最长边不超过 <paramref name="maxSide"/>，小图不放大
    /// </summary>
    private static Mat ResizeMaxSide(Mat image, int maxSide)
    {
        int longest = Math.Max(image.Rows, image.Cols);
        if (longest <= maxSide)
            return image.Clone();

        double scale = (double)maxSide / longest;
        int newW = Math.Max(1, (int)(image.Cols * scale));
        int newH = Math.Max(1, (int)(image.Rows * scale));

        return image.Resize(new Size(newW, newH), interpolation: InterpolationFlags.Area);
    }

    /// <summary>
    /// 计算文件 SHA256 哈希
    /// </summary>
    private string? ComputeFileHash(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception ex)
        {
            _logService.LogWarning($"计算文件哈希失败: {filePath} - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 并行比对特征，返回重复组
    /// </summary>
    /// <remarks>
    /// 相比原实现的关键改进：
    /// <list type="number">
    ///   <item>使用 <see cref="CandidatePrefilter"/> 预筛，比对量通常降至 1%~5%；</item>
    ///   <item>多核并行；</item>
    ///   <item><b>消除了重复计算</b>：原实现在两两比对后又对每组重新算一遍相似度，
    ///         导致相似度计算量翻倍。此处缓存比对结果直接复用。</item>
    /// </list>
    /// </remarks>
    public async Task<List<DeduplicationResult>> CompareFeaturesAsync(
        IReadOnlyList<ImageData> images,
        AlgorithmType algorithmType,
        AlgorithmParameters parameters,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (images.Count == 0)
            return new List<DeduplicationResult>();

        _logService.LogInfo($"开始比对特征: {algorithmType}（{images.Count} 张）");

        var valid = images.Where(i => i.HasFeature(algorithmType)).ToList();

        // 显式告警：SIFT/ORB 在平滑图像上可能提不出特征，这些图会被排除，
        // 否则用户会误以为「没有重复」而实际是算法失效。
        int skipped = images.Count - valid.Count;
        if (skipped > 0)
        {
            _logService.LogWarning(
                $"有 {skipped} 张图像未能提取 {algorithmType} 特征（图像过于平滑或过小），已跳过这些图像");
        }

        if (valid.Count < 2)
        {
            _logService.LogWarning(
                valid.Count == 0
                    ? "所有图像均未能提取有效特征，无法比对"
                    : "有效特征不足 2 个，无法比对");
            return new List<DeduplicationResult>();
        }

        // 预筛：只保留可能相似的组合
        var candidates = CandidatePrefilter.BuildCandidates(valid, algorithmType, parameters);

        long totalPairs = (long)valid.Count * (valid.Count - 1) / 2;
        double reduction = totalPairs == 0 ? 0 : (1.0 - (double)candidates.Count / totalPairs) * 100;

        _logService.LogInfo($"预筛完成: {totalPairs} 组组合 → {candidates.Count} 组候选（减少 {reduction:F1}%）");

        // 并行计算候选对的相似度
        var similarities = new ConcurrentDictionary<(int, int), double>();
        int processed = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = cancellationToken
        };

        try
        {
            await Task.Run(() =>
            {
                Parallel.ForEach(candidates, options, pair =>
                {
                    double similarity = CalculateSimilarity(valid[pair.Item1], valid[pair.Item2], algorithmType, parameters);
                    similarities[pair] = similarity;

                    int done = Interlocked.Increment(ref processed);
                    if (done % 100 == 0 || done == candidates.Count)
                        progress?.Report((int)(done * 100L / Math.Max(1, candidates.Count)));
                });
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logService.LogWarning("特征比对已取消");
            throw;
        }

        // 并查集：把相似度超阈值的图合并为同一组
        var parent = new int[valid.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]]; // 路径压缩（迭代版，避免深递归栈溢出）
                x = parent[x];
            }
            return x;
        }

        void Union(int x, int y)
        {
            int px = Find(x);
            int py = Find(y);
            if (px != py) parent[px] = py;
        }

        foreach (var kvp in similarities)
        {
            if (kvp.Value >= parameters.Threshold)
                Union(kvp.Key.Item1, kvp.Key.Item2);
        }

        // 按组归类，并复用已算好的相似度
        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < valid.Count; i++)
        {
            int root = Find(i);
            if (!groups.TryGetValue(root, out var list))
            {
                list = new List<int>();
                groups[root] = list;
            }
            list.Add(i);
        }

        var results = new List<DeduplicationResult>();

        foreach (var group in groups.Values)
        {
            if (group.Count <= 1)
                continue;

            // 以色彩丰富度最高的图作为原图
            var sorted = group.OrderByDescending(i => valid[i].ColorRichness).ToList();
            int originalIndex = sorted[0];

            var duplicates = new List<DuplicateItem>();
            foreach (int idx in sorted.Skip(1))
            {
                // 复用比对阶段的相似度，避免二次计算
                int a = Math.Min(originalIndex, idx);
                int b = Math.Max(originalIndex, idx);
                double similarity = similarities.TryGetValue((a, b), out double cached)
                    ? cached
                    : CalculateSimilarity(valid[a], valid[b], algorithmType, parameters);

                duplicates.Add(new DuplicateItem
                {
                    Path = valid[idx].FilePath,
                    Similarity = similarity
                });
            }

            duplicates.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));

            results.Add(new DeduplicationResult
            {
                OriginalPath = valid[originalIndex].FilePath,
                Duplicates = duplicates
            });
        }

        _logService.LogInfo($"特征比对完成，找到 {results.Count} 组重复图像");
        return results;
    }

    /// <summary>
    /// 计算两个图像的相似度（公开方法）
    /// </summary>
    public double CalculateSimilarityPublic(ImageData image1, ImageData image2, AlgorithmType algorithmType, AlgorithmParameters parameters)
    {
        return CalculateSimilarity(image1, image2, algorithmType, parameters);
    }

    /// <summary>
    /// 计算两个图像的相似度
    /// </summary>
    private double CalculateSimilarity(ImageData image1, ImageData image2, AlgorithmType algorithmType, AlgorithmParameters parameters)
    {
        try
        {
            switch (algorithmType)
            {
                case AlgorithmType.FileHash:
                    return image1.FileHash != null && image2.FileHash != null && image1.FileHash == image2.FileHash
                        ? 1.0
                        : 0.0;

                case AlgorithmType.AHash:
                    return HashSimilarity(image1.AHash, image1.AHashBits, image2.AHash, image2.AHashBits);

                case AlgorithmType.DHash:
                    return HashSimilarity(image1.DHash, image1.DHashBits, image2.DHash, image2.DHashBits);

                case AlgorithmType.PHash:
                    return HashSimilarity(image1.PHash, image1.PHashBits, image2.PHash, image2.PHashBits);

                case AlgorithmType.FastDup:
                    if (image1.Thumbnail != null && image2.Thumbnail != null)
                        return SimilarityAlgorithms.ComputeSsim(image1.Thumbnail, image2.Thumbnail);
                    return 0;

                case AlgorithmType.Histogram:
                    if (image1.Histogram != null && image2.Histogram != null)
                        return SimilarityAlgorithms.HistogramSimilarity(image1.Histogram, image2.Histogram);
                    return 0;

                case AlgorithmType.Template:
                    if (image1.Template != null && image2.Template != null)
                    {
                        double sim = SimilarityAlgorithms.TemplateMatchingSimilarity(image1.Template, image2.Template);
                        double colorDiff = Math.Abs(image1.ColorRichness - image2.ColorRichness)
                                           / Math.Max(image1.ColorRichness, image2.ColorRichness);
                        return sim * (1 - colorDiff * 0.3);
                    }
                    return 0;

                case AlgorithmType.Ssim:
                    if (image1.SsimImage != null && image2.SsimImage != null)
                        return SimilarityAlgorithms.ComputeSsim(image1.SsimImage, image2.SsimImage);
                    return 0;

                case AlgorithmType.Sift:
                    return FeatureAlgorithms.SiftSimilarity(
                        image1.SiftKeyPoints, image1.SiftDescriptors,
                        image2.SiftKeyPoints, image2.SiftDescriptors,
                        parameters.Threshold, parameters.MinMatch);

                case AlgorithmType.Surf:
                    return FeatureAlgorithms.OrbSimilarity(
                        image1.SurfKeyPoints, image1.SurfDescriptors,
                        image2.SurfKeyPoints, image2.SurfDescriptors,
                        parameters.Threshold, parameters.MinMatch);

                case AlgorithmType.Lance:
                    if (image1.LanceVector != null && image2.LanceVector != null)
                        return SimilarityAlgorithms.CosineSimilarity(image1.LanceVector, image2.LanceVector);
                    return 0;

                default:
                    return 0;
            }
        }
        catch (Exception ex)
        {
            _logService.LogError($"计算相似度时出错: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 哈希相似度：优先使用打包后的 ulong 走快速汉明距离（LSH 预筛后通常位数很小）
    /// </summary>
    private static double HashSimilarity(byte[]? hash1, ulong bits1, byte[]? hash2, ulong bits2)
    {
        if (hash1 == null || hash2 == null || hash1.Length == 0 || hash1.Length != hash2.Length)
            return 0;

        int distance;

        // 64 位以内可用打包值 + PopCount，速度远快于逐位比较
        if (hash1.Length <= 64)
        {
            distance = HashAlgorithms.HammingDistance(bits1, bits2);
        }
        else
        {
            distance = HashAlgorithms.HammingDistance(hash1, hash2);
        }

        return 1.0 - (double)distance / hash1.Length;
    }

    /// <summary>
    /// 执行去重操作（删除到回收站或移动到输出目录）
    /// </summary>
    public async Task ExecuteDeduplicationAsync(
        IReadOnlyList<DeduplicationResult> results,
        string outputDirectory,
        string action,
        FileOperationService fileOperationService,
        RecycleBinService recycleBinService,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _logService.LogInfo($"开始执行去重操作: {action}，共 {results.Count} 组");

        int total = 0;
        int deletedCount = 0;
        int movedCount = 0;
        int processed = 0;

        foreach (var result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (result.IsDeleted)
                continue;

            foreach (var duplicate in result.Duplicates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;

                if (action == "delete")
                {
                    var recyclePath = recycleBinService.MoveToRecycleBin(duplicate.Path);
                    if (recyclePath != null)
                    {
                        duplicate.Path = recyclePath;
                        deletedCount++;
                    }
                }
else if (action == "move")
       {
     if (string.IsNullOrEmpty(outputDirectory))
    continue;

        // 目标已存在时改用不冲突的文件名，避免静默失败
       string fileName = Path.GetFileName(duplicate.Path);
        string destPath = fileOperationService.HandleFileNameConflict(
     outputDirectory, fileName);

     if (fileOperationService.MoveFile(duplicate.Path, destPath))
   {
    duplicate.Path = destPath;
     movedCount++;
       }
   else
    {
         _logService.LogWarning($"移动失败: {duplicate.Path} -> {destPath}");
    }
        }
            }

            result.IsDeleted = true;
            processed++;
            progress?.Report((int)(processed * 100L / Math.Max(1, results.Count)));
        }

        await Task.CompletedTask.ConfigureAwait(false);

        _logService.LogInfo($"去重操作完成: 删除 {deletedCount} 个，移动 {movedCount} 个");
    }

    /// <summary>
    /// 取消当前操作
    /// </summary>
    public void CancelOperation()
    {
        try
        {
            _cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 令牌源已释放，忽略
        }
    }

    /// <summary>
    /// 创建新的取消令牌源
    /// </summary>
    public CancellationTokenSource CreateCancellationTokenSource()
    {
        var previous = _cancellationTokenSource;
        _cancellationTokenSource = new CancellationTokenSource();

        // 释放旧令牌源（若已结束），避免句柄泄漏
        if (previous is { IsCancellationRequested: false })
        {
            previous.Dispose();
        }

        return _cancellationTokenSource;
    }
}