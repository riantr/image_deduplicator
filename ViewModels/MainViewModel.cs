using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Deduplicator.Helpers;
using Deduplicator.Models;
using Deduplicator.Services;

namespace Deduplicator.ViewModels;

/// <summary>
/// 主窗口 ViewModel。
///
/// <para>职责：编排「扫描 → 特征 → 比对 → 执行」流程，并把结果投影到 UI 绑定属性上。
/// 图像的解码、特征与相似度计算全部委托给 <see cref="DeduplicationService"/>。</para>
/// </summary>
public class MainViewModel : ViewModelBase
{
    /// <summary>
 /// 单条删除记录，用于撤回删除
    /// </summary>
    private readonly record struct DeletedItemInfo(
        string FilePath,
        string RecyclePath,
        DeduplicationResult ParentResult,
        DuplicateItem DuplicateItem);

    private readonly LogService _logService;
    private readonly FileOperationService _fileOperationService;
    private readonly RecycleBinService _recycleBinService;
    private readonly DeduplicationService _deduplicationService;
    private readonly ThumbnailCache _thumbnailCache = new();

    private string _inputDirectory = string.Empty;
    private string _outputDirectory = string.Empty;
    private AlgorithmType _currentAlgorithm = AlgorithmType.AHash;
    private AlgorithmParameters _algorithmParameters = new();
    private List<ImageData> _images = new();
    private int _currentIndex = -1;
    private int _progress;
    private string _statusMessage = "就绪";

    private CancellationTokenSource? _cancellationTokenSource;

    private DeduplicationResult? _selectedOriginal;
    private DuplicateItem? _selectedDuplicate;
    private int _currentDuplicateIndex;
    private ImageData? _selectedImage;

    /// <summary>
    /// 删除历史，用于撤回删除
    /// </summary>
    private readonly Stack<DeletedItemInfo> _deletedItemsHistory = new();

    public MainViewModel(
        LogService logService,
        FileOperationService fileOperationService,
        RecycleBinService recycleBinService,
        DeduplicationService deduplicationService)
    {
        _logService = logService;
        _fileOperationService = fileOperationService;
        _recycleBinService = recycleBinService;
        _deduplicationService = deduplicationService;

        _logService.LogMessages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(LogText));

        SetDefaultParameters();
        InitializeCommands();
    }

    // ===== 绑定属性 =====

    public string InputDirectory
    {
        get => _inputDirectory;
        set => SetProperty(ref _inputDirectory, value);
    }

    public string OutputDirectory
    {
        get => _outputDirectory;
        set => SetProperty(ref _outputDirectory, value);
    }

    public AlgorithmType CurrentAlgorithm
    {
        get => _currentAlgorithm;
        set
        {
            if (SetProperty(ref _currentAlgorithm, value))
            {
                SetDefaultParameters();
            }
        }
    }

    public AlgorithmParameters AlgorithmParameters
    {
        get => _algorithmParameters;
        set => SetProperty(ref _algorithmParameters, value ?? new AlgorithmParameters());
    }

    /// <summary>
    /// 已加载的图像列表
    /// </summary>
    public List<ImageData> Images
    {
        get => _images;
        private set
        {
            _images = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LoadedImageCount));
            RefreshCommandStates();
        }
    }

    public int LoadedImageCount => _images.Count;

    public ObservableCollection<DeduplicationResult> DeduplicationResults { get; } = new();

    /// <summary>
    /// 原图列表数据源
    /// </summary>
    public object OriginalListSource => DeduplicationResults.Count > 0
        ? DeduplicationResults
            .Select(r => FindImage(r.OriginalPath))
            .Where(img => img != null)
            .Cast<ImageData>()
            .ToList()
        : _images;

    public bool IsShowingComparisonResults => DeduplicationResults.Count > 0;

    public string NavigationText
    {
        get
        {
            if (IsShowingComparisonResults)
                return $"{CurrentIndex + 1} / {DeduplicationResults.Count}";

            int total = _images.Count;
            int current = CurrentIndex >= 0 && CurrentIndex < total ? CurrentIndex + 1 : 0;
            return $"{current} / {total}";
        }
    }

    public ObservableCollection<DuplicateItem> CurrentDuplicates { get; } = new();

    public int CurrentIndex
    {
        get => _currentIndex;
        set
        {
            if (_currentIndex == value) return;
            _currentIndex = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(NavigationText));
            OnPropertyChanged(nameof(CurrentResult));
            OnPropertyChanged(nameof(OriginalImage));
            OnPropertyChanged(nameof(DuplicateImages));

            UpdateCurrentDuplicates();
            RefreshCommandStates();

            // 预取选中项缩略图，避免界面显示空白
            _ = PrefetchThumbnailAsync();
        }
    }

    public int Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ObservableCollection<string> LogMessages => _logService.LogMessages;

    public string LogText
    {
        get
        {
            var messages = _logService.LogMessages;
            lock (messages)
            {
                return string.Join("\n", messages);
            }
        }
    }

    public DeduplicationResult? SelectedOriginal
    {
        get => _selectedOriginal;
        set
        {
            if (ReferenceEquals(_selectedOriginal, value)) return;
            _selectedOriginal = value;
            OnPropertyChanged();

            if (value != null)
            {
                int index = DeduplicationResults.IndexOf(value);
                if (index >= 0)
                    CurrentIndex = index;
            }

            RefreshCommandStates();
        }
    }

    public ImageData? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (ReferenceEquals(_selectedImage, value)) return;
            _selectedImage = value;

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedImageBitmap));

            if (value == null)
            {
                CurrentDuplicates.Clear();
                OnPropertyChanged(nameof(CurrentDuplicates));
                RefreshCommandStates();
                return;
            }

            if (IsShowingComparisonResults)
            {
                int index = DeduplicationResults.ToList().FindIndex(r => r.OriginalPath == value.FilePath);
                if (index >= 0)
                {
                    _currentIndex = index;
                    OnPropertyChanged(nameof(CurrentIndex));
                    OnPropertyChanged(nameof(NavigationText));
                    OnPropertyChanged(nameof(CurrentResult));
                    UpdateCurrentDuplicates();
                }
            }
            else
            {
                int index = _images.IndexOf(value);
                if (index >= 0)
                {
                    _currentIndex = index;
                    OnPropertyChanged(nameof(CurrentIndex));
                    OnPropertyChanged(nameof(NavigationText));
                }
            }

            _ = PrefetchThumbnailAsync();
            RefreshCommandStates();
        }
    }

    public Avalonia.Media.Imaging.Bitmap? SelectedImageBitmap => _selectedImage?.BitmapImage;

    public DuplicateItem? SelectedDuplicate
    {
        get => _selectedDuplicate;
        set
        {
            if (ReferenceEquals(_selectedDuplicate, value)) return;
            _selectedDuplicate = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedDuplicateImageData));
            RefreshCommandStates();
        }
    }

    public ImageData? SelectedDuplicateImageData =>
        _selectedDuplicate != null ? FindImage(_selectedDuplicate.Path) : null;

    public int CurrentDuplicateIndex
    {
        get => _currentDuplicateIndex;
        set
        {
            if (_currentDuplicateIndex == value) return;
            _currentDuplicateIndex = value;
            OnPropertyChanged();
            UpdateSelectedDuplicate();
        }
    }

    public DeduplicationResult? CurrentResult =>
        CurrentIndex >= 0 && CurrentIndex < DeduplicationResults.Count
            ? DeduplicationResults[CurrentIndex]
            : null;

    public ImageData? OriginalImage =>
        CurrentResult != null ? FindImage(CurrentResult.OriginalPath) : null;

    public List<ImageData> DuplicateImages
    {
        get
        {
            var result = new List<ImageData>();
            if (CurrentResult == null) return result;

            foreach (var duplicate in CurrentResult.Duplicates)
            {
                var image = FindImage(duplicate.Path);
                if (image != null) result.Add(image);
            }
            return result;
        }
    }

    // ===== 命令 =====

    public ICommand LoadImagesCommand { get; private set; } = null!;
    public ICommand CalculateFeaturesCommand { get; private set; } = null!;
    public ICommand CompareFeaturesCommand { get; private set; } = null!;
    public ICommand StopCommand { get; private set; } = null!;
    public ICommand ExecuteDeduplicationCommand { get; private set; } = null!;
    public ICommand NavigatePreviousCommand { get; private set; } = null!;
    public ICommand NavigateNextCommand { get; private set; } = null!;
    public ICommand DeleteDuplicateCommand { get; private set; } = null!;
    public ICommand UndoDeleteCommand { get; private set; } = null!;
    public ICommand ClearLogCommand { get; private set; } = null!;
    public ICommand ExportLogCommand { get; private set; } = null!;

    public ICommand SearchDuplicatesCommand { get; private set; } = null!;
    public ICommand CompareImagesCommand { get; private set; } = null!;
    public ICommand DeleteOriginalFileCommand { get; private set; } = null!;
    public ICommand SwapImagesCommand { get; private set; } = null!;
    public ICommand ReplaceImagesCommand { get; private set; } = null!;
    public ICommand RemoveAllFromListCommand { get; private set; } = null!;
    public ICommand RemoveFromListCommand { get; private set; } = null!;
    public ICommand DeleteDuplicateFileCommand { get; private set; } = null!;
    public ICommand SwapWithOriginalCommand { get; private set; } = null!;

    public ICommand NavigateDuplicatePreviousCommand { get; private set; } = null!;
    public ICommand NavigateDuplicateNextCommand { get; private set; } = null!;

    private AsyncRelayCommand? _asyncLoad;
    private AsyncRelayCommand? _asyncCalculate;
    private AsyncRelayCommand? _asyncCompare;
    private AsyncRelayCommand? _asyncExecute;
    private AsyncRelayCommand? _asyncUndo;

    private void InitializeCommands()
    {
        void OnError(Exception ex) => _logService.LogError($"操作失败: {ex.Message}");

        _asyncLoad = new AsyncRelayCommand(LoadImagesAsync, CanLoadImages, OnError);
        _asyncCalculate = new AsyncRelayCommand(CalculateFeaturesAsync, CanCalculateFeatures, OnError);
        _asyncCompare = new AsyncRelayCommand(CompareFeaturesAsync, CanCompareFeatures, OnError);
        _asyncExecute = new AsyncRelayCommand(ExecuteDeduplicationAsync, CanExecuteDeduplication, OnError);
        _asyncUndo = new AsyncRelayCommand(UndoDeleteAsync, CanUndoDelete, OnError);

        LoadImagesCommand = _asyncLoad;
        CalculateFeaturesCommand = _asyncCalculate;
        CompareFeaturesCommand = _asyncCompare;
        ExecuteDeduplicationCommand = _asyncExecute;
        UndoDeleteCommand = _asyncUndo;

        StopCommand = new RelayCommand(StopOperation, CanStopOperation);
        NavigatePreviousCommand = new RelayCommand(NavigatePrevious, CanNavigatePrevious);
        NavigateNextCommand = new RelayCommand(NavigateNext, CanNavigateNext);
        DeleteDuplicateCommand = new RelayCommand(DeleteDuplicate, CanDeleteDuplicate);
        ClearLogCommand = new RelayCommand(() => _logService.ClearLog());
        ExportLogCommand = new RelayCommand(ExportLog);

        SearchDuplicatesCommand = new RelayCommand(() => _ = SearchDuplicatesAsync(), CanSearchDuplicates);
        CompareImagesCommand = new RelayCommand(CompareImages, CanCompareImages);
        DeleteOriginalFileCommand = new RelayCommand(DeleteOriginalFile, CanDeleteOriginalFile);
        SwapImagesCommand = new RelayCommand(SwapImages, CanSwapImages);
        ReplaceImagesCommand = new RelayCommand(ReplaceImages, CanReplaceImages);
        RemoveAllFromListCommand = new RelayCommand(RemoveAllFromList, CanRemoveAllFromList);
        RemoveFromListCommand = new RelayCommand(RemoveFromList, CanRemoveFromList);
        DeleteDuplicateFileCommand = new RelayCommand(DeleteDuplicateFile, CanDeleteDuplicateFile);
        SwapWithOriginalCommand = new RelayCommand(SwapWithOriginal, CanSwapWithOriginal);

        NavigateDuplicatePreviousCommand = new RelayCommand(() => CurrentDuplicateIndex--, () => CurrentDuplicateIndex > 0);
        NavigateDuplicateNextCommand = new RelayCommand(() => CurrentDuplicateIndex++,
            () => CurrentResult != null && CurrentDuplicateIndex < CurrentResult.Duplicates.Count - 1);
    }

    /// <summary>
    /// 刷新所有依赖状态的命令可用性
    /// </summary>
    private void RefreshCommandStates()
    {
        _asyncLoad?.RaiseCanExecuteChanged();
        _asyncCalculate?.RaiseCanExecuteChanged();
        _asyncCompare?.RaiseCanExecuteChanged();
        _asyncExecute?.RaiseCanExecuteChanged();
        _asyncUndo?.RaiseCanExecuteChanged();

        (StopCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (NavigatePreviousCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (NavigateNextCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (DeleteDuplicateCommand as RelayCommandBase)?.RaiseCanExecuteChanged();

        (SearchDuplicatesCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (CompareImagesCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (DeleteOriginalFileCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (SwapImagesCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (ReplaceImagesCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (RemoveAllFromListCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (RemoveFromListCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (DeleteDuplicateFileCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (SwapWithOriginalCommand as RelayCommandBase)?.RaiseCanExecuteChanged();

        (NavigateDuplicatePreviousCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        (NavigateDuplicateNextCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
    }

    private bool CanLoadImages() => !IsBusy;
    private bool CanCalculateFeatures() => _images.Count > 0 && !IsBusy;
    private bool CanCompareFeatures() => _images.Count > 1 && !IsBusy;
    private bool CanStopOperation() => _cancellationTokenSource is { IsCancellationRequested: false };

    private bool IsBusy =>
        _asyncLoad?.IsExecuting == true
        || _asyncCalculate?.IsExecuting == true
        || _asyncCompare?.IsExecuting == true
        || _asyncExecute?.IsExecuting == true;

    private bool CanExecuteDeduplication() => DeduplicationResults.Count > 0 && !IsBusy;
    private bool CanUndoDelete() => _deletedItemsHistory.Count > 0 || !IsBusy;

    private bool CanNavigatePrevious() => CurrentIndex > 0;
    private bool CanNavigateNext() =>
        IsShowingComparisonResults ? CurrentIndex < DeduplicationResults.Count - 1
                                   : CurrentIndex < _images.Count - 1;

    private bool CanDeleteDuplicate() => CurrentIndex >= 0 && CurrentIndex < DeduplicationResults.Count;

    private bool CanSearchDuplicates() => SelectedOriginal != null && !string.IsNullOrEmpty(InputDirectory);
    private bool CanCompareImages() => SelectedOriginal != null && SelectedOriginal.Duplicates.Count > 0;
    private bool CanDeleteOriginalFile() => SelectedOriginal != null && File.Exists(SelectedOriginal.OriginalPath);
    private bool CanSwapImages() => SelectedOriginal != null && SelectedDuplicate != null;
    private bool CanReplaceImages() => SelectedOriginal != null && SelectedDuplicate != null;
    private bool CanRemoveAllFromList() => SelectedOriginal != null && SelectedOriginal.Duplicates.Count > 0;
    private bool CanRemoveFromList() => SelectedDuplicate != null;
    private bool CanDeleteDuplicateFile() => SelectedDuplicate != null && File.Exists(SelectedDuplicate.Path);
    private bool CanSwapWithOriginal() => SelectedDuplicate != null && CurrentResult != null;

    // ===== 参数默认值 =====

    private void SetDefaultParameters()
    {
        var p = new AlgorithmParameters
        {
            SortMethod = "filename"
        };

        switch (_currentAlgorithm)
        {
            case AlgorithmType.FileHash:
                p.HashAlgorithm = "SHA256";
                break;

            case AlgorithmType.AHash:
            case AlgorithmType.DHash:
            case AlgorithmType.PHash:
                p.Threshold = 0.85;
                p.HashSize = 8;
                p.HashWeight = 0.5;
                break;

            case AlgorithmType.FastDup:
                p.Threshold = 0.85;
                p.ThumbSize = 64;
                break;

            case AlgorithmType.Histogram:
                p.Threshold = 0.85;
                p.Bins = 128;
                break;

            case AlgorithmType.Template:
            case AlgorithmType.Ssim:
                p.Threshold = 0.85;
                p.WindowSize = 11;
                p.BlockSize = 200;
                break;

            case AlgorithmType.Sift:
                p.Threshold = 0.60;
                p.Features = 1000;
                p.MinMatch = 6;
                break;

            case AlgorithmType.Surf:
                p.Threshold = 0.60;
                p.HessianThreshold = 400;
                p.MinMatch = 6;
                break;

            case AlgorithmType.Lance:
                p.Threshold = 0.85;
                p.Dim = 128;
                p.Knn = 5;
                break;
        }

        AlgorithmParameters = p;
    }

    // ===== 核心流程 =====

    /// <summary>
    /// 扫描目录加载图像（仅建立索引，不解码像素）
    /// </summary>
    private async Task LoadImagesAsync()
    {
        if (string.IsNullOrWhiteSpace(InputDirectory) || !Directory.Exists(InputDirectory))
        {
            _logService.LogWarning("请先选择有效的输入目录！");
            StatusMessage = "请选择输入目录";
            return;
        }

        var cts = BeginOperation();
        try
        {
            StatusMessage = "正在扫描图像文件...";
            Progress = 0;

            var images = await Task.Run(
                () => _deduplicationService.ScanImages(InputDirectory, AlgorithmParameters.SortMethod),
                cts.Token);

            cts.Token.ThrowIfCancellationRequested();

            DisposeImages();
            _thumbnailCache.Clear();

            Images = images;
            DeduplicationResults.Clear();
            CurrentDuplicates.Clear();
            SelectedOriginal = null;
            SelectedDuplicate = null;
            CurrentIndex = -1;

            OnPropertyChanged(nameof(OriginalListSource));
            OnPropertyChanged(nameof(IsShowingComparisonResults));

            Progress = 100;
            StatusMessage = $"已加载 {images.Count} 个图像";
            _logService.LogInfo($"图像加载完成: {images.Count} 个");
        }
        finally
        {
            EndOperation(cts);
        }
    }

    /// <summary>
    /// 并行计算特征
    /// </summary>
    private async Task CalculateFeaturesAsync()
    {
        var cts = BeginOperation();
        try
        {
            _logService.LogInfo($"开始计算特征，算法: {CurrentAlgorithm}");
            StatusMessage = "正在计算特征...";
            Progress = 0;

            var progress = new Progress<int>(v => Progress = v);

            await _deduplicationService.CalculateFeaturesAsync(
                _images, CurrentAlgorithm, AlgorithmParameters, progress, cts.Token);

            Progress = 100;
            StatusMessage = "特征计算完成";
            _logService.LogInfo($"特征计算完成: {_images.Count} 个图像");
        }
        finally
        {
            EndOperation(cts);
        }
    }

    /// <summary>
    /// 并行比对特征
    /// </summary>
    private async Task CompareFeaturesAsync()
    {
        var cts = BeginOperation();
        try
        {
            StatusMessage = "正在比对特征...";
            Progress = 0;

            _logService.LogInfo($"开始比对特征，算法: {CurrentAlgorithm}");

            var progress = new Progress<int>(v => Progress = v);
            var results = await _deduplicationService.CompareFeaturesAsync(
                _images, CurrentAlgorithm, AlgorithmParameters, progress, cts.Token);

            Progress = 100;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DeduplicationResults.Clear();
                foreach (var result in results)
                {
                    DeduplicationResults.Add(result);
                }

                OnPropertyChanged(nameof(OriginalListSource));
                OnPropertyChanged(nameof(IsShowingComparisonResults));

                CurrentIndex = DeduplicationResults.Count > 0 ? 0 : -1;

                StatusMessage = results.Count > 0
                    ? $"找到 {results.Count} 组重复图像"
                    : "未找到重复图像";

                _logService.LogInfo($"特征比对完成: {results.Count} 组重复图像");
                RefreshCommandStates();
            });
        }
        finally
        {
            EndOperation(cts);
        }
    }

    /// <summary>
    /// 执行去重
    /// </summary>
    private async Task ExecuteDeduplicationAsync()
    {
        var cts = BeginOperation();
        try
        {
            _logService.LogInfo("开始执行去重操作，模式: delete");
            StatusMessage = "正在执行去重操作...";
            Progress = 0;

            var progress = new Progress<int>(v => Progress = v);

            await _deduplicationService.ExecuteDeduplicationAsync(
                DeduplicationResults.ToList(),
                OutputDirectory,
                "delete",
                _fileOperationService,
                _recycleBinService,
                progress,
                cts.Token);

            Progress = 100;
            StatusMessage = "去重操作完成";
            _logService.LogInfo("去重操作完成");
        }
        finally
        {
            EndOperation(cts);
        }
    }

    private CancellationTokenSource BeginOperation()
    {
        _cancellationTokenSource = _deduplicationService.CreateCancellationTokenSource();
        (StopCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
        return _cancellationTokenSource;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_cancellationTokenSource, cts))
        {
            _cancellationTokenSource = null;
        }

        (StopCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
    }

    private void StopOperation()
    {
        _deduplicationService.CancelOperation();
        (StopCommand as RelayCommandBase)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 释放当前持有的全部图像资源
    /// </summary>
    private void DisposeImages()
    {
        foreach (var image in _images)
        {
            image.Dispose();
        }
    }

    // ===== 导航 =====

    private void NavigatePrevious()
    {
        if (CurrentIndex > 0)
            CurrentIndex--;
    }

    private void NavigateNext()
    {
        int maxIndex = IsShowingComparisonResults ? DeduplicationResults.Count - 1 : _images.Count - 1;
        if (CurrentIndex < maxIndex)
            CurrentIndex++;
    }

    private void UpdateSelectedDuplicate()
    {
        if (CurrentDuplicates.Count > 0 && _currentDuplicateIndex >= 0 && _currentDuplicateIndex < CurrentDuplicates.Count)
        {
            SelectedDuplicate = CurrentDuplicates[_currentDuplicateIndex];
        }
        else
        {
            SelectedDuplicate = null;
        }
    }

    private void UpdateCurrentDuplicates()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateCurrentDuplicates);
            return;
        }

        CurrentDuplicates.Clear();

        if (CurrentResult != null)
        {
            foreach (var duplicate in CurrentResult.Duplicates)
            {
                CurrentDuplicates.Add(duplicate);
            }
            CurrentDuplicateIndex = 0;
        }

        OnPropertyChanged(nameof(CurrentDuplicates));
    }

    /// <summary>
    /// 为选中项预取缩略图
    /// </summary>
    private async Task PrefetchThumbnailAsync()
    {
        var image = _selectedImage;
        if (image == null || image.HasThumbnail)
            return;

        var data = image;
        try
        {
            bool created = await data.EnsureThumbnailAsync(_thumbnailCache);
            if (!created) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnPropertyChanged(nameof(SelectedImageBitmap));
                OnPropertyChanged(nameof(OriginalImage));
                OnPropertyChanged(nameof(DuplicateImages));
                OnPropertyChanged(nameof(OriginalListSource));
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"缩略图加载失败: {ex.Message}");
        }
    }

    // ===== 文件操作 =====

    private ImageData? FindImage(string path)
    {
        foreach (var image in _images)
        {
            if (string.Equals(image.FilePath, path, StringComparison.Ordinal))
                return image;
        }
        return null;
    }

private void DeleteDuplicate()
    {
    if (CurrentIndex < 0 || CurrentIndex >= DeduplicationResults.Count)
     return;

      var result = DeduplicationResults[CurrentIndex];

        foreach (var duplicate in result.Duplicates)
        {
            // 必须先记录原始路径，再改写为回收站路径。
            // 顺序颠倒会让撤回时把文件还原到回收站自身，造成文件丢失。
    string originalPath = duplicate.Path;

  var recyclePath = _recycleBinService.MoveToRecycleBin(originalPath);
       if (recyclePath != null)
   {
       _deletedItemsHistory.Push(new DeletedItemInfo(originalPath, recyclePath, result, duplicate));

    duplicate.Path = recyclePath;
        }
   else
 {
    _logService.LogWarning($"移入回收站失败，跳过: {originalPath}");
   }
        }

        result.IsDeleted = true;
        StatusMessage = "重复项已删除";
        _logService.Log($"重复项已删除: {result.OriginalPath}");
        RefreshCommandStates();
    }

    /// <summary>
    /// 撤回删除：从回收站恢复文件
    /// </summary>
    private async Task UndoDeleteAsync()
    {
        if (_deletedItemsHistory.Count == 0)
        {
            StatusMessage = "没有可恢复的删除记录";
            _logService.Log("没有可恢复的删除记录");
            return;
        }

        int restored = 0;

while (_deletedItemsHistory.Count > 0)
        {
            var info = _deletedItemsHistory.Pop();

  // 恢复失败（文件丢失/被占用）时保留记录，提示用户而非静默丢弃
            if (!_recycleBinService.RestoreFromRecycleBin(info.RecyclePath, info.FilePath))
      {
   _logService.LogWarning($"恢复失败: {info.FilePath}");
       continue;
    }

            restored++;

            await Dispatcher.UIThread.InvokeAsync(() =>
    {
  // 恢复后把路径改回原始位置，否则该项仍指向回收站路径
          info.DuplicateItem.Path = info.FilePath;

     if (!DeduplicationResults.Contains(info.ParentResult))
            DeduplicationResults.Add(info.ParentResult);

       if (!info.ParentResult.Duplicates.Contains(info.DuplicateItem))
     info.ParentResult.Duplicates.Add(info.DuplicateItem);

  info.ParentResult.IsDeleted = false;

                if (CurrentResult == info.ParentResult)
     {
       if (!CurrentDuplicates.Contains(info.DuplicateItem))
   CurrentDuplicates.Add(info.DuplicateItem);

                    OnPropertyChanged(nameof(CurrentDuplicates));
           }
         });
        }

        StatusMessage = $"已恢复 {restored} 个文件";
        _logService.Log($"已从回收站恢复 {restored} 个文件");
        RefreshCommandStates();
    }

private async Task SearchDuplicatesAsync()
    {
        if (SelectedOriginal == null) return;

      /*
         * 这三步会更新绑定到 UI 的属性（StatusMessage / Progress /
         * DeduplicationResults / Images），因此必须保持在 UI 线程上。
         * 不能加 ConfigureAwait(false) —— 之后的代码若跑在后台线程，
         * 修改这些属性会让 Avalonia 抛 "Call from invalid thread"。
         */
        await LoadImagesAsync();
        await CalculateFeaturesAsync();
        await CompareFeaturesAsync();
  }

    private void CompareImages()
    {
        StatusMessage = "比对功能待实现";
        _logService.Log("比对功能待实现: 需要打开比对对话框");
    }

    private void DeleteOriginalFile()
    {
        if (SelectedOriginal == null) return;

        var recyclePath = _recycleBinService.MoveToRecycleBin(SelectedOriginal.OriginalPath);
        if (recyclePath == null) return;

        _logService.Log($"原图已删除: {SelectedOriginal.OriginalPath}");
        StatusMessage = "原图已删除";

        var target = SelectedOriginal;
        Dispatcher.UIThread.Post(() =>
        {
            DeduplicationResults.Remove(target);
            OnPropertyChanged(nameof(OriginalListSource));
            OnPropertyChanged(nameof(IsShowingComparisonResults));
            RefreshCommandStates();
        });
    }

/// <summary>
    /// 交换两个文件的文件名
    /// </summary>
    /// <remarks>
    /// 采用「临时文件 + 三步移动」，并对每一步失败做完整回滚：
    /// 任一步失败都保证两个文件都回到原位，不会出现文件滞留在 .tmp 或丢失。
    /// </remarks>
    private void SwapImages()
    {
        if (SelectedOriginal == null || SelectedDuplicate == null) return;

     string originalPath = SelectedOriginal.OriginalPath;
        string duplicatePath = SelectedDuplicate.Path;

    if (!File.Exists(originalPath) || !File.Exists(duplicatePath))
  {
   StatusMessage = "交换失败：文件不存在";
  _logService.LogWarning($"交换失败，文件不存在: {originalPath} 或 {duplicatePath}");
      return;
        }

     // 临时文件放在原图同目录，确保同一磁盘，移动为原子重命名
        string tempPath = Path.Combine(
    Path.GetDirectoryName(originalPath) ?? string.Empty,
        $"{Path.GetFileName(originalPath)}.{Guid.NewGuid():N}.swap.tmp");

    try
  {
            // 步骤1：原图 -> 临时
            File.Move(originalPath, tempPath);

      try
        {
              // 步骤2：重复图 -> 原图位置
                File.Move(duplicatePath, originalPath);

                try
    {
   // 步骤3：临时 -> 重复图位置
            File.Move(tempPath, duplicatePath);
       }
       catch
   {
     // 回滚：先把重复图移回，再把原图归位
     if (File.Exists(originalPath))
       File.Move(originalPath, duplicatePath);
      if (File.Exists(tempPath))
      File.Move(tempPath, originalPath);
         throw;
       }
     }
      catch
{
       // 步骤2 失败：原图从临时移回
         if (File.Exists(tempPath) && !File.Exists(originalPath))
             File.Move(tempPath, originalPath);
         throw;
        }

    _logService.LogInfo($"已交换文件名: {originalPath} <-> {duplicatePath}");
        StatusMessage = "文件名已交换";
        RefreshCommandStates();
     }
        catch (Exception ex)
        {
        StatusMessage = $"交换文件名失败: {ex.Message}";
       _logService.LogError($"交换文件名失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 交换两个文件的内容
    /// </summary>
    private void ReplaceImages()
    {
        if (SelectedOriginal == null || SelectedDuplicate == null) return;

        string originalPath = SelectedOriginal.OriginalPath;
        string duplicatePath = SelectedDuplicate.Path;

        try
        {
            // 先写临时文件，任一步失败都不会破坏原文件
            byte[] originalBytes = File.ReadAllBytes(originalPath);
            byte[] duplicateBytes = File.ReadAllBytes(duplicatePath);

            string tempPath = Path.Combine(
                Path.GetDirectoryName(originalPath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(originalPath)}.swap.tmp");

            File.WriteAllBytes(tempPath, duplicateBytes);
            File.Move(tempPath, originalPath, overwrite: true);

            tempPath = Path.Combine(
                Path.GetDirectoryName(duplicatePath) ?? string.Empty,
                $"{Path.GetFileNameWithoutExtension(duplicatePath)}.swap.tmp");

            File.WriteAllBytes(tempPath, originalBytes);
            File.Move(tempPath, duplicatePath, overwrite: true);

            _logService.Log($"已交换文件内容: {originalPath} <-> {duplicatePath}");
            StatusMessage = "文件内容已交换";
        }
        catch (Exception ex)
        {
            StatusMessage = $"交换文件内容失败: {ex.Message}";
            _logService.LogError($"交换文件内容失败: {ex.Message}");
        }
    }

    private void RemoveAllFromList()
    {
        if (SelectedOriginal == null) return;

        int count = SelectedOriginal.Duplicates.Count;
        var target = SelectedOriginal;

        foreach (var duplicate in target.Duplicates.ToList())
        {
            target.Duplicates.Remove(duplicate);
        }

        _logService.Log($"已从重复列表中移除 {count} 个图像");
        StatusMessage = $"已移除 {count} 个重复图像";

        Dispatcher.UIThread.Post(() =>
        {
            if (target.Duplicates.Count == 0)
            {
                DeduplicationResults.Remove(target);
                OnPropertyChanged(nameof(OriginalListSource));
                OnPropertyChanged(nameof(IsShowingComparisonResults));
            }
            RefreshCommandStates();
        });
    }

    private void RemoveFromList()
    {
        if (SelectedDuplicate == null || CurrentResult == null) return;

        var duplicateToRemove = SelectedDuplicate;
        var currentResult = CurrentResult;

        currentResult.Duplicates.Remove(duplicateToRemove);
        CurrentDuplicates.Remove(duplicateToRemove);
        SelectedDuplicate = null;

        _logService.Log($"已从重复列表中移除: {duplicateToRemove.Path}");
        StatusMessage = "已移出重复列表";

        if (currentResult.Duplicates.Count == 0)
        {
            DeduplicationResults.Remove(currentResult);
            OnPropertyChanged(nameof(OriginalListSource));
            OnPropertyChanged(nameof(IsShowingComparisonResults));
        }

        RefreshCommandStates();
    }

    private void DeleteDuplicateFile()
    {
        if (SelectedDuplicate == null || CurrentResult == null) return;

        string deletedPath = SelectedDuplicate.Path;
        var parentResult = CurrentResult;
        var duplicateItem = SelectedDuplicate;

        var recyclePath = _recycleBinService.MoveToRecycleBin(deletedPath);
        if (recyclePath == null) return;

        _deletedItemsHistory.Push(new DeletedItemInfo(deletedPath, recyclePath, parentResult, duplicateItem));

        parentResult.Duplicates.Remove(duplicateItem);
        CurrentDuplicates.Remove(duplicateItem);
        SelectedDuplicate = null;

        _logService.Log($"重复图像已删除: {deletedPath}");
        StatusMessage = "重复图像已删除";

        if (parentResult.Duplicates.Count == 0)
        {
            DeduplicationResults.Remove(parentResult);
            OnPropertyChanged(nameof(OriginalListSource));
            OnPropertyChanged(nameof(IsShowingComparisonResults));
        }

        OnPropertyChanged(nameof(CurrentDuplicates));
        RefreshCommandStates();
    }

    /// <summary>
    /// 将重复图像与原图对调，并重算该组的相似度
    /// </summary>
    private void SwapWithOriginal()
    {
        if (SelectedDuplicate == null || CurrentResult == null) return;

        string originalPath = CurrentResult.OriginalPath;
        string duplicatePath = SelectedDuplicate.Path;

        var originalImage = FindImage(originalPath);
        var duplicateImage = FindImage(duplicatePath);

        if (originalImage == null || duplicateImage == null)
        {
            StatusMessage = "对调失败: 找不到图像数据";
            _logService.LogWarning("对调失败: 找不到图像数据");
            return;
        }

        _logService.LogInfo($"开始对调: 原图={originalPath}, 重复图像={duplicatePath}");

        var newResult = new DeduplicationResult
        {
            OriginalPath = duplicatePath,
            Duplicates = new List<DuplicateItem>()
        };

        // 原图降级为重复项
        double originalSimilarity = _deduplicationService.CalculateSimilarityPublic(
            duplicateImage, originalImage, CurrentAlgorithm, AlgorithmParameters);
        newResult.Duplicates.Add(new DuplicateItem { Path = originalPath, Similarity = originalSimilarity });

        // 其余重复项相对新原图重新计算相似度
        foreach (var dup in CurrentResult.Duplicates)
        {
            if (dup.Path == duplicatePath) continue;

            var dupImage = FindImage(dup.Path);
            if (dupImage == null) continue;

            double similarity = _deduplicationService.CalculateSimilarityPublic(
                duplicateImage, dupImage, CurrentAlgorithm, AlgorithmParameters);

            newResult.Duplicates.Add(new DuplicateItem { Path = dup.Path, Similarity = similarity });
        }

        int resultIndex = DeduplicationResults.IndexOf(CurrentResult);
        if (resultIndex >= 0)
        {
            DeduplicationResults[resultIndex] = newResult;
        }

        CurrentDuplicates.Clear();
        DuplicateItem? originalAsDuplicate = null;

        foreach (var duplicate in newResult.Duplicates)
        {
            CurrentDuplicates.Add(duplicate);
            if (duplicate.Path == originalPath)
                originalAsDuplicate = duplicate;
        }

        SelectedDuplicate = originalAsDuplicate;
        CurrentIndex = resultIndex >= 0 ? resultIndex : CurrentIndex;
        SelectedImage = duplicateImage;

        _logService.LogInfo($"对调完成: 新原图={duplicatePath}");
        StatusMessage = "原图对调成功";

        OnPropertyChanged(nameof(CurrentDuplicates));
        OnPropertyChanged(nameof(OriginalListSource));
        RefreshCommandStates();
    }

    // ===== 目录选择 =====

    /// <summary>
    /// 选择输入目录并加载
    /// </summary>
    public async void SelectInputDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        InputDirectory = directory;
        _logService.Log($"输入目录: {directory}");

        if (_asyncLoad is { IsExecuting: false } && _asyncLoad.CanExecute(null))
            await _asyncLoad.ExecuteAsyncSafe();
    }

    public void SelectOutputDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        OutputDirectory = directory;
        _logService.Log($"输出目录: {directory}");
    }

    /// <summary>
    /// 加载默认图像目录（启动时调用）
    /// </summary>
    public void LoadDefaultDirectory()
    {
        if (string.IsNullOrEmpty(InputDirectory) || !Directory.Exists(InputDirectory))
        {
            _logService.LogWarning($"默认图像目录不存在或为空: {InputDirectory}");
            return;
        }

        _logService.LogInfo($"自动加载默认图像目录: {InputDirectory}");

        if (_asyncLoad is { IsExecuting: false } && _asyncLoad.CanExecute(null))
            _ = _asyncLoad.ExecuteAsyncSafe();
    }

    public AlgorithmParameters GetAlgorithmParameters() => AlgorithmParameters;

    /// <summary>
    /// 导出日志
    /// </summary>
    private void ExportLog()
    {
        try
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            var mainWindow = lifetime?.MainWindow;
            var storageProvider = mainWindow?.StorageProvider;

            if (storageProvider == null || !storageProvider.CanSave)
                return;

            _ = ExportLogAsync(storageProvider);
        }
        catch (Exception ex)
        {
            _logService.LogError($"导出日志失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 异步导出日志
    /// </summary>
    /// <remarks>
    /// 原实现使用了 <c>.Result</c> 同步等待，在 UI 线程上会阻塞并可能死锁。
    /// </remarks>
    private async Task ExportLogAsync(Avalonia.Platform.Storage.IStorageProvider storageProvider)
    {
        try
        {
            var file = await storageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "导出日志",
                DefaultExtension = "log",
                SuggestedFileName = $"deduplicator_log_{DateTime.Now:yyyyMMdd_HHmmss}.log",
                FileTypeChoices = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("日志文件 (*.log)")
                    {
                        Patterns = new[] { "*.log" }
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType("文本文件 (*.txt)")
                    {
                        Patterns = new[] { "*.txt" }
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType("所有文件 (*.*)")
                    {
                        Patterns = new[] { "*.*" }
                    }
                }
            });

            if (file?.Path.LocalPath is { } path)
            {
                _logService.ExportLog(path);
            }
        }
        catch (Exception ex)
        {
            _logService.LogError($"导出日志失败: {ex.Message}");
        }
    }
}