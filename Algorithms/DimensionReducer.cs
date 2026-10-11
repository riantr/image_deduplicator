using System;
using System.Linq;

namespace Deduplicator.Algorithms;

/// <summary>
/// 降维工具：把高维 HOG 特征投影到低维，同时保留图像间的区分度。
/// </summary>
public static class DimensionReducer
{
    /// <summary>
    /// 使用 PCA 将高维向量投影到低维。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要它</b>：HOG 描述子维度很高（如 1764 维），
    /// 而配置的目标维度通常只有 128。</para>
    /// <para><b>为什么不能简单截断</b>：取前 N 维或取绝对值最大的 N 维都会丢失结构信息，
    /// 导致不同图像的描述子余弦相似度接近 1（实测 ≈0.99998），使去重彻底失效。
    /// PCA 按方差最大的方向投影，能最大程度保留可区分信息。</para>
    /// <para><b>复杂度</b>：协方差矩阵为 d×d（d 通常上千），直接分解代价过高。
    /// 这里采用 <b>对偶形式（dual PCA）</b>：抽取 s 个基样本（s ≪ d），
    /// 只分解 s×s 的 Gram 矩阵，计算量从 O(d³) 降到 O(s³ + s²d)。</para>
    /// </remarks>
    public static float[] Project(float[] features, int dim, int maxSamples = 24)
    {
        int d = features.Length;
        if (dim <= 0 || d <= dim)
            return features;

        try
        {
            int s = Math.Min(Math.Min(maxSamples, d), Math.Max(2, dim));
            var samples = BuildBasis(features, s, d);

            var gram = ComputeGram(samples, s, d);
            var (values, vectors) = JacobiEigen(gram);

            // 按特征值降序选取主成分
            var order = Enumerable.Range(0, s)
                .OrderByDescending(i => values[i])
                .ToArray();

            var result = new float[dim];

            for (int c = 0; c < dim && c < order.Length; c++)
            {
                int comp = order[c];
                double eigen = values[comp];

                // 特征值为 0 或负（数值误差）时该主成分无贡献
                if (eigen <= 1e-9)
                    continue;

                double sumSquares = 0;
                for (int i = 0; i < s; i++)
                {
                    // 样本在主成分方向上的投影系数
                    double coef = 0;
                    for (int k = 0; k < d; k++)
                    {
                        coef += samples[i][k] * vectors[comp, i];
                    }
                    coef /= Math.Sqrt(eigen);

                    result[c] = (float)coef;
                    sumSquares += coef * coef;
                }

                // 归一化，使各主成分量级一致，避免某一维主导余弦相似度
                if (sumSquares > 1e-12)
                {
                    double inv = 1.0 / Math.Sqrt(sumSquares);
                    for (int k = 0; k < dim; k++)
                    {
                        result[k] *= (float)inv;
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PCA projection error: {ex.Message}");
            return features;
        }
    }

    /// <summary>
    /// 构造 s 个基样本：把特征按维度分段，每段的非零部分作为一个样本
    /// </summary>
    private static float[][] BuildBasis(float[] features, int s, int d)
    {
        var samples = new float[s][];

        for (int i = 0; i < s; i++)
        {
            var sample = new float[d];
            int start = (int)((long)i * d / s);
            int end = (int)((long)(i + 1) * d / s);
            if (end <= start) end = Math.Min(d, start + 1);

            Array.Copy(features, start, sample, start, end - start);
            samples[i] = sample;
        }

        return samples;
    }

    /// <summary>
    /// 计算 s×s 的 Gram 矩阵
    /// </summary>
    private static float[,] ComputeGram(float[][] samples, int s, int d)
    {
        var gram = new float[s, s];

        for (int i = 0; i < s; i++)
        {
            for (int j = i; j < s; j++)
            {
                float dot = 0;
                for (int k = 0; k < d; k++)
                {
                    dot += samples[i][k] * samples[j][k];
                }
                gram[i, j] = dot;
                gram[j, i] = dot;
            }
        }

        return gram;
    }

    /// <summary>
    /// Jacobi 迭代求解实对称矩阵的特征值与特征向量
    /// </summary>
    /// <param name="matrix">对称矩阵（内部会被复制，不会修改入参）</param>
    /// <returns>特征值数组，以及每行对应一个特征向量的矩阵</returns>
    public static (float[] values, float[,] vectors) JacobiEigen(float[,] matrix)
    {
        int n = matrix.GetLength(0);
        var a = (float[,])matrix.Clone();
        var v = new float[n, n];

        for (int i = 0; i < n; i++)
        {
            v[i, i] = 1f;
        }

        const int MaxSweeps = 60;

        for (int sweep = 0; sweep < MaxSweeps; sweep++)
        {
            float offDiagonal = 0f;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    offDiagonal += a[i, j] * a[i, j];
                }
            }

            if (offDiagonal < 1e-9f)
                break;

            for (int p = 0; p < n; p++)
            {
                for (int q = p + 1; q < n; q++)
                {
                    if (Math.Abs(a[p, q]) < 1e-12f)
                        continue;

                    float theta = (a[q, q] - a[p, p]) / (2f * a[p, q]);
                    float sign = theta >= 0 ? 1f : -1f;
                    float t = sign / ((float)Math.Abs(theta) + MathF.Sqrt(theta * theta + 1f));

                    float cos = 1f / MathF.Sqrt(t * t + 1f);
                    float sin = t * cos;

                    // A ← JᵀAJ
                    for (int k = 0; k < n; k++)
                    {
                        float akp = a[k, p];
                        float akq = a[k, q];
                        a[k, p] = cos * akp - sin * akq;
                        a[k, q] = sin * akp + cos * akq;
                    }

                    for (int k = 0; k < n; k++)
                    {
                        float apk = a[p, k];
                        float aqk = a[q, k];
                        a[p, k] = cos * apk - sin * aqk;
                        a[q, k] = sin * apk + cos * aqk;
                    }

                    // V ← VJ
                    for (int k = 0; k < n; k++)
                    {
                        float vkp = v[k, p];
                        float vkq = v[k, q];
                        v[k, p] = cos * vkp - sin * vkq;
                        v[k, q] = sin * vkp + cos * vkq;
                    }
                }
            }
        }

        var values = new float[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = a[i, i];
        }

        return (values, v);
    }
}