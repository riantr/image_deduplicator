using System.Diagnostics;
using System.Text;

namespace Deduplicator.Tests;

/// <summary>
/// 极简测试断言与运行器（不引入第三方测试框架，保持零依赖）
/// </summary>
public static class Assert
{
    public static int Passed;
    public static int Failed;
    private static readonly List<string> _failures = new();

    public static void That(bool condition, string name, string? detail = null)
    {
        if (condition)
        {
            Passed++;
            Console.WriteLine($"  PASS  {name}");
        }
        else
        {
            Failed++;
            var message = string.IsNullOrEmpty(detail) ? name : $"{name} — {detail}";
            _failures.Add(message);
            Console.WriteLine($"  FAIL  {message}");
        }
    }

    public static void Equal<T>(T expected, T actual, string name)
    {
        bool ok = EqualityComparer<T>.Default.Equals(expected, actual);
        That(ok, name, ok ? null : $"期望={expected}, 实际={actual}");
    }

    public static void NotNull(object? value, string name)
    {
        That(value != null, name, value == null ? "值为 null" : null);
    }

    public static void True(bool condition, string name, string? detail = null) => That(condition, name, detail);

    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"── {title} ──");
    }

    public static int Summary()
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 52));

        if (Failed > 0)
        {
            Console.WriteLine($"失败明细（{Failed} 项）：");
            foreach (var f in _failures)
            {
                Console.WriteLine($"  · {f}");
            }
        }

        Console.WriteLine($"结果：PASS {Passed} / FAIL {Failed}");
        return Failed == 0 ? 0 : 1;
    }
}

/// <summary>
/// 测试用 PNG 图像生成器（纯托管实现，无外部依赖）
/// </summary>
public static class TestImages
{
    /// <summary>
    /// 写入 PNG 文件。pixelFn 返回 (r,g,b)。
    /// </summary>
    public static void WritePng(string path, int width, int height, Func<int, int, (int r, int g, int b)> pixelFn)
    {
        using var ms = new MemoryStream();

        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR
        var ihdr = new byte[13];
        WriteInt32BE(ihdr, 0, width);
        WriteInt32BE(ihdr, 4, height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // color type: truecolor RGB
        WriteChunk(ms, "IHDR", ihdr);

        // IDAT（zlib 压缩原始扫描线）
        using var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0); // filter type: None
            for (int x = 0; x < width; x++)
            {
                var (r, g, b) = pixelFn(x, y);
                raw.WriteByte((byte)Math.Clamp(r, 0, 255));
                raw.WriteByte((byte)Math.Clamp(g, 0, 255));
                raw.WriteByte((byte)Math.Clamp(b, 0, 255));
            }
        }

        WriteChunk(ms, "IDAT", Compress(raw.ToArray()));
        WriteChunk(ms, "IEND", Array.Empty<byte>());

        File.WriteAllBytes(path, ms.ToArray());
    }

    /// <summary>
    /// 彩色方块图（低纹理，适合验证算法边界行为）
    /// </summary>
    public static void WriteBlocks(string path, int seed, int width = 400, int height = 300)
    {
        var rnd = new Random(seed);
        var blocks = new (int r, int g, int b)[6];
        for (int i = 0; i < 6; i++)
        {
            blocks[i] = (rnd.Next(256), rnd.Next(256), rnd.Next(256));
        }

WritePng(path, width, height, (x, y) =>
    {
      int row = Math.Min(2, y * 3 / height);
        int col = Math.Min(1, x * 2 / width);
     var c = blocks[row * 2 + col];
    return (c.Item1, c.Item2, c.Item3);
  });
    }

    /// <summary>
    /// 多频正弦叠加纹理图（高纹理，适合验证感知哈希）
    /// </summary>
    public static void WriteTexture(string path, int seed, int width = 400, int height = 300)
    {
        var rnd = new Random(seed);
        var freqs = new (double fx, double fy, double px, double py, double amp)[8];
        for (int i = 0; i < 8; i++)
        {
            freqs[i] = (rnd.Next(3, 30), rnd.Next(3, 30),
                        rnd.NextDouble() * 6.28, rnd.NextDouble() * 6.28,
                        rnd.NextDouble() * 80 + 30);
        }

        var tint = (rnd.Next(-40, 41), rnd.Next(-40, 41), rnd.Next(-40, 41));

        WritePng(path, width, height, (x, y) =>
        {
            double v = 0;
            foreach (var f in freqs)
            {
                v += f.amp * Math.Sin(2 * Math.PI * f.fx * x / width + f.px)
                       * Math.Cos(2 * Math.PI * f.fy * y / height + f.py);
            }
int baseVal = (int)(128 + v * 0.5);
            return (baseVal + tint.Item1, baseVal + tint.Item2, baseVal + tint.Item3);
        });
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteInt32BE(len, 0, data.Length);
        s.Write(len);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);

        // CRC32 over type + data
        var crcInput = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, crcInput, 0, typeBytes.Length);
        Buffer.BlockCopy(data, 0, crcInput, typeBytes.Length, data.Length);

        var crc = new byte[4];
        WriteInt32BE(crc, 0, unchecked((int)Crc32(crcInput)));
        s.Write(crc);
    }

    private static void WriteInt32BE(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(
            ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFF;
    }
}

/// <summary>
/// 计时辅助
/// </summary>
public static class Bench
{
    public static (long Ms, T Result) Time<T>(Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        var result = action();
        sw.Stop();
        return (sw.ElapsedMilliseconds, result);
    }

    /// <summary>
    /// 计时执行一个返回 void 的操作
    /// </summary>
    public static long Time(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.ElapsedMilliseconds;
    }
}