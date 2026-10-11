using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Deduplicator.Tests;

/// <summary>
/// 无头渲染界面截图
/// </summary>
public static class UiShot
{
    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        Dispatcher.UIThread.Invoke(() =>
        {
            var window = new Views.MainWindow
            {
                Width = 1440,
                Height = 860
            };

            // 强制布局，使控件树完整
            window.Measure(new Size(1440, 860));
            window.Arrange(new Rect(0, 0, 1440, 860));

            Dispatcher.UIThread.RunJobs();

            var frame = new RenderTargetBitmap(new PixelSize(1440, 860));
            frame.Render(window);
            frame.Save(Path.Combine(outDir, "main.png"));

            Console.WriteLine("截图已保存: " + Path.Combine(outDir, "main.png"));
            window.Close();
        });
    }
}
