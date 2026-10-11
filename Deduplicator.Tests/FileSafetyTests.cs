using Deduplicator.Services;

namespace Deduplicator.Tests;

/// <summary>
/// 文件操作安全性测试。
///
/// <para>这些用例针对真实发生过的数据丢失缺陷：
/// 「恢复到已占用位置」时文件丢失、meta 记录路径与实际落盘路径错位等。</para>
/// </summary>
public static class FileSafetyTests
{
    public static void Run(string workDir)
    {
        MoveSemantics();
        RecycleBinRoundTrip(workDir);
        NameConflictHandling(workDir);
    }

    // ---------- MoveFile 语义 ----------
    private static void MoveSemantics()
    {
        Assert.Section("MoveFile 语义：目标已存在时不覆盖");

        var log = new LogService { EnableFileLog = false };
        var fos = new FileOperationService(log);

        string root = NewDir("move_semantics");
        try
        {
            string src = Path.Combine(root, "src.txt");
            string dst = Path.Combine(root, "dst.txt");

            File.WriteAllText(src, "SOURCE");
            File.WriteAllText(dst, "EXISTING");

            bool moved = fos.MoveFile(src, dst);

            Assert.True(!moved, "目标已存在时 MoveFile 返回 false");
            Assert.True(File.Exists(dst), "目标文件未被覆盖");
            Assert.Equal("EXISTING", File.ReadAllText(dst), "目标内容保持不变");
            Assert.True(File.Exists(src), "源文件未被移动（操作失败应保持原状）");
        }
        finally
        {
            SafeDelete(root);
        }
    }

    // ---------- 回收站往返 ----------
    private static void RecycleBinRoundTrip(string baseDir)
    {
        Assert.Section("回收站：移入 → 恢复 往返一致性");

        var log = new LogService { EnableFileLog = false };
        var fos = new FileOperationService(log);
        var rbs = new RecycleBinService(log, fos);

        string root = NewDir("recycle_roundtrip");
        try
        {
            string file = Path.Combine(root, "photo.png");
            File.WriteAllText(file, "CONTENT-XYZ");

            var recyclePath = rbs.MoveToRecycleBin(file);

            Assert.NotNull(recyclePath, "移入回收站成功");
            Assert.True(!File.Exists(file), "原文件已从原位移除");
            Assert.True(File.Exists(recyclePath!), "文件存在于回收站中");
            Assert.True(File.Exists(recyclePath + ".meta"), "元数据已写入");

            Assert.Equal(file, File.ReadAllText(recyclePath + ".meta"), "元数据记录了正确的原路径");

            bool restored = rbs.RestoreFromRecycleBin(recyclePath!);

            Assert.True(restored, "恢复成功");
            Assert.True(File.Exists(file), "文件回到原始位置");
            Assert.Equal("CONTENT-XYZ", File.ReadAllText(file), "内容完整无损坏");
            Assert.True(!File.Exists(recyclePath + ".meta"), "元数据已清理");
        }
        finally
        {
            SafeDelete(root);
            TryClearRecycleBin(rbs);
        }
    }

    // ---------- 关键场景：恢复位置已被占用 ----------
    private static void NameConflictHandling(string baseDir)
    {
        Assert.Section("关键场景：恢复时原位置已被占用（不得覆盖）");

        var log = new LogService { EnableFileLog = false };
        var fos = new FileOperationService(log);
        var rbs = new RecycleBinService(log, fos);

        string root = NewDir("recycle_conflict");
        try
        {
            string file = Path.Combine(root, "doc.png");
            File.WriteAllText(file, "ORIGINAL-CONTENT");

            var recyclePath = rbs.MoveToRecycleBin(file);
            Assert.NotNull(recyclePath, "移入回收站成功");

            // 模拟：用户已在原位置放了新文件
            File.WriteAllText(file, "NEW-DIFFERENT-FILE");

            bool restored = rbs.RestoreFromRecycleBin(recyclePath!);
            Assert.True(restored, "占用情况下恢复仍应成功（以改名方式）");

            // 核心断言：已有文件绝不能被覆盖
            Assert.Equal("NEW-DIFFERENT-FILE", File.ReadAllText(file),
                "原位置的新文件未被覆盖（数据安全）");

            // 被恢复的文件以改名方式落盘，内容完整
            var restoredFiles = Directory.GetFiles(root, "doc*.png")
                .Where(f => File.ReadAllText(f) == "ORIGINAL-CONTENT")
                .ToArray();

            Assert.Equal(1, restoredFiles.Length,
                $"被删除的文件完整恢复（目录内容: {string.Join(", ", Directory.GetFiles(root).Select(Path.GetFileName))}）");
        }
        finally
        {
            SafeDelete(root);
            TryClearRecycleBin(rbs);
        }
    }

    // ---------- 辅助 ----------
private static string NewDir(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"dedup_test_{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private static void TryClearRecycleBin(RecycleBinService rbs)
    {
        try { rbs.ClearRecycleBin(); } catch { }
    }
}