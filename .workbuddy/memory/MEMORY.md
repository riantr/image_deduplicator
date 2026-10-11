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

## 命令
```bash
dotnet build                                    # 构建（应 0 错误 0 警告）
dotnet run --project Deduplicator.csproj       # 运行
dotnet run --project Deduplicator.Tests -c Release   # 测试（退出码 0 = 通过，可接 CI）
```

## Git
- 托管在 Gitee（全局身份：任勇祥 <14427461+ren-yongxiang@user.noreply.gitee.com>）
- 尚未关联远程仓库，需 `git remote add origin <gitee地址>` 后推送
- CI 配置在 `.github/workflows/ci.yml` 与 `.gitee/workflows/ci.yml`
- 回退锚点标签：`backup-before-author-fix`

## 当前状态
- 构建 0 错误 0 警告；测试 **73 项**全通过
- git 仓库已初始化，5 次提交，工作区干净
- XAML 未改动过，绑定属性保持原样
- **UI 层未经人工验证**：删除、原图对调、文件名交换等交互建议实际跑一遍确认