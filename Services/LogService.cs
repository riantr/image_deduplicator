using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Deduplicator.Services;

/// <summary>
/// 日志级别
/// </summary>
public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// 日志项，包含消息和级别
/// </summary>
public class LogItem
{
    public string Message { get; set; } = string.Empty;
    public LogLevel Level { get; set; }
    public string Timestamp { get; set; } = string.Empty;
    public string FormattedMessage => $"[{Timestamp}] [{Level}] {Message}";
}

/// <summary>
/// 日志服务，提供日志记录和可观察的日志流
/// </summary>
public class LogService : IDisposable
{
    private readonly ObservableCollection<string> _logMessages = new();
    private readonly StringBuilder _logBuffer = new();
    private readonly object _lock = new();
    private string? _logFilePath;
    private bool _disposed;

    // UI 批量提交缓冲：避免每条日志都 Post 一次到 UI 线程
    private readonly List<string> _pendingUiMessages = new();
    private readonly object _uiLock = new();
    private int _flushScheduled;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(150);
    
    /// <summary>
    /// 获取日志消息集合
    /// </summary>
    public ObservableCollection<string> LogMessages => _logMessages;
    
    /// <summary>
    /// 最大日志条数
    /// </summary>
    public int MaxLogCount { get; set; } = 1000;
    
    /// <summary>
    /// 是否启用文件日志
    /// </summary>
    public bool EnableFileLog { get; set; } = true;
    
    /// <summary>
    /// 构造函数
    /// </summary>
    public LogService()
    {
        InitializeLogFile();
    }
    
    /// <summary>
    /// 初始化日志文件
    /// </summary>
    private void InitializeLogFile()
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Deduplicator",
                "Logs"
            );
            
            if (!Directory.Exists(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
            
            var fileName = $"deduplicator_{DateTime.Now:yyyyMMdd_HHmmss}.log";
            _logFilePath = Path.Combine(logDir, fileName);
            
            // 写入文件头
            var header = $"图像去重工具日志 - {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        $"日志文件: {_logFilePath}\n" +
                        "==========================================\n\n";
            File.WriteAllText(_logFilePath, header);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"初始化日志文件失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 添加调试日志
    /// </summary>
    public void LogDebug(string message)
    {
        Log(message, LogLevel.Debug);
    }
    
    /// <summary>
    /// 添加信息日志
    /// </summary>
    public void LogInfo(string message)
    {
        Log(message, LogLevel.Info);
    }
    
    /// <summary>
    /// 添加警告日志
    /// </summary>
    public void LogWarning(string message)
    {
        Log(message, LogLevel.Warning);
    }
    
    /// <summary>
    /// 添加错误日志
    /// </summary>
    public void LogError(string message)
    {
        Log(message, LogLevel.Error);
    }
    
    /// <summary>
    /// 添加日志消息（兼容旧版本）
    /// </summary>
    /// <param name="message">日志消息</param>
    public void Log(string message)
    {
        Log(message, LogLevel.Info);
    }
    
    /// <summary>
    /// 添加日志消息（带级别）
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="level">日志级别</param>
    public void Log(string message, LogLevel level)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var logItem = new LogItem
        {
            Message = message,
            Level = level,
            Timestamp = timestamp
        };
        
var levelPrefix = GetLevelPrefix(level);
        var logEntry = $"[{timestamp}] {levelPrefix} {message}";

        // 排队后批量提交，避免高频日志（如加载上千张图）造成 UI 调度风暴
        QueueForUi(logEntry);

        // 写入文件
        WriteToFile(logItem);
    }

    /// <summary>
    /// 裁剪日志集合到上限
    /// </summary>
    /// <remarks>
    /// ObservableCollection 没有 RemoveRange，只能逐项 RemoveAt。
    /// 这里一次性算出差值后连续移除，避免每条都做差值计算。
    /// </remarks>
    private void TrimToLimit()
    {
        int excess = _logMessages.Count - MaxLogCount;
        for (int i = 0; i < excess; i++)
        {
            _logMessages.RemoveAt(0);
        }
    }

    /// <summary>
    /// 将日志加入 UI 提交队列，按固定间隔批量刷新
    /// </summary>
    private void QueueForUi(string logEntry)
    {
        lock (_uiLock)
        {
            _pendingUiMessages.Add(logEntry);

            // 已有刷新任务在排队，无需重复调度
            if (Interlocked.Exchange(ref _flushScheduled, 1) == 1)
                return;
        }

        _ = FlushLoopAsync();
    }

    /// <summary>
    /// 周期性把队列中的日志提交到 UI 线程
    /// </summary>
    private async Task FlushLoopAsync()
    {
        try
        {
            await Task.Delay(FlushInterval).ConfigureAwait(false);

            List<string> batch;
            lock (_uiLock)
            {
                batch = new List<string>(_pendingUiMessages);
                _pendingUiMessages.Clear();
                Interlocked.Exchange(ref _flushScheduled, 0);

                // 期间又产生了新日志，继续下一轮
                if (_pendingUiMessages.Count > 0)
                {
                    Interlocked.Exchange(ref _flushScheduled, 1);
                }
            }

            if (batch.Count > 0)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    foreach (var entry in batch)
                    {
                        _logMessages.Add(entry);
                    }

// 超量时裁剪到上限
      TrimToLimit();
    });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"刷新日志到 UI 失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 获取日志级别前缀
    /// </summary>
    private string GetLevelPrefix(LogLevel level)
    {
        return level switch
        {
            LogLevel.Debug => "[DEBUG]",
            LogLevel.Info => "[INFO]",
            LogLevel.Warning => "[WARN]",
            LogLevel.Error => "[ERROR]",
            _ => "[INFO]"
        };
    }
    
    /// <summary>
    /// 写入日志到文件
    /// </summary>
    private void WriteToFile(LogItem logItem)
    {
        if (!EnableFileLog || string.IsNullOrEmpty(_logFilePath))
            return;
        
        try
        {
            lock (_lock)
            {
                var logLine = logItem.FormattedMessage + "\n";
                _logBuffer.Append(logLine);
                
                // 每 10 条日志或遇到错误时刷新到文件
                if (_logBuffer.Length > 0 && 
                    (logItem.Level == LogLevel.Error || _logBuffer.Length > 5000))
                {
                    FlushToFile();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"写入日志文件失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 刷新缓冲区到文件
    /// </summary>
    private void FlushToFile()
    {
        if (string.IsNullOrEmpty(_logFilePath) || _logBuffer.Length == 0)
            return;
        
        try
        {
            File.AppendAllText(_logFilePath, _logBuffer.ToString());
            _logBuffer.Clear();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"刷新日志文件失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 清空日志
    /// </summary>
    public void ClearLog()
    {
        // 同时清空待提交队列，否则已排队但未显示的日志会重新出现
        lock (_uiLock)
        {
            _pendingUiMessages.Clear();
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _logMessages.Clear();
        });

        LogInfo("日志已清空");
    }
    
    /// <summary>
    /// 导出日志到文件
    /// </summary>
    /// <param name="filePath">目标文件路径</param>
    public void ExportLog(string filePath)
    {
        try
        {
            var exportDir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(exportDir) && !Directory.Exists(exportDir))
            {
                Directory.CreateDirectory(exportDir);
            }
            
            var content = $"图像去重工具日志导出\n" +
                        $"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                        "==========================================\n\n";
            
            foreach (var message in _logMessages)
            {
                content += message + "\n";
            }
            
            File.WriteAllText(filePath, content, Encoding.UTF8);
            LogInfo($"日志已导出到: {filePath}");
        }
        catch (Exception ex)
        {
            LogError($"导出日志失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 获取日志文件路径
    /// </summary>
    public string? GetLogFilePath()
    {
        return _logFilePath;
    }
    
    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            // 关闭前把队列中剩余日志刷到 UI，避免丢失
            FlushPendingToUi();
            FlushToFile();
            _disposed = true;
        }
    }

    /// <summary>
    /// 立即把待提交日志刷到 UI 线程
    /// </summary>
    private void FlushPendingToUi()
    {
        List<string>? batch = null;

        lock (_uiLock)
        {
            if (_pendingUiMessages.Count > 0)
            {
                batch = new List<string>(_pendingUiMessages);
                _pendingUiMessages.Clear();
            }
        }

        if (batch == null) return;

        try
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                foreach (var entry in batch)
                {
                    _logMessages.Add(entry);
                }

TrimToLimit();
            });
        }
        catch
        {
            // UI 已关闭时忽略
        }
    }
}