using Deduplicator.Tests;

namespace Deduplicator.Tests;

/// <summary>
/// 测试入口
///
/// <para>用法：<c>dotnet run --project Deduplicator.Tests</c></para>
/// <para>退出码 0 表示全部通过，非 0 表示存在失败（可用于 CI）。</para>
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        bool quick = args.Contains("--quick");
        string workDir = CreateWorkDirectory(quick);

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("════════════════════════════════════════════════════");
        Console.WriteLine("  图像去重工具 — 自动化验证");
        Console.WriteLine("════════════════════════════════════════════════════");
        Console.WriteLine($"测试数据目录：{workDir}");
        Console.WriteLine($"数据规模：{(quick ? "快速" : "完整")}");

        // 性能剖析模式：对外部指定的目录运行，不生成测试数据
        if (args.Contains("--profile"))
        {
            string profileDir = args.FirstOrDefault(a => !a.StartsWith("--"))
                ?? throw new ArgumentException("--profile 需要指定图像目录");
            Profile.Run(profileDir);
            return 0;
        }

try
        {
        TestData.Generate(workDir, quick);

  // 诊断入口需在数据生成之后运行，否则目录为空
            if (args.Contains("--ssim"))
            {
         SsimDiag.Run(workDir);
     return 0;
}

      DeduplicationTests.Run(workDir);
            FileSafetyTests.Run(workDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine($"测试执行异常：{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Assert.That(false, "测试执行未抛出异常");
        }
        finally
        {
            TryCleanup(workDir);
        }

        return Assert.Summary();
    }

    private static string CreateWorkDirectory(bool quick)
    {
        string dir = Path.Combine(Path.GetTempPath(),
            $"dedup_tests_{DateTime.Now:yyyyMMdd_HHmmss}");

        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryCleanup(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }
}

/// <summary>
/// 测试数据生成
/// </summary>
public static class TestData
{
    /// <summary>
    /// 实际生成的文件数（供测试校验扫描结果）
    /// </summary>
    public static int GeneratedFileCount { get; private set; }

    /// <summary>
    /// 生成测试图像
    /// </summary>
    /// <remarks>
    /// 包含三类数据：
    /// <list type="bullet">
    ///   <item><b>完全相同的副本对</b>（orig_a/copy_a、orig_b/copy_b）—— 任何算法都必须检出；</item>
    ///   <item><b>低纹理色块图</b> —— 验证算法在极端输入下的边界行为；</item>
    ///   <item><b>多频纹理图</b> —— 感知哈希的主战场。</item>
    /// </list>
    /// </remarks>
    public static void Generate(string dir, bool quick)
    {
        // 第 1 组：完全相同的副本对（字节级一致）
        TestImages.WriteBlocks(Path.Combine(dir, "orig_a.png"), 1);
        File.Copy(Path.Combine(dir, "orig_a.png"), Path.Combine(dir, "copy_a.png"), overwrite: true);

        // 第 2 组：另一对副本，纹理不同
        TestImages.WriteTexture(Path.Combine(dir, "orig_b.png"), 2);
        File.Copy(Path.Combine(dir, "orig_b.png"), Path.Combine(dir, "copy_b.png"), overwrite: true);

        // 低纹理色块图
        for (int i = 0; i < 6; i++)
        {
            TestImages.WriteBlocks(Path.Combine(dir, $"block_{i:00}.png"), 100 + i);
        }

        // 多频纹理图
        int textureCount = quick ? 6 : 30;
        for (int i = 0; i < textureCount; i++)
        {
            TestImages.WriteTexture(Path.Combine(dir, $"tex_{i:00}.png"), 1000 + i);
        }

        GeneratedFileCount = 4 + 6 + textureCount;
    }
}