# Deduplicator — 图像去重工具

基于 Avalonia + OpenCV 的桌面图像去重工具，支持 11 种相似度算法。

## 快速开始

```bash
# 构建
dotnet build

# 运行
dotnet run --project Deduplicator.csproj

# 运行测试
dotnet run --project Deduplicator.Tests -c Release
```

测试退出码为 0 表示全部通过，可直接接入 CI。

## 支持的算法

| 算法 | 原理 | 适用场景 | 预筛削减 |
|---|---|---|---|
| FileHash | 文件 SHA256 | 完全相同的副本 | 99.7% |
| AHash | 8×8 均值哈希 | 快速粗筛 | 87.2% |
| DHash | 差值哈希 | **推荐**，对亮度变化鲁棒 | 82.3% |
| PHash | DCT 感知哈希 | 抗压缩、抗缩放 | 32.6% |
| FastDup | 缩略图 SSIM | 近似重复 | — |
| Histogram | 颜色分布相关 | 配色相近 | 9.9% |
| Template | 纹理最丰富区块匹配 | 局部内容相似 | — |
| SSIM | 结构相似度 | 整体结构相近 | — |
| SIFT | 局部特征点 | 缩放/旋转不变 | — |
| SURF(ORB) | 二值特征点 | 快速局部匹配 | — |
| Lance | HOG + PCA | 形状轮廓 | 92.2% |

> **选择建议**：优先用 DHash 或 FileHash。PHash 对纯色、低纹理图像区分度较弱
> （相似度可达 0.81），这是算法固有特性而非缺陷。

## 架构

```
Algorithms/          算法实现（无状态）
  HashAlgorithms       感知哈希 + LSH 分段
  SimilarityAlgorithms 直方图/SSIM/模板匹配
  FeatureAlgorithms    SIFT/ORB/HOG
  DimensionReducer     PCA 降维（对偶形式 + Jacobi 特征分解）
  ImageHelper          文件扫描与像素辅助

Services/           业务编排
  DeduplicationService 扫描/特征计算/比对/执行的编排
  CandidatePrefilter   候选预筛（各种算法的无漏报加速）
  ThumbnailCache       有界 LRU 缩略图缓存
  RecycleBinService    回收站（可恢复删除）
  FileOperationService 文件移动/改名/冲突处理
  LogService           批量提交 UI 的日志

Models/             数据模型
ViewModels/         MVVM
Views/              Avalonia UI
Deduplicator.Tests/ 自动化验证（43 项断言）
```

## 关键设计

### 内存：不常驻全分辨率图像

原始实现把每张图的全分辨率 `Mat` 与 PNG 重编码的 `Bitmap` 全部常驻内存，
一张 12MP 照片约 36MB，1000 张必然 OOM。

当前实现：
- 特征计算时**临时解码 → 计算 → 立即释放**
- 常驻内存中只有紧凑特征（哈希 64bit、直方图 768 float 等）
- UI 预览通过 `ThumbnailCache` 按需生成缩略图，**内存占用与图片总数无关**

实测 600 张 800×600 图的特征常驻内存约 1MB。

### 性能：预筛 + 并行

朴素比对需要 O(n²) 次相似度计算。`CandidatePrefilter` 为每种算法构造
「若两图相似则必满足」的必要条件，把绝大多数不可能相似的组合提前排除。

所有预筛均经过**穷举校验**（暴力算出全部超阈值对，确认无一遗漏），
并带高碰撞保护：候选量接近全量时自动回退暴力比对。

### 文件安全

删除一律走回收站而非直接 `File.Delete`，并保留 `.meta` 元数据以支持精确恢复。

关键约束：
- `MoveFile` **不会自动改名**，目标已存在即返回 `false`，由调用方决策
  （早期实现在内部二次改名，导致调用方记录的路径与实际落盘路径错位，恢复时文件丢失）
- 恢复到已被占用的位置时，以**改名方式**落盘，绝不覆盖已有文件

## 跨平台

OpenCV 原生库通过 NuGet 包按平台自动引用（`OpenCvSharp4.runtime.*`），
无需手工配置 `LD_LIBRARY_PATH` 或创建软链接。

## 测试

```bash
dotnet run --project Deduplicator.Tests -c Release
```

覆盖：
- 目录扫描、文件大小记录
- 11 种算法的特征计算（含平滑图降级行为）
- 相似度正确性（相同副本必为 1，不同文件必为 0）
- **预筛无漏报**（对 FileHash/AHash/DHash/PHash/Histogram/Lance 逐对穷举校验）
- 内存占用
- 文件安全：MoveFile 语义、回收站往返、恢复位置被占用时不覆盖