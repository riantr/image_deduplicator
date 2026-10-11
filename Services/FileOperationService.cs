using System;
using System.IO;

namespace Deduplicator.Services;

/// <summary>
/// 文件操作服务，提供文件移动、删除、目录创建等功能
/// </summary>
public class FileOperationService
{
    private readonly LogService _logService;
    
    public FileOperationService(LogService logService)
    {
        _logService = logService;
    }
    
/// <summary>
    /// 移动文件
    /// </summary>
    /// <remarks>
    /// <b>重要</b>：本方法不会自动改名。目标已存在时直接返回 false。
 /// 调用方需先用 <see cref="HandleFileNameConflict"/> 取得最终路径，
 /// 再传入本方法。这样调用方拿到的返回值与实际落盘路径始终一致
///（早期实现会在此内部二次改名，导致调用方记录的路径与实际路径错位，
    /// 造成「恢复时文件丢失」的数据丢失问题）。
    /// </remarks>
    public bool MoveFile(string source, string destination)
    {
        _logService.LogDebug($"[MoveFile] 输入: {source} -> {destination}");

        try
        {
            if (!File.Exists(source))
            {
                _logService.LogWarning($"源文件不存在: {source}");
                return false;
            }

            // 目标目录确保存在
            var destDir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
            {
                Directory.CreateDirectory(destDir);
      }

       // 不在此处改名：目标已存在即视为冲突，交由调用方决策
    if (File.Exists(destination))
            {
       _logService.LogWarning($"目标文件已存在: {destination}");
      return false;
   }

    File.Move(source, destination);
            _logService.LogInfo($"文件已移动: {source} -> {destination}");
       return true;
        }
        catch (Exception ex)
        {
   _logService.LogError($"移动文件失败: {source} -> {destination}, 错误: {ex.Message}");
  return false;
  }
    }
    
    /// <summary>
    /// 删除文件
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>是否成功</returns>
    public bool DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            _logService.Log($"文件已删除: {path}");
            return true;
        }
        catch (Exception ex)
        {
            _logService.Log($"删除文件失败: {path}, 错误: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// 创建目录
    /// </summary>
    /// <param name="path">目录路径</param>
    /// <returns>是否成功</returns>
    public bool CreateDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                _logService.Log($"目录已创建: {path}");
            }
            return true;
        }
        catch (Exception ex)
        {
            _logService.Log($"创建目录失败: {path}, 错误: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// 检查文件是否存在
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>是否存在</returns>
    public bool FileExists(string path)
    {
        return File.Exists(path);
    }
    
    /// <summary>
    /// 检查目录是否存在
    /// </summary>
    /// <param name="path">目录路径</param>
    /// <returns>是否存在</returns>
    public bool DirectoryExists(string path)
    {
        return Directory.Exists(path);
    }
    
/// <summary>
    /// 生成一个不冲突的文件路径（仅查询，不移动文件）
    /// </summary>
/// <remarks>
    /// 由于「查询」与「使用」之间存在时间差，仍可能被其他进程抢占，
    /// 因此调用方必须以 <see cref="MoveFile"/> 的返回值判断是否成功。
    /// 循环设有次数上限，避免极端情况（大量同名文件）下无限自增。
    /// </remarks>
    public string HandleFileNameConflict(string directory, string filename)
    {
     if (string.IsNullOrEmpty(directory))
            return filename;

  var nameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
        var extension = Path.GetExtension(filename);
        const int MaxAttempts = 10000;

   var candidate = Path.Combine(directory, filename);
      for (int counter = 1; counter <= MaxAttempts && File.Exists(candidate); counter++)
        {
 candidate = Path.Combine(directory, $"{nameWithoutExtension}_{counter}{extension}");
        }

      return candidate;
    }
    
    /// <summary>
    /// 获取目录中的所有文件
    /// </summary>
    /// <param name="directory">目录路径</param>
    /// <param name="searchPattern">搜索模式</param>
    /// <param name="searchOption">搜索选项</param>
    /// <returns>文件列表</returns>
    public string[] GetFiles(string directory, string searchPattern = "*.*", SearchOption searchOption = SearchOption.AllDirectories)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return Array.Empty<string>();
            }
            
            return Directory.GetFiles(directory, searchPattern, searchOption);
        }
        catch (Exception ex)
        {
            _logService.Log($"获取文件列表失败: {directory}, 错误: {ex.Message}");
            return Array.Empty<string>();
        }
    }
    
    /// <summary>
    /// 获取文件大小
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns>文件大小（字节）</returns>
    public long GetFileSize(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            return fileInfo.Length;
        }
        catch (Exception ex)
        {
            _logService.Log($"获取文件大小失败: {path}, 错误: {ex.Message}");
            return 0;
        }
    }
}