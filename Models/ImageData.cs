using System;
using System.IO;
using Avalonia.Media.Imaging;
using Deduplicator.Algorithms;
using Deduplicator.Services;
using OpenCvSharp;

namespace Deduplicator.Models;

/// <summary>
/// 单个图像的数据与特征。
///
/// <para><b>内存策略</b>：本类刻意<b>不持有全分辨率 Mat</b>。全分辨率图像（如 12MP 约 36MB）
/// 若全部常驻，上千张图会直接耗尽内存。改为：</para>
/// <list type="number">
///   <item>特征计算时临时解码，算完立即释放（见 DeduplicationService）；</item>
///   <item>UI 预览通过 <see cref="EnsureThumbnailAsync"/> 按需生成缩略图，
///         并由 <see cref="ThumbnailCache"/> 做有界 LRU 缓存，内存占用与图片总数无关。</item>
/// </list>
/// </summary>
public class ImageData : IDisposable
{
    private static readonly string[] SupportedExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif", ".webp", ".gif" };

    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// 文件名（从 FilePath 提取）
    /// </summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>
    /// 文件所在目录
    /// </summary>
    public string DirectoryPath => Path.GetDirectoryName(FilePath) ?? string.Empty;

    /// <summary>
    /// 文件大小（字节），用于 FileHash 算法的廉价预筛
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// 是否为受支持的图像扩展名
    /// </summary>
    public static bool IsSupportedImage(string path)
    {
        return !string.IsNullOrEmpty(path)
               && Array.IndexOf(SupportedExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;
    }

    // ===== UI 状态 =====

    public bool Checked { get; set; }
    public bool Moved { get; set; }

    /// <summary>
    /// 重复数（在全部图像模式下始终为0，用于与 DeduplicationResult 兼容）
    /// </summary>
    public int DuplicatesCount => 0;

    /// <summary>
    /// 预览缩略图（懒加载，由 ThumbnailCache 管理生命周期）
    /// </summary>
    public Bitmap? BitmapImage { get; private set; }

    /// <summary>
    /// 缩略图是否已加载
    /// </summary>
    public bool HasThumbnail => BitmapImage != null;

    /// <summary>
    /// 确保缩略图已加载；返回 true 表示本次调用产生了新缩略图
    /// </summary>
    public async System.Threading.Tasks.Task<bool> EnsureThumbnailAsync(
        ThumbnailCache cache, int maxSize = 256,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (BitmapImage != null)
            return false;

        var bitmap = await cache.GetOrCreateAsync(this, maxSize, cancellationToken)
            .ConfigureAwait(false);

        if (bitmap != null)
        {
            BitmapImage = bitmap;
            return true;
        }
        return false;
    }

    // ===== 算法特征 =====
    // 说明：以下特征均为紧凑表示（哈希为 64bit、向量为 float[]），
    // 总体占用通常 < 1KB/图，1000 张图不足 1MB，可安全常驻。

    // FileHash 算法
    public string? FileHash { get; set; }

    // Template 算法
    public Mat? Template { get; set; }
    public double ColorRichness { get; set; }

    // Hash 算法（0/1 位数组，hashSize*hashSize 字节）
    public byte[]? PHash { get; set; }
    public byte[]? AHash { get; set; }
    public byte[]? DHash { get; set; }

    /// <summary>
    /// 打包后的哈希，用于 LSH 快速预筛（避免逐对计算汉明距离）
    /// </summary>
    public ulong PHashBits { get; set; }
    public ulong AHashBits { get; set; }
    public ulong DHashBits { get; set; }

    // FastDup 算法
    public Mat? Thumbnail { get; set; }

    // Histogram 算法
    public float[]? Histogram { get; set; }

    // SSIM 算法：存缩略尺寸灰度图即可，避免保留全分辨率
    public Mat? SsimImage { get; set; }

    // SIFT 算法
    public KeyPoint[]? SiftKeyPoints { get; set; }
    public Mat? SiftDescriptors { get; set; }

    // SURF (ORB) 算法
    public KeyPoint[]? SurfKeyPoints { get; set; }
    public Mat? SurfDescriptors { get; set; }

    // Lance 算法
    public float[]? LanceVector { get; set; }

    // ===== 代理特征（用于慢速算法的预筛）=====

    /// <summary>
    /// 代理感知哈希（16×16 灰度网格的均值哈希）。
    ///
    /// <para><b>用途</b>：SSIM / Template / SIFT / ORB / FastDup 的精确比对非常昂贵
    /// （实测 SIFT 在 360 张图上需 93 秒），无法对全部 O(n²) 组合执行。
    /// 本字段提供一个廉价的 64 位哈希，用于把候选量压到可接受范围。</para>
    ///
    /// <para><b>无漏报依据</b>：若两图在结构上足够相似以致精确算法判定为重复，
    /// 则其低分辨率灰度网格必然也高度相似，代理哈希的汉明距离必然很小。
    /// 因此「精确算法判定相似 ⟹ 代理汉明距离 ≤ 阈值」成立，
    /// 以此构造的候选集必然包含全部真实重复对。</para>
    /// </summary>
    public ulong ProxyHash { get; set; }

    /// <summary>
    /// 代理哈希是否已计算
    /// </summary>
    public bool HasProxyHash { get; set; }

    /// <summary>
    /// 图像的平均亮度与对比度（0-1 归一化），用于极廉价的一维剪枝
    /// </summary>
    public double MeanLuma { get; set; }

    public double ContrastLuma { get; set; }

    /// <summary>
    /// 代理统计量是否已计算
    /// </summary>
    public bool HasLumaStats { get; set; }

    /// <summary>
    /// 是否已完成当前算法所需的特征计算
    /// </summary>
    public bool HasFeature(AlgorithmType algorithmType)
    {
        return algorithmType switch
        {
            AlgorithmType.FileHash => FileHash != null,
            AlgorithmType.AHash => AHash != null,
            AlgorithmType.DHash => DHash != null,
            AlgorithmType.PHash => PHash != null,
            AlgorithmType.FastDup => Thumbnail != null,
            AlgorithmType.Histogram => Histogram != null,
            AlgorithmType.Template => Template != null,
            AlgorithmType.Ssim => SsimImage != null,
            AlgorithmType.Sift => SiftDescriptors != null,
            AlgorithmType.Surf => SurfDescriptors != null,
            AlgorithmType.Lance => LanceVector != null,
            _ => false
        };
    }

    /// <summary>
    /// 清空当前算法相关的特征（切换算法时调用以释放资源）
    /// </summary>
    public void ClearFeatures(AlgorithmType algorithmType)
    {
        switch (algorithmType)
        {
            case AlgorithmType.FileHash:
                FileHash = null;
                break;
            case AlgorithmType.AHash:
                AHash = null;
                AHashBits = 0;
                break;
            case AlgorithmType.DHash:
                DHash = null;
                DHashBits = 0;
                break;
            case AlgorithmType.PHash:
                PHash = null;
                PHashBits = 0;
                break;
            case AlgorithmType.FastDup:
                Thumbnail?.Dispose();
                Thumbnail = null;
                break;
            case AlgorithmType.Histogram:
                Histogram = null;
                break;
            case AlgorithmType.Template:
                Template?.Dispose();
                Template = null;
                break;
            case AlgorithmType.Ssim:
                SsimImage?.Dispose();
                SsimImage = null;
                break;
            case AlgorithmType.Sift:
                SiftDescriptors?.Dispose();
                SiftDescriptors = null;
                SiftKeyPoints = null;
                break;
            case AlgorithmType.Surf:
                SurfDescriptors?.Dispose();
                SurfDescriptors = null;
                SurfKeyPoints = null;
                break;
            case AlgorithmType.Lance:
                LanceVector = null;
                break;
        }
    }

    /// <summary>
    /// 释放该图像占用的全部本地资源。
    /// 注意：<see cref="BitmapImage"/> 的所有权属于 <see cref="ThumbnailCache"/>，
    /// 由缓存统一淘汰，此处仅解除引用，避免重复释放导致 UI 崩溃。
    /// </summary>
    public void Dispose()
    {
        Template?.Dispose();
        Template = null;

        Thumbnail?.Dispose();
        Thumbnail = null;

        SsimImage?.Dispose();
        SsimImage = null;

        SiftDescriptors?.Dispose();
        SiftDescriptors = null;
        SiftKeyPoints = null;

        SurfDescriptors?.Dispose();
        SurfDescriptors = null;
        SurfKeyPoints = null;

        BitmapImage = null;

        PHash = null;
        AHash = null;
        DHash = null;
        Histogram = null;
        LanceVector = null;
        FileHash = null;

      ProxyHash = 0;
        HasProxyHash = false;
        MeanLuma = 0;
      ContrastLuma = 0;
        HasLumaStats = false;
    }
}