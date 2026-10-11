# Deduplicator 项目长期记忆

## 项目定位
Avalonia + OpenCV 桌面图像去重工具，11 种相似度算法。

## 技术栈与平台
- .NET 8 / Avalonia 11.0.10 / OpenCvSharp4 4.10
- **OpenCV 原生库必须用 NuGet 包按平台引用**（`OpenCvSharp4.runtime.win/linux-x64/osx`），
  不要用 `LD_LIBRARY_PATH`、不要手工建 `.so` 软链接——历史上就是这么写的，导致 Windows 上跑不起来
- 主项目是 WinExe + `StartupObject` 未指定时，测试项目的 Main 会与主程序冲突；
  主 csproj 需 `<Compile Remove="Deduplicator.Tests\**\*.cs" />`

## 架构约定
- `ImageData` **不持有全分辨率 Mat**，特征计算时临时解码、算完释放
- UI 缩略图走 `ThumbnailCache`（有界 LRU），内存与图片总数解耦
- 所有耗时操作用 `Parallel.For` 多核；进度上报要节流（每 25/100 次一报）
- 日志走 `LogService` 的批量提交（150ms），不要每条单独 Post 到 UI 线程

## 算法选型
- **优先 DHash 或 FileHash，阈值保持在 0.85 左右**。PHash/DHash 对纯色、低纹理图像区分度差
  （相似度可达 0.81），这是算法固有局限，不是 bug
- 需要低阈值下的加速能力时选 **Lance**（多表 LSH 全阈值稳定 92.2% 削减）
- SIFT/ORB 在平滑图上提不出特征，属正常；需保证不崩溃并显式告警
- **预筛削减率随阈值变化**：Hash 类阈值 <0.75 后收益归零（鸽笼原理的数学必然，
  保无漏报所需分带数随阈值降低而增加，桶泛化至全量）。这是合理行为，不要误判为缺陷

## 预筛现状（2026-11-11 更新）
全部 11 种算法均已有预筛：
- FileHash：文件大小 + 哈希分桶（99.7%）
- A/D/PHash：分带 LSH（段数随阈值自适应）
- Histogram：均值排序 + 二分滑窗剪枝
- Lance：多表随机投影 LSH（全阈值稳定 92.2%）
- **SSIM/Template/FastDup/SIFT/ORB：代理哈希预筛（8×8 均值哈希 + PopCount 逐对筛选）**
  实测把这5 个算法的比对总耗时从 199s 降到 1.8s（113 倍）

## SIFT/ORB 工作尺寸
- 上限设为最长边 **256**（原先 ORB 会把图「放大」到 480，实测反而更慢且无收益）
- 依据：--quality 实测 400/320/256/192 下相似度完全一致（重复对 1.0、独立图 0.0）
- SIFT 单线程特征耗时 17.68ms → 8.27ms

## 关键陷阱（血泪教训）
1. **OpenCvSharp 的 Mat 所有权**：从 `using` 变量返回前必须 `Clone()`，
   否则调用方拿到悬垂指针 → `AccessViolationException`。编译器查不出来
2. **预筛必须无漏报**：加断言穷举校验（暴力算全部超阈值对，确认都在候选集内）。
   直方图这类全维相似度没有哈希分带那样的局部等价性，启发式分桶会漏报
3. **文件操作要测试**：MoveFile 不自动改名（由调用方处理冲突），
   避免调用方记录的路径与实际落盘路径错位导致恢复失败
4. 测试数据要同时含低纹理图与高频纹理图，只用随机噪声图会漏掉真实缺陷
5. **重写 git 历史后要校验树对象哈希**：`git rev-parse HEAD^{tree}` 前后一致
   才能确认内容未变，只有作者变了

## Avalonia 11 的坑（从 WPF 迁移必看）
| 坑 | 说明 |
|---|---|
| `Visibility` 枚举不存在 | Avalonia 11 的 `IsVisible` 是 **bool**，照 WPF 习惯写会编译失败 |
| 无 `ResourceInclude` | 资源字典只能内联到 `Application.Resources` |
| `ResourceDictionary` 不能用 `StyleInclude` | 类型不匹配，运行时抛 `InvalidCastException` |
| `Grid` 无 `ColumnSpacing`/`RowSpacing` | Avalonia 11 不支持，只能用 Margin |
| `Cursor="Default"` 非法 | 只支持 `Hand` 等少数值 |
| `AppBuilder` 无 `Application` 属性 | 用 `Application.Current` |
| 遍历子控件 | `GetLogicalChildren`/`GetVisualChildren` 在 `LogicalExtensions`/`VisualExtensions`；两棵树会重复计数，需去重 |

**XAML 只在运行时解析，编译通过 ≠ 能跑**。必须有 UI 自动化测试。

## 跨线程：绝不能在 ViewModel/命令层用 ConfigureAwait(false)
`await xxx.ConfigureAwait(false)` 会让后续代码脱离 UI 线程。若之后修改了
绑定到 UI 的属性（StatusMessage / Progress / 集合），Avalonia 会抛
`InvalidOperationException: Call from invalid thread`。

本项目踩过的坑：`AsyncRelayCommand` 用 `ConfigureAwait(false)` 后，
`finally` 里复位 `IsExecuting` 在后台线程触发 `CanExecuteChanged`，
该事件被命令绑定读取 `Button.CanExecute` → 直接崩溃，启动即挂。

规则：
- ViewModel 与命令层：**不用** ConfigureAwait(false)
- `RaiseCanExecuteChanged` 必须先 `CheckAccess()`，否则 `Post` 到 UI 线程
- Services 层（不碰 UI）可以用 ConfigureAwait(false)

## UI 结构（2026-11-11 重构后）
- `App.axaml`：内联设计令牌（色板/字体层级/圆角）
- `Views/Controls.axaml`：所有控件样式集中定义，按语义分变体
- `Views/MainWindow.axaml`：**不再有 11 个算法 Tab 页**，改为下拉框 + 动态参数字段
- `Converters/AlgorithmConverters.cs`：11 个转换器控制参数字段的显示
  （新增参数只需改一处，原实现要改 11 处）

## 性能调优工具（Tests 项目内）
```bash
--profile <目录>   # 各算法特征/比对耗时与候选量，按耗时排序
--stage <目录>     # 特征计算分阶段：解码/灰度/代理哈希/各算法核心
--quality <目录>   # 验证工作尺寸对相似度质量的影响（决定能否缩小的依据）
--shot           # 无头截图（受 Avalonia Headless 限制，当前渲染为空白）
```

**UI 测试**：`UiTests.cs` 用 Avalonia Headless 构建控件树，
断言资源加载、样式生效、控件数量、转换器逻辑。断言数 103 → 117。
**关键经验**：OpenCV 内部已多线程，外层 `Parallel.For` 会产生线程竞争，
profile（并行）与 stage（单线程）的数字要**分开看**才能看清真实收益。

## 命令
```bash
dotnet build                                    # 构建（应 0 错误 0 警告）
dotnet run --project Deduplicator.csproj       # 运行
dotnet run --project Deduplicator.Tests -c Release   # 测试（退出码 0 = 通过，可接 CI）
# 性能剖析（对任意图像目录）
dotnet run --project Deduplicator.Tests -c Release -- --profile <目录>
```

## Git
- **双远程**（同一份代码托管两处）：
  - `origin` → `git@gitee.com:ren-yongxiang/image_deduplicator.git`
  - `github` → `git@github.com:riantr/image_deduplicator.git`
- 身份：任勇祥 <14427461+ren-yongxiang@user.noreply.gitee.com>（全局已配）
- **必须用 SSH**：HTTPS 会卡在密码输入，非交互终端无法完成
  （GitHub 无令牌/gh CLI；本地 `id_ed25519` 已在两个平台授权）
- 上游追踪：`master` → `origin/master`（Gitee）
- **推送两个远程要分别执行**：`git push origin master` 然后 `git push github master`
  （⚠️ `git push origin github master` 是**错的** —— `github` 会被当作分支名，
  报 `src refspec github does not match any`；
  `push.default=all` 也不是合法值，只有 nothing/matching/simple/upstream/current）
- CI 配置在 `.github/workflows/ci.yml` 与 `.gitee/workflows/ci.yml`
  → **GitHub 上会真正生效**（Actions 原生支持），Gitee 需在仓库设置里启用 Gitee Go
- 回退锚点标签：`backup-before-author-fix`
- 仓库历史干净，无测试残留

## 当前状态
- 构建 0 错误 0 警告；测试 **119 项**全通过（含 16 项 UI 测试）
- 应用可正常启动运行（`dotnet run` 已实测无异常）
- 已推送到 Gitee 与 GitHub，两处与本地三方同步（`1182149`）
- XAML 未改动过，绑定属性保持原样
- **UI 已重构但仍需人工确认视觉效果**：自动化测试只验证「能构建、资源不缺失」，
  实际观感（配色协调、间距舒适）需真人过目