using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Deduplicator.Models;

namespace Deduplicator.Services;

/// <summary>
/// 缩略图缓存（有界 LRU + 单航班加载去重）。
///
/// <para><b>为什么需要它</b>：原实现为每张图生成一个全分辨率 Bitmap 并常驻内存，
/// 1000 张 12MP 照片会占用数GB，且每张图都要经过一次 PNG 重编码（极慢）。
/// 本缓存只为<b>当前可见/选中</b>的图像保留缩略图，内存占用与图片总数无关。</para>
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    private readonly int _maxEntries;

    // access 顺序：最近使用的排在最后（用于 LRU 淘汰）
    private readonly record struct ThumbnailEntry(string Key, Bitmap Bitmap, int PixelSize);

    private readonly LinkedList<ThumbnailEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<ThumbnailEntry>> _map = new();
    private readonly object _sync = new();

    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inflight = new();

    private int _currentPixelBudget;

    /// <summary>
    /// 缩略图总像素预算（约 256MB）
    /// </summary>
    private const int PixelBudget = 64 * 1024 * 1024 / 4;

    public ThumbnailCache(int maxSize = 256, int maxEntries = 300)
    {
        _maxEntries = Math.Max(16, maxEntries);
        _currentPixelBudget = PixelBudget;
    }

    /// <summary>
    /// 当前缓存的缩略图数量
    /// </summary>
    public int Count
    {
        get { lock (_sync) return _map.Count; }
    }

    /// <summary>
    /// 缓存统计（字节估算）
    /// </summary>
    public long EstimatedBytes
    {
        get { lock (_sync) return (long)_currentPixelBudget * 4; }
    }

    /// <summary>
    /// 获取缩略图，若缓存中已有则直接返回，否则解码生成并缓存
    /// </summary>
    public async Task<Bitmap?> GetOrCreateAsync(
        ImageData data, int maxSize, CancellationToken cancellationToken = default)
    {
        if (data == null || string.IsNullOrEmpty(data.FilePath))
            return null;

        string key = data.FilePath;

        // 命中缓存则刷新 LRU 顺序
        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                return node.Value.Bitmap;
            }
        }

        // 单航班：同一张图的并发加载合并为一次，避免重复解码
        var task = _inflight.GetOrAdd(key, _ => LoadAsync(data, maxSize, cancellationToken));

        Bitmap? result;
        try
        {
            result = await task.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }

        return result;
    }

    /// <summary>
    /// 同步获取缩略图（用于 UI 绑定等不便 await 的场景）
    /// </summary>
    public Bitmap? GetOrCreate(ImageData data, int maxSize)
    {
        if (data == null || string.IsNullOrEmpty(data.FilePath))
            return null;

        string key = data.FilePath;

        lock (_sync)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                return node.Value.Bitmap;
            }
        }

        try
        {
            var bitmap = LoadThumbnailSync(data.FilePath, maxSize);
            if (bitmap != null)
                Add(key, bitmap);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private async Task<Bitmap?> LoadAsync(ImageData data, int maxSize, CancellationToken cancellationToken)
    {
        try
        {
            // 释放解码线程占用，让出给其他图像
            var bitmap = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return LoadThumbnailSync(data.FilePath, maxSize);
            }, cancellationToken).ConfigureAwait(false);

            if (bitmap != null)
                Add(data.FilePath, bitmap);

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解码并缩放为缩略图。
    /// 直接从磁盘字节解码，避免 ImRead 在部分平台上的中文路径问题。
    /// </summary>
    private Bitmap? LoadThumbnailSync(string filePath, int maxSize)
    {
        if (!File.Exists(filePath))
            return null;

// 从流解码，避免中文路径在部分平台的兼容问题
        using var stream = File.OpenRead(filePath);
        using var source = new Bitmap(stream);

     int width = source.PixelSize.Width;
        int height = source.PixelSize.Height;
        int longest = Math.Max(width, height);

   if (longest <= maxSize)
   {
     // 图本来就很小，创建独立实例交给缓存持有
         return CloneBitmap(source);
        }

    int newWidth = maxSize;
        int newHeight = Math.Max(1, (int)((long)height * maxSize / width));

        using var resized = source.CreateScaledBitmap(
            new Avalonia.PixelSize(newWidth, newHeight),
        Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);

        return CloneBitmap(resized);
    }

    /// <summary>
    /// 复制位图以获得独立实例（Avalonia 11.0 无 Bitmap(Bitmap) 复制构造函数）
    /// </summary>
    private static Bitmap CloneBitmap(Bitmap source)
    {
        using var buffer = new MemoryStream();
        source.Save(buffer);
        buffer.Position = 0;
        return new Bitmap(buffer);
    }

    private void Add(string key, Bitmap bitmap)
    {
        int w = bitmap.PixelSize.Width;
        int h = bitmap.PixelSize.Height;
        int pixels = (int)Math.Min((long)w * h, int.MaxValue);

        var entry = new ThumbnailEntry(key, bitmap, pixels);

        lock (_sync)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _currentPixelBudget -= existing.Value.PixelSize;
                try { existing.Value.Bitmap.Dispose(); } catch { }
                _lru.Remove(existing);
                _map.Remove(key);
            }

            var node = new LinkedListNode<ThumbnailEntry>(entry);
            _lru.AddLast(node);
            _map[key] = node;
            _currentPixelBudget += pixels;

            Evict();
        }
    }

    /// <summary>
    /// 淘汰最久未使用的条目，直到回到条数与像素预算之内
    /// </summary>
    private void Evict()
    {
        while (_map.Count > _maxEntries || _currentPixelBudget > PixelBudget)
        {
            var oldest = _lru.First;
            if (oldest == null)
                break;

            _lru.RemoveFirst();
            _map.Remove(oldest.Value.Key);
            _currentPixelBudget -= oldest.Value.PixelSize;

            try
            {
                oldest.Value.Bitmap.Dispose();
            }
            catch
            {
                // 忽略释放异常
            }
        }
    }

    /// <summary>
    /// 清空并释放所有缓存
    /// </summary>
    public void Clear()
    {
        lock (_sync)
        {
            foreach (var node in _lru)
            {
                try { node.Bitmap.Dispose(); } catch { }
            }
            _lru.Clear();
            _map.Clear();
            _currentPixelBudget = 0;
        }
    }

    public void Dispose() => Clear();
}