using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Deduplicator.Converters;
using Deduplicator.Models;
using Deduplicator.Views;

namespace Deduplicator.Tests;

/// <summary>
/// UI 层自动化验证。
///
/// <para>UI 重构后，XAML 只在运行时才真正解析 —— 编译通过不代表能跑起来。
/// 本类用 Avalonia Headless 在无显示环境下构建整个控件树，
/// 借此暴露资源缺失、样式选择器无效、绑定路径错误等问题。</para>
/// </summary>
public static class UiTests
{
    public static void Run()
    {
        Assert.Section("UI 层（无头环境）");

try
  {
      AppBuilder.Configure<App>()
   .UseHeadless(new AvaloniaHeadlessPlatformOptions())
      .UseSkia()
           .SetupWithoutStarting();

            // SetupWithoutStarting 内部已走完 App 初始化，
      // 即 App.axaml 中的资源字典与 Controls.axaml 样式都已加载。
   // 只要它不抛异常，就说明资源路径与样式选择器都是有效的。
            Assert.NotNull(Application.Current, "应用实例已创建（资源与样式加载成功）");
            Assert.NotNull(Application.Current!.Styles, "应用样式集合可用");

            Assert.That(true, "应用与样式加载成功");
        }
        catch (Exception ex)
        {
            Assert.That(false, "应用与样式加载成功", $"{ex.GetType().Name}: {ex.Message}");
            return;
        }

        // 转换器逻辑
        ConverterTests();

        // 命令的线程安全
        CommandThreadTests();

        // 窗口控件树
        try
        {
            var window = new MainWindow();
            Assert.NotNull(window, "主窗口可实例化");

            Assert.That(FindByType<Grid>(window) != null, "控件树已构建（存在根 Grid）");

            var algoCombo = FindAll<ComboBox>(window).ToList();
            Assert.True(algoCombo.Count >= 1,
                $"算法下拉框存在（找到 {algoCombo.Count} 个 ComboBox）");

            var buttons = FindAll<Button>(window).ToList();
            Assert.True(buttons.Count >= 10,
                $"主要操作按钮齐全（找到 {buttons.Count} 个 Button）");

            var sliders = FindAll<Slider>(window).ToList();
            Assert.True(sliders.Count >= 5,
                $"参数滑块齐全（找到 {sliders.Count} 个 Slider）");

            window.Close();
        }
        catch (Exception ex)
        {
            Assert.That(false, "主窗口可实例化", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

      /// <summary>
    /// 验证命令的 CanExecuteChanged 在后台线程触发时不会崩溃。
    ///
  /// <para>这是真实踩过的坑：<c>ConfigureAwait(false)</c> 让 await 之后的代码
    /// 运行在后台线程，此时设置 <c>IsExecuting</c> 会触发 <c>CanExecuteChanged</c>，
    /// 而该事件会去读 Avalonia 控件属性 → 抛
  /// <c>InvalidOperationException: Call from invalid thread</c>。</para>
    ///
  /// <para>异步执行 + 后台触发是必经路径，这里直接模拟该场景。</para>
 /// </summary>
    private static void CommandThreadTests()
    {
        try
        {
            int invoked = 0;
            var cmd = new Helpers.AsyncRelayCommand(
                execute: () => Task.CompletedTask,
canExecute: () => true);

   cmd.CanExecuteChanged += (_, _) => Interlocked.Increment(ref invoked);

            Exception? threadError = null;

            // 模拟后台线程触发（修复前这里会抛
            // InvalidOperationException: Call from invalid thread）
var t = new Thread(() =>
     {
            try
            {
          cmd.RaiseCanExecuteChanged();
        }
       catch (Exception ex)
          {
           threadError = ex;
         }
       });
  t.Start();
      t.Join(3000);

  Assert.That(threadError == null,
   "后台线程触发 CanExecuteChanged 不崩溃",
    threadError?.GetType().Name + ": " + threadError?.Message);

            if (threadError != null)
      return;

  // 事件通过 Dispatcher.Post 异步派发到 UI 线程，
            // 这里等待 UI 线程处理完队列再断言
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();

      Assert.True(invoked > 0,
                "CanExecuteChanged 已派发到 UI 线程");
      }
        catch (Exception ex)
        {
            Assert.That(false, "命令线程安全检查通过", ex.Message);
  }
    }

  private static void ConverterTests()
    {
        var hashSize = new ShowHashSizeConverter();
        var windowSize = new ShowWindowSizeConverter();
        var minMatch = new ShowMinMatchConverter();
        var surfOnly = new ShowHessianConverter();
        var lanceOnly = new ShowDimConverter();

        // 每个算法都应该能解析出可见性，不抛异常
        foreach (AlgorithmType algo in Enum.GetValues<AlgorithmType>())
        {
            try
            {
                hashSize.Convert(algo, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture);
                windowSize.Convert(algo, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture);
                minMatch.Convert(algo, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture);
                surfOnly.Convert(algo, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture);
                lanceOnly.Convert(algo, typeof(bool), null, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                Assert.That(false, $"{algo} 转换器可用", ex.Message);
                return;
            }
        }
        Assert.That(true, "全部 11 种算法的参数可见性转换均正常");

        // 抽查几组预期结果
        Assert.Equal(true, hashSize.Convert(AlgorithmType.DHash, typeof(bool), null,
            System.Globalization.CultureInfo.InvariantCulture), "DHash 需要哈希大小");
        Assert.Equal(false, hashSize.Convert(AlgorithmType.Lance, typeof(bool), null,
            System.Globalization.CultureInfo.InvariantCulture), "Lance 不需要哈希大小");
        Assert.Equal(true, minMatch.Convert(AlgorithmType.Surf, typeof(bool), null,
            System.Globalization.CultureInfo.InvariantCulture), "SURF 需要最小匹配数");
        Assert.Equal(true, surfOnly.Convert(AlgorithmType.Surf, typeof(bool), null,
            System.Globalization.CultureInfo.InvariantCulture), "SURF 需要 Hessian 阈值");
        Assert.Equal(false, surfOnly.Convert(AlgorithmType.Sift, typeof(bool), null,
            System.Globalization.CultureInfo.InvariantCulture), "SIFT 不需要 Hessian 阈值");
    }

    // ---------- 视觉树遍历 ----------

    /// <summary>
    /// 遍历控件树。
    ///
    /// <para>注意：同一控件可能同时出现在逻辑树与视觉树中，
    /// 直接合并两棵树会导致重复计数（实测 Button 会被数成 1344 个）。
    /// 因此这里用引用去重。</para>
    /// </summary>
    private static List<T> FindAll<T>(Control root) where T : Control
    {
   var seen = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var result = new List<T>();
        var queue = new Queue<Control>();

        queue.Enqueue(root);

        while (queue.Count > 0)
        {
     var current = queue.Dequeue();

      // 同一控件可能在逻辑树与视觉树各出现一次
   if (!seen.Add(current))
       continue;

    if (current is T typed)
         result.Add(typed);

            foreach (var child in current.GetLogicalChildren().OfType<Control>())
        queue.Enqueue(child);

      foreach (var child in current.GetVisualChildren().OfType<Control>())
   queue.Enqueue(child);
        }

    return result;
    }

    private static T? FindByType<T>(Control root) where T : Control
        => FindAll<T>(root).FirstOrDefault();
}
