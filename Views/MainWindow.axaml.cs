using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Deduplicator.Services;
using Deduplicator.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System;

namespace Deduplicator.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel? _viewModel;
    private readonly IStorageProvider? _storageProvider;

    public MainWindow()
    {
        InitializeComponent();

        var services = App.ServiceProvider;
        if (services != null)
        {
            var logService = services.GetRequiredService<LogService>();
            var fileOperationService = services.GetRequiredService<FileOperationService>();
            var recycleBinService = services.GetRequiredService<RecycleBinService>();
            var deduplicationService = services.GetRequiredService<DeduplicationService>();

            _viewModel = new MainViewModel(
                logService,
                fileOperationService,
                recycleBinService,
                deduplicationService);

            DataContext = _viewModel;
        }

        _storageProvider = StorageProvider;
    }

    /// <summary>
 /// 窗口加载完成后自动打开默认图像目录
    /// </summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        // 延迟执行，避免在 OnLoaded 中直接调用异步方法
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _viewModel?.LoadDefaultDirectory(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 浏览输入目录
    /// </summary>
    private async void BrowseInputDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (_storageProvider == null) return;

        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择输入目录",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            _viewModel?.SelectInputDirectory(folders[0].Path.LocalPath ?? string.Empty);
        }
    }

    /// <summary>
    /// 浏览输出目录
    /// </summary>
    private async void BrowseOutputDirectory_Click(object? sender, RoutedEventArgs e)
    {
        if (_storageProvider == null) return;

        var folders = await _storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择输出目录",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            _viewModel?.SelectOutputDirectory(folders[0].Path.LocalPath ?? string.Empty);
        }
    }
    
    /// <summary>
  /// 切换浅色 / 深色主题
 /// </summary>
    /// <remarks>
    /// 主题令牌定义在 App.axaml 的 ThemeDictionaries 中，
    /// 切换 RequestedThemeVariant 后所有 DynamicResource 绑定会自动重新求值。
    /// </remarks>
    private void ToggleTheme_Click(object? sender, RoutedEventArgs e)
    {
      _viewModel?.ToggleTheme();
    }

    /// <summary>
    /// 键盘快捷键处理
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 检查 Shift 修饰键
        if (e.KeyModifiers == KeyModifiers.Shift)
        {
            if (e.Key == Key.Up)
            {
                _viewModel?.NavigateDuplicatePreviousCommand?.Execute(null);
                e.Handled = true;
                return;
            }
            else if (e.Key == Key.Down)
            {
                _viewModel?.NavigateDuplicateNextCommand?.Execute(null);
                e.Handled = true;
                return;
            }
        }
        
        base.OnKeyDown(e);
    }
}