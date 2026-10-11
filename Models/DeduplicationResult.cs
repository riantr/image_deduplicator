using System.Collections.Generic;

namespace Deduplicator.Models;

public class DeduplicationResult
{
    public string OriginalPath { get; set; } = string.Empty;
    public List<DuplicateItem> Duplicates { get; set; } = new();
    public bool IsDeleted { get; set; }
    
    /// <summary>
    /// 文件路径（兼容 ImageData）
    /// </summary>
    public string FilePath => OriginalPath;
    
    /// <summary>
    /// 文件名（从 OriginalPath 提取）
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(OriginalPath);
    
    /// <summary>
    /// 重复数
    /// </summary>
    public int DuplicatesCount => Duplicates.Count;
}

public class DuplicateItem
{
    public string Path { get; set; } = string.Empty;
    public double Similarity { get; set; }
    
    /// <summary>
    /// 文件名（从 Path 提取）
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(Path);
}