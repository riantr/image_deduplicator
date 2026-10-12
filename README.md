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
| FastDup | 缩略图 SSIM | 近似重复 | 99.7% |
| Histogram | 颜色分布相关 | 配色相近 | 9.9% |
| Template | 纹理最丰富区块匹配 | 局部内容相似 | 99.7% |
| SSIM | 结构相似度 | 整体结构相近 | 99.7% |
| SIFT | 局部特征点 | 缩放/旋转不变 | 99.7% |
| SURF(ORB) | 二值特征点 | 快速局部匹配 | 99.7% |
| Lance | HOG + PCA | 形状轮廓 | 92.2% |

> 「预筛削减」为默认阈值 0.85 下、40 张测试图（780 个组合）的实测值。
> SSIM / Template / FastDup / SIFT / ORB 使用代理哈希预筛 —— 在 360 张图的
> 实测中比对量从 64,620 降至 117（-99.97%），比对照集更小的测试集会低估其效果。

### 阈值对预筛效率的影响

预筛的削减率**随阈值变化**，实测（40 张图）：

| 阈值 | AHash | DHash | PHash | Histogram | Lance |
|---|---|---|---|---|---|
| 0.95 | 97.8% | 96.7% | 97.3% | 60.9% | 92.2% |
| 0.85 | 87.2% | 82.3% | 32.6% | 9.9% | 92.2% |
| 0.75 | 26.8% | 31.2% | 0% | 0% | 92.2% |
| 0.65 | 0% | 0% | 0% | 0% | 92.2% |
| ≤0.55 | 0% | 0% | 0% | 0% | 92.2% |

**重要结论**：
- **Lance 的多表 LSH 在全阈值区间稳定保持 92.2%**，不受阈值影响。
- **Hash 类在阈值低于 0.75 后剪枝收益趋近于零**，会正确退化为全量比对。
  这是鸽笼原理的数学必然——阈值越低，允许的汉明距离越大，为保证不漏报所需的分带数越多，
  桶就越"泛化"直至退化为全量。**这不是缺陷，但意味着低阈值场景下不应期待预筛加速。**
- 全部阈值区间均**零漏报**（穷举校验，见下文测试说明）。

> **选择建议**：优先用 DHash 或 FileHash，阈值保持在 0.85 左右。
> PHash 对纯色、低纹理图像区分度较弱（相似度可达 0.81），这是算法固有特性而非缺陷。
> 若需要低阈值下的加速能力，选择 Lance。

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
Deduplicator.Tests/ 自动化验证（135 项断言）
```

## 界面

### 布局

```
┌────────────────────────────────────────────────────────────┐
│ 品牌 · 流程指示（载入 → 特征 → 查重）        已载入 N 张  ☾ │
├──────────────────┬─────────────────────────────────────────┤
│ 目录             │  对比工作区                     ◀ ▶     │
│  算法 + 动态参数 │  ┌─────────┬─────────┬──────────┐        │
│  日志            │  │  原图   │ 重复图  │ 重复项   │        │
│                  │  │         │ 相似度   │  列表     │        │
│ ────────────────  │  └─────────┴─────────┴──────────┘        │
│ 载入/特征/查重   │  对调文件名 · 撤回删除 · 删除重复          │
├──────────────────┴─────────────────────────────────────────┤
│ 状态消息 ······························ 进度条  N 张图像     │
└────────────────────────────────────────────────────────────┘
```

### 算法设置：11 个 Tab 页 → 动态参数面板

早期实现为每种算法一个 Tab 页，参数面板重复约 600 行 XAML，
**每新增一个参数就要改 11 处**，极易遗漏。

现改为「一个下拉框 + 按算法动态显示字段」，显示逻辑集中在
`Converters/AlgorithmConverters.cs` 的 11 个转换器中，新增参数只需改一处。

### 视觉体系

遵循 Fluent Design（WinUI 3）规范，语义化命名：

```
LayerFillColorDefaultBrush      卡片面          TextFillColorPrimary/Secondary/Tertiary/Disabled  四级文本
SolidBackgroundFillColorBaseBrush  页面底色     AccentFillColorDefault/Secondary/Tertiary  强调色三态
ControlFillColorDefault/Secondary/Tertiary  控件三档填充
CardStrokeColorDefaultBrush     卡片描边        SystemFillColorSuccess/Caution/Critical  语义色
```

全部定义在 `ThemeDictionaries` 中，**浅色 / 深色自动适配**
（深色下强调色换用 #60CDFF 以保证对比度）。

标题栏右侧可切换主题，偏好持久化到
`%LocalAppData%/Deduplicator/theme.txt`，下次启动自动恢复。

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

修复的三处**会导致文件永久丢失**的缺陷：

1. `MoveFile` 原会在内部二次改名，导致调用方记录的路径与实际落盘路径错位，恢复时找不到文件。
   现改为：**不自动改名**，目标已存在即返回 `false`，由调用方决策。
2. 撤回删除时若原位置已被占用，原实现会**覆盖**新文件。现改为以 `name_1.ext` 改名落盘。
3. 对调文件名原是两两直接交换，第三步失败会留下错位状态。
   现改为「临时文件 + 三步移动」，任一步失败完整回滚。

### 跨线程：UI 层禁止 ConfigureAwait(false)

`await xxx.ConfigureAwait(false)` 会让后续代码脱离 UI 线程。
若之后修改了绑定到 UI 的属性，Avalonia 会抛
`InvalidOperationException: Call from invalid thread`。

本项目踩过：`AsyncRelayCommand` 用 `ConfigureAwait(false)` 后，
`finally` 里复位 `IsExecuting` 在后台线程触发 `CanExecuteChanged`，
该事件被命令绑定读取 `Button.CanExecute` → **启动即崩**。

规则：
- ViewModel 与命令层：**不用** `ConfigureAwait(false)`
- `RaiseCanExecuteChanged` 必须先 `CheckAccess()`，否则 `Post` 到 UI 线程
- Services 层（不触碰 UI）可以用 `ConfigureAwait(false)`

## 跨平台

OpenCV 原生库通过 NuGet 包按平台自动引用（`OpenCvSharp4.runtime.*`），
无需手工配置 `LD_LIBRARY_PATH` 或创建软链接。

## 测试

```bash
dotnet run --project Deduplicator.Tests -c Release
# 退出码 0 = 全部通过
```

当前 **135 项断言**全部通过（含 32 项 UI 层验证）。覆盖：

- 目录扫描、文件大小记录
- 11 种算法的特征计算（含平滑图降级行为）
- 相似度正确性（相同副本必为 1，不同文件必为 0）
- **预筛无漏报**：对 11 种算法 × 6 档阈值共 **66 组组合**校验真实重复不遗漏
  （用文件内容哈希判定「真实重复」，而非「所有超阈值对」——
  后者在低阈值下会把 SSIM 的假阳性也纳入，指标会退化为衡量算法判别能力）
- 内存占用
- 文件安全：MoveFile 语义、回收站往返、**恢复位置被占用时不覆盖已有文件**
- UI 层（Avalonia Headless 构建完整控件树）：
  资源加载、设计令牌齐全（浅色 + 深色两套主题）、
  主题切换真正改变 `Application.RequestedThemeVariant`、
  控件树结构、参数可见性转换器、**后台线程触发命令不崩溃**

测试数据同时包含低纹理色块图与多频纹理图——只用单一类型数据会漏掉真实缺陷。

## 性能剖析

定位瓶颈的工具，可对任意目录运行：

```bash
dotnet run --project Deduplicator.Tests -c Release -- --profile <图像目录>
```

输出各算法的特征耗时、比对耗时与候选量，按耗时排序以突出瓶颈。

另外两个辅助工具：

```bash
# 分阶段剖析：解码 / 灰度 / 代理特征 / 各算法核心
dotnet run --project Deduplicator.Tests -c Release -- --stage <目录>

# 验证工作尺寸对相似度质量的影响
dotnet run --project Deduplicator.Tests -c Release -- --quality <目录>

# HOG/Lance 诊断（耗时/维度/质量）—— 结论：尺寸不可缩小
dotnet run --project Deduplicator.Tests -c Release -- --hog <目录>
```

> 注意：`--profile` 是并行测量，`--stage` 是单线程测量。
> OpenCV 内部已有多线程，外层并行会产生线程竞争，两者需分开看。

### 实测结果（360 张 400×300 图）

引入代理哈希预筛后，比对总耗时从 199 秒降至 1.8 秒：

| 算法 | 优化前 | 优化后 | 加速 |
|---|---|---|---|
| SIFT | 93,061ms | 1,023ms | 91× |
| ORB | 88,009ms | 467ms | 188× |
| SSIM | 9,818ms | 71ms | 138× |
| Template | 5,226ms | 19ms | 275× |
| FastDup | 3,040ms | 6ms | 507× |
| **合计** | **199,309ms** | **1,761ms** | **113×** |

特征计算阶段也做了优化：SIFT/ORB 的工作尺寸上限设为 256，
SIFT 单张耗时从 17.68ms 降至 8.27ms。

### 已证伪的优化：Lance/HOG 不能缩小工作尺寸

SIFT/ORB 缩小工作尺寸后质量完全不变，于是容易认为「缩小尺寸」是通用手段。
**实测表明这个推论是错的**（60 张 400×300 图）：

| 尺寸 | 特征耗时 | 重复对相似度 | 独立图相似度 |
|------|---------|-------------|-------------|
| 256  | 4.53ms  | 1.0000 | 0.8074 |
| 192  | 2.62ms  | 1.0000 | **0.8493** ← 贴到 0.85 默认阈值 |
| 160  | 2.6ms   | 1.0000 | 0.8744 |
| 128  | 2.4ms   | 1.0000 | 0.8967 |

缩小后**重复对恒为 1.0，看似完全无损**。但独立图的相似度单调上升——
缩到 192 时实际检出组数从 **11 组涨到 16 组**，多出的 5 组全是误报。

**原因**：HOG 是全局梯度直方图，区分度依赖分辨率；
缩小会让梯度分布趋于平滑，不同图像的特征互相靠拢。
而 SIFT/ORB 用的是 DoG/FAST 角点这类**局部结构**，对缩小不敏感。

> **同一优化手段在不同算法上效果不同，必须逐个验证。**
> 复现：`--hog <目录>`，结论已固化在该工具的注释中。

## 持续集成

已配置 GitHub Actions（`.github/workflows/ci.yml`）与 Gitee Go（`.gitee/workflows/ci.yml`）：

- push / pull_request 触发
- Windows + Ubuntu 双平台矩阵
- 构建 + 运行测试
- 失败时上传测试日志

关联到 Gitee 后即可在仓库的「流水线」页面看到结果。