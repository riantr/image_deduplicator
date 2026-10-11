using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Data.Converters;
using Deduplicator.Models;

namespace Deduplicator.Converters;

/// <summary>
/// 控制参数区块是否可见。
///
/// <para>重构前，11 种算法各有 4~5 个 Tab 页，参数面板重复约 600 行 XAML：
/// 每新增一个参数就要改 11 处，极易遗漏。重构后改为「一个下拉框 +
/// 按算法动态显示字段」，显示逻辑集中在这里。</para>
///
/// <para>每种参数对应一个预置实例（在 <see cref="AlgorithmVisibility"/> 中注册），
/// 避免在 XAML 里传字符串参数 —— 那需要嵌套引号，在 XAML 中极易出错。</para>
/// </summary>
public abstract class AlgorithmVisibilityBase : IValueConverter
{
    protected abstract IReadOnlyCollection<AlgorithmType> Applicable { get; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AlgorithmType current && Applicable.Contains(current))
            return true;

        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ShowHashSizeConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.FileHash, AlgorithmType.AHash,
        AlgorithmType.DHash, AlgorithmType.PHash
    };
}

public sealed class ShowHashWeightConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.AHash, AlgorithmType.DHash
    };
}

public sealed class ShowWindowSizeConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.FileHash, AlgorithmType.PHash, AlgorithmType.FastDup,
        AlgorithmType.Histogram, AlgorithmType.Template, AlgorithmType.Ssim
    };
}

public sealed class ShowThumbSizeConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.FastDup
    };
}

public sealed class ShowBinsConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.Histogram
    };
}

public sealed class ShowBlockSizeConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.Template, AlgorithmType.Ssim
    };
}

public sealed class ShowFeaturesConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.Sift
    };
}

public sealed class ShowMinMatchConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.Sift, AlgorithmType.Surf
    };
}

public sealed class ShowHessianConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.Surf
    };
}

public sealed class ShowDimConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.Lance
    };
}

public sealed class ShowKnnConverter : AlgorithmVisibilityBase
{
    protected override IReadOnlyCollection<AlgorithmType> Applicable { get; } = new[]
    {
        AlgorithmType.Lance
    };
}

/// <summary>
/// 算法类型 → 用途说明文字
/// </summary>
public sealed class AlgorithmDescriptionConverter : IValueConverter
{
    private static readonly Dictionary<AlgorithmType, string> Descriptions = new()
    {
        [AlgorithmType.FileHash] = "比对文件内容的 SHA256。只认字节完全相同的副本 —— 最快，也最严格。",
        [AlgorithmType.DHash] = "比较相邻像素的明暗差异梯度。对亮度变化与轻微裁剪鲁棒，一般首选。",
        [AlgorithmType.AHash] = "比较各区域的平均明暗。速度最快，但低纹理图像区分度较弱。",
        [AlgorithmType.PHash] = "先做 DCT 变换再比较低频分量。抗压缩与缩放能力强，计算量较大。",
        [AlgorithmType.FastDup] = "在缩略图上做快速比对，适合先粗筛再人工确认。",
        [AlgorithmType.Histogram] = "比较 RGB 颜色分布。对配色相近但内容不同的图像可能误判。",
        [AlgorithmType.Template] = "寻找纹理最丰富的区块做模板匹配。适合内容相似但构图不同。",
        [AlgorithmType.Ssim] = "基于结构相似度，贴近人眼判断，对亮度偏移有一定容忍。",
        [AlgorithmType.Sift] = "提取尺度不变关键点。对旋转、缩放最鲁棒，但计算量大。",
        [AlgorithmType.Surf] = "二进制特征点，速度快于 SIFT，对旋转缩放同样鲁棒。",
        [AlgorithmType.Lance] = "HOG 梯度特征降维后做向量检索，低阈值下仍能保持较高效剪枝。",
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AlgorithmType algo && Descriptions.TryGetValue(algo, out var text))
            return text;
        return string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 对象为 null 时显示（用于空状态占位提示）
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}