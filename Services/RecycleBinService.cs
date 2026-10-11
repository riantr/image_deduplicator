using System;
using System.IO;
using System.Linq;

namespace Deduplicator.Services;

/// <summary>
/// 回收站服务，提供文件移动到回收站和从回收站恢复的功能
/// </summary>
public class RecycleBinService
{
    private readonly LogService _logService;
    private readonly FileOperationService _fileOperationService;
    private string? _recycleBinDirectory;
    
    public RecycleBinService(LogService logService, FileOperationService fileOperationService)
    {
        _logService = logService;
        _fileOperationService = fileOperationService;
    }
    
    /// <summary>
    /// 获取回收站目录
    /// </summary>
    /// <returns>回收站目录路径</returns>
    public string GetRecycleBinDirectory()
    {
        if (!string.IsNullOrEmpty(_recycleBinDirectory))
        {
            return _recycleBinDirectory;
        }
        
        // 使用用户目录下的 .recycle_bin 作为回收站
        var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _recycleBinDirectory = Path.Combine(userDir, ".image_deduplicator_recycle");
        
        return _recycleBinDirectory;
    }
    
/// <summary>
 /// 移动文件到回收站
    /// </summary>
    /// <returns>回收站中的最终路径；失败返回 null</returns>
 public string? MoveToRecycleBin(string filePath)
    {
        _logService.LogDebug($"[MoveToRecycleBin] 输入: {filePath}");

        try
{
            if (!_fileOperationService.FileExists(filePath))
  {
        _logService.LogWarning($"文件不存在: {filePath}");
                return null;
}

          var recycleDir = GetRecycleBinDirectory();
            if (!_fileOperationService.CreateDirectory(recycleDir))
{
   _logService.LogError($"创建回收站目录失败: {recycleDir}");
            return null;
            }

      // 先算出无冲突的目标路径，再写 meta，最后移动 —— 顺序保证 meta 记录的路径就是实际落盘路径
        string fileName = Path.GetFileName(filePath);
            string destPath = _fileOperationService.HandleFileNameConflict(recycleDir, fileName);

    // 优先用「移动」而非「复制」，删除语义与回收站一致
        if (!_fileOperationService.MoveFile(filePath, destPath))
         {
          _logService.LogWarning($"移入回收站失败: {filePath}");
   return null;
            }

            // 移动成功后再写元数据；若此处失败，文件仍在回收站中，
      // 仅丢失「恢复原路径」的能力，不会导致文件丢失
            string metaFile = destPath + ".meta";
      try
       {
    File.WriteAllText(metaFile, filePath);
       }
      catch (Exception ex)
      {
       _logService.LogWarning($"写入回收站元数据失败（不影响文件安全）: {ex.Message}");
       }

        _logService.LogInfo($"文件已移至回收站: {filePath} -> {destPath}");
     return destPath;
        }
        catch (Exception ex)
  {
    _logService.LogError($"移动到回收站失败: {filePath}, 错误: {ex.Message}");
        return null;
        }
    }

    /// <summary>
    /// 从回收站恢复文件
    /// </summary>
    /// <param name="recyclePath">回收站中的文件路径</param>
    /// <param name="originalPath">原始目标路径（为空则从 meta 读取）</param>
    /// <returns>是否成功</returns>
    public bool RestoreFromRecycleBin(string recyclePath, string? originalPath = null)
    {
    _logService.LogDebug($"[RestoreFromRecycleBin] 输入: recyclePath={recyclePath}, originalPath={originalPath ?? "null"}");

      try
        {
          if (!_fileOperationService.FileExists(recyclePath))
   {
      _logService.LogWarning($"回收站文件不存在: {recyclePath}");
      return false;
        }

  string metaFile = recyclePath + ".meta";

            // 未指定原路径时，从元数据读取
            if (string.IsNullOrEmpty(originalPath))
            {
      if (!_fileOperationService.FileExists(metaFile))
       {
      _logService.LogWarning($"元数据文件不存在，无法确定恢复位置: {recyclePath}");
   return false;
   }

                originalPath = File.ReadAllText(metaFile).Trim();
                if (string.IsNullOrEmpty(originalPath))
           {
     _logService.LogWarning($"元数据内容为空: {metaFile}");
        return false;
      }
        }

      string? targetDir = Path.GetDirectoryName(originalPath);
            if (string.IsNullOrEmpty(targetDir))
     {
         _logService.LogError($"无效的恢复路径: {originalPath}");
  return false;
            }

   // 原目录可能已被删除
   if (!_fileOperationService.CreateDirectory(targetDir))
 {
 _logService.LogError($"创建恢复目录失败: {targetDir}");
      return false;
            }

          // 原位置已被占用时改名恢复，而不是失败
       string targetPath = originalPath;
    if (_fileOperationService.FileExists(targetPath))
 {
  targetPath = _fileOperationService.HandleFileNameConflict(targetDir, Path.GetFileName(originalPath));
   _logService.LogInfo($"原位置已占用，恢复为改名文件: {targetPath}");
            }

            if (!_fileOperationService.MoveFile(recyclePath, targetPath))
            {
           _logService.LogError($"恢复失败: {recyclePath} -> {targetPath}");
     return false;
}

            // 移动成功后再清理元数据（用旧路径引用，此时文件已不在该位置）
      if (_fileOperationService.FileExists(metaFile))
            {
   _fileOperationService.DeleteFile(metaFile);
            }

      _logService.LogInfo($"文件已从回收站恢复: {recyclePath} -> {targetPath}");
            return true;
        }
     catch (Exception ex)
 {
     _logService.LogError($"从回收站恢复失败: {recyclePath}, 错误: {ex.Message}");
            return false;
        }
    }
    
/// <summary>
    /// 清空回收站
    /// </summary>
    /// <remarks>
    /// 此操作<b>不可恢复</b>，调用前应有明确的用户确认。
 /// </remarks>
    /// <returns>是否成功</returns>
    public bool ClearRecycleBin()
    {
   try
        {
      var recycleDir = GetRecycleBinDirectory();

            if (!_fileOperationService.DirectoryExists(recycleDir))
    {
     return true;
   }

            // 先删文件（含 .meta），再清理可能残留的空目录
    var files = _fileOperationService.GetFiles(recycleDir, "*.*", SearchOption.TopDirectoryOnly);

  int deleted = 0;
            foreach (var file in files)
  {
                if (_fileOperationService.DeleteFile(file))
        deleted++;
  }

    _logService.LogInfo($"回收站已清空: {recycleDir}（删除 {deleted} 个文件）");
  return true;
  }
        catch (Exception ex)
   {
     _logService.LogError($"清空回收站失败: {ex.Message}");
      return false;
        }
    }
    
    /// <summary>
    /// 获取回收站中的文件列表
    /// </summary>
    /// <returns>文件列表</returns>
    public string[] GetRecycleBinFiles()
    {
        try
        {
            var recycleDir = GetRecycleBinDirectory();
            
            if (!_fileOperationService.DirectoryExists(recycleDir))
            {
                return Array.Empty<string>();
            }
            
            var allFiles = _fileOperationService.GetFiles(recycleDir, "*.*", SearchOption.TopDirectoryOnly);
            
            // 过滤掉 meta 文件
            return allFiles.Where(f => !f.EndsWith(".meta")).ToArray();
        }
        catch (Exception ex)
        {
            _logService.Log($"获取回收站文件列表失败, 错误: {ex.Message}");
            return Array.Empty<string>();
        }
    }
    
    /// <summary>
    /// 获取文件的原始路径
    /// </summary>
    /// <param name="recyclePath">回收站中的文件路径</param>
    /// <returns>原始路径，如果无法获取则返回 null</returns>
    public string? GetOriginalPath(string recyclePath)
    {
        try
        {
            var metaFile = recyclePath + ".meta";
            
            if (!_fileOperationService.FileExists(metaFile))
            {
                return null;
            }
            
            return File.ReadAllText(metaFile);
        }
        catch (Exception ex)
        {
            _logService.Log($"获取原始路径失败: {recyclePath}, 错误: {ex.Message}");
            return null;
        }
    }
}