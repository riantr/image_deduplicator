using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Deduplicator.Services;
using Deduplicator.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Deduplicator;

public partial class App : Application
{
    public static ServiceProvider? ServiceProvider { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 配置依赖注入
        var services = new ServiceCollection();
        
        // 注册服务
        services.AddSingleton<LogService>();
        services.AddSingleton<FileOperationService>();
        services.AddSingleton<RecycleBinService>();
        services.AddSingleton<DeduplicationService>();
        services.AddSingleton<ThumbnailCache>();

        // 注册 ViewModel
        services.AddTransient<ViewModels.MainViewModel>();
        
        ServiceProvider = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}