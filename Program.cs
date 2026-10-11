using System;
using Avalonia;
using OpenCvSharp;

namespace Deduplicator;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VerifyOpenCv();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// 启动前验证 OpenCV 原生库是否可用。
    ///
    /// <para>原实现依赖 Linux 专属的 <c>LD_LIBRARY_PATH</c> 与 <c>/usr/lib/x86_64-linux-gnu</c>，
    /// 在 Windows/macOS 上完全无效。改为使用 NuGet 包自带的原生库，
    /// 并在启动时做一次轻量自检，失败时给出明确提示而非运行中途崩溃。</para>
    /// </summary>
    private static void VerifyOpenCv()
    {
        try
        {
            using var test = new Mat(4, 4, MatType.CV_8UC3);
            test.SetTo(new Scalar(0, 0, 0));
            Cv2.GaussianBlur(test, test, new OpenCvSharp.Size(3, 3), 1.0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "OpenCV 原生库加载失败，图像算法不可用。");
            Console.Error.WriteLine($"原因: {ex.Message}");
            Console.Error.WriteLine(
                "请执行 `dotnet restore` 重新下载原生库，或确认当前平台的 OpenCvSharp4 runtime 包已安装。");
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}