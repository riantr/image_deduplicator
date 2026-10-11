using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Deduplicator.Models;
using Deduplicator.Services;

namespace Deduplicator.Views;

public partial class ImageListWindow : Window
{
    public ObservableCollection<ImageListItem> ImageList { get; } = new();

    private ObservableCollection<ImageData>? _allImages;
    private string _inputDirectory = string.Empty;
    private DeduplicationService? _deduplicationService;
    private LogService? _logService;
    private CancellationTokenSource? _loadCancellationTokenSource;
    private ThumbnailCache? _thumbnailCache;
    private int _loadedCount;

    public ImageListWindow()
    {
        InitializeComponent();
        ImageItemsControl.ItemsSource = ImageList;
        _thumbnailCache = new ThumbnailCache(maxEntries: 400);

        this.Opened += async (_, _) =>
        {
            if (ImageList.Count > 0)
            {
                await LoadAllImagesAsync();
            }
        };
    }

    /// <summary>
    /// 设置父窗口位置
    /// </summary>
    public void SetOwner(Window owner)
    {
        this.Position = new Avalonia.PixelPoint(owner.Position.X + 50, owner.Position.Y + 50);
    }

    /// <summary>
    /// 设置输入目录和服务
    /// </summary>
    public void Initialize(
        string inputDirectory,
        DeduplicationService deduplicationService,
        LogService logService,
        ObservableCollection<ImageData> allImages)
    {
        _inputDirectory = inputDirectory;
        _deduplicationService = deduplicationService;
        _logService = logService;
        _allImages = allImages;

        _logService.LogInfo($"ImageListWindow 初始化: {inputDirectory}");
    }

    /// <summary>
    /// 扫描目录并添加文件名到列表（不解码像素，秒级完成）
    /// </summary>
    public void ScanAndAddFileNames(string directory) => ScanAndAddFileNamesSync(directory);

    public void ScanAndAddFileNamesSync(string directory)
    {
        _inputDirectory = directory;
        _logService?.LogInfo($"开始扫描目录: {directory}");

        ImageList.Clear();
        _loadedCount = 0;

        try
        {
            // 复用 ImageHelper 的扫描逻辑，保持扩展名判定一致
            var files = Algorithms.ImageHelper.GetImageFiles(directory, "filename");

            _logService?.LogInfo($"找到 {files.Count} 个图像文件");

            for (int i = 0; i < files.Count; i++)
            {
                ImageList.Add(new ImageListItem
                {
                    Index = i,
                    FilePath = files[i].Path,
                    FileSize = files[i].FileSize,
                    FileName = Path.GetFileName(files[i].Path)
                });
            }

            CountText.Text = $"数量: {ImageList.Count}";
            UpdateStatus($"找到 {ImageList.Count} 个图像文件，正在加载缩略图...");

            _logService?.LogInfo($"已添加 {ImageList.Count} 个文件名到列表");
        }
        catch (Exception ex)
        {
            _logService?.LogError($"扫描目录失败: {ex.Message}");
            UpdateStatus($"扫描目录失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 并行加载缩略图（受 LRU 缓存约束，内存与图片总数无关）
    /// </summary>
    public async Task LoadAllImagesAsync()
    {
        if (_logService == null || _thumbnailCache == null)
            return;

        _loadCancellationTokenSource?.Cancel();
        _loadCancellationTokenSource = new CancellationTokenSource();

        var token = _loadCancellationTokenSource.Token;
        _logService.LogInfo("开始异步加载缩略图...");

        _loadedCount = 0;

        var pending = ImageList.Where(item => !item.IsLoaded).ToList();
        if (pending.Count == 0)
        {
            UpdateStatus();
            return;
        }

        // 每次批量提交一批到 UI 线程，避免逐项 Post 造成的调度风暴
        const int batchSize = 16;

        try
        {
            using var semaphore = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount));

            var tasks = pending.Select(async item =>
            {
                await semaphore.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var data = item.ImageData ?? new ImageData { FilePath = item.FilePath, FileSize = item.FileSize };
                    await data.EnsureThumbnailAsync(_thumbnailCache, 200, token).ConfigureAwait(false);

                    if (!data.HasThumbnail)
                        return;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        item.ImageData = data;
                        item.IsLoaded = true;
                        _loadedCount++;
                        _allImages?.Add(data);

                        if (_loadedCount % batchSize == 0 || _loadedCount == pending.Count)
                        {
                            UpdateStatus($"已加载 {_loadedCount}/{pending.Count} 个图像");
                        }
                    });
                }
                catch (OperationCanceledException)
                {
                    // 正常取消
                }
                catch (Exception ex)
                {
                    _logService.LogWarning($"加载缩略图失败: {item.FileName} - {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(UpdateStatus);
            _logService.LogInfo($"图像加载完成: {_loadedCount}/{pending.Count}");
        }
        catch (OperationCanceledException)
        {
            _logService.LogWarning("图像加载已取消");
        }
        catch (Exception ex)
        {
            _logService.LogError($"加载图像时出错: {ex.Message}");
        }
    }

    /// <summary>
    /// 清空列表
    /// </summary>
    public void ClearList()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _loadCancellationTokenSource?.Cancel();
            ImageList.Clear();
            _allImages?.Clear();
            _loadedCount = 0;
            _thumbnailCache?.Clear();
            UpdateStatus();
        });
    }

    private void UpdateStatus()
    {
        StatusText.Text = $"共加载 {_loadedCount}/{ImageList.Count} 个图像";
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    /// <summary>
    /// 在 UI 线程设置状态文本
    /// </summary>
    public void SetStatus(string status)
    {
        Dispatcher.UIThread.Post(() => StatusText.Text = status);
    }

    private void ImageDataGrid_DoubleTapped(object? sender, TappedEventArgs e) => PreviewSelectedImage();

    private void PreviewButton_Click(object? sender, RoutedEventArgs e) => PreviewSelectedImage();

    private void PreviewSelectedImage()
    {
        SetStatus("预览功能暂未实现");
    }

    /// <summary>
    /// 刷新按钮点击
    /// </summary>
    private async void RefreshButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_inputDirectory))
        {
            SetStatus("无法刷新：未设置输入目录");
            return;
        }

        _loadCancellationTokenSource?.Cancel();

        ScanAndAddFileNames(_inputDirectory);
        await LoadAllImagesAsync();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        _loadCancellationTokenSource?.Cancel();
        _thumbnailCache?.Dispose();
        Close();
    }
}

/// <summary>
/// 图像列表项
/// </summary>
public class ImageListItem
{
    public int Index { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public ImageData? ImageData { get; set; }
    public bool IsLoaded { get; set; }
}