using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;

namespace Deduplicator.Helpers;

/// <summary>
/// 命令基类，统一 <see cref="RaiseCanExecuteChanged"/> 的调用入口
/// </summary>
public abstract class RelayCommandBase : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public abstract bool CanExecute(object? parameter);

    public abstract void Execute(object? parameter);

    /// <summary>
    /// 触发 <see cref="CanExecuteChanged"/>。
    ///
    /// <para><b>必须切回 UI 线程</b>：Avalonia 的命令绑定在收到该事件后会读取
    /// 控件属性（如 <c>Button.CanExecute</c>），若事件在后台线程触发，
    /// 会抛 <c>InvalidOperationException: Call from invalid thread</c>。
    /// 这在异步命令里极易发生 —— <c>await</c> 之后的代码可能已不在 UI 线程。</para>
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        var dispatcher = Dispatcher.UIThread;

        if (dispatcher.CheckAccess())
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            dispatcher.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
        }
    }
}

/// <summary>
/// 同步命令
/// </summary>
public class RelayCommand : RelayCommandBase
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public override bool CanExecute(object? parameter) => _canExecute == null || _canExecute();

    public override void Execute(object? parameter) => _execute();
}

/// <summary>
/// 异步命令。
///
/// <para>原代码用 <c>new RelayCommand(async () =&gt; await FooAsync())</c> 包装异步方法，
/// 这会退化为 <c>async void</c>——异常无法被捕获、也无法等待完成，
/// 导致界面状态错乱。此类通过 <see cref="IsExecuting"/> 防止重入，并正确传播异常。</para>
/// </summary>
public class AsyncRelayCommand : RelayCommandBase
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    /// <summary>
    /// 是否正在执行
    /// </summary>
    public bool IsExecuting
    {
        get => _isExecuting;
        private set
        {
            if (_isExecuting == value) return;
            _isExecuting = value;
            RaiseCanExecuteChanged();
        }
    }

    public override bool CanExecute(object? parameter) => !_isExecuting && (_canExecute == null || _canExecute());

    public override void Execute(object? parameter)
    {
        // ICommand.Execute 必须同步返回，这里用 IsExecuting 防止并发重入
        _ = ExecuteAsyncSafe();
    }

    /// <summary>
    /// 以 Task 形式执行，供 ViewModel 内部显式 await 使用（避免 async void）
    /// </summary>
public async Task ExecuteAsyncSafe()
    {
        if (_isExecuting) return;

        IsExecuting = true;
        try
   {
     /*
       * 注意：此处<b>不能</b>用 ConfigureAwait(false)。
              *
              * ViewModel 的 Execute 委托里会更新绑定到 UI 的属性
              * （StatusMessage、Progress、DeduplicationResults 等）。
          * 若在此处切断 UI 上下文，后续代码就运行在后台线程，
              * 修改这些属性会触发跨线程的绑定更新 —— Avalonia 会抛
       * "Call from invalid thread"。
        *
              * 代价是 continuation 回到 UI 线程，对于命令这种
              * 以 I/O 与计算为主的场景完全可接受。
              */
     await _execute();
    }
  catch (OperationCanceledException)
   {
  // 取消是正常流程，不视为错误
      }
        catch (Exception ex)
        {
   _onError?.Invoke(ex);
      System.Diagnostics.Debug.WriteLine($"命令执行失败: {ex}");
        }
 finally
        {
  // 同样需要切回 UI 线程，见上
     Dispatcher.UIThread.Post(() => IsExecuting = false);
        }
    }
}