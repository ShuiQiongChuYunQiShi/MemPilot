# 复盘 + 迭代计划：GPU 不可用、Docker 识别、程序清单

## Context

你报了两个问题，探查后确认一个是**真 bug 链**、一个是**规则顺序缺陷**：

1. **"GPU 信息不可用"**（机器有 RTX 5060 Ti，WMI/GDI 都能看到）。根因三层：
   - `_gpu` 只在 `if (uiActive)` 分支赋值（`MainViewModel.cs:397-404`），而 `StartMinimizedToTray=true` + 启动即 `Hide()` → **首刷永远走隐藏分支，`_gpu` 从未赋值**；`ShowWindow()` 不触发刷新，要等下一个 tick（最长 ~10s）。
   - `DXGI_ADAPTER_DESC1.LuidLowPart` 声明成 `long`（原生 LUID = DWORD+LONG = 4+4 字节），导致后续 `Flags` 读到错位 4 字节 → software-adapter 过滤读垃圾值，**可能把真显卡丢掉**（`GpuInfoService.cs:229`）。
   - GDI 兜底按 `ATTACHED_TO_DESKTOP(0x1)` 过滤（远程/虚拟显示会话会全滤掉）；空结果被缓存 60s；GDI 分支渲染成假的 "0 B (0%)"。
2. **Docker"识别不出来"**：`DevPathFragments` 含 `\docker\` 且 **DevTool 判定先于 ContainerVm** → Docker 全家被标"开发工具"，ContainerVm 分支永远不可达；没有 `docker` 发布者规则（签名明明是 `CN=Docker Inc`）；`vmmemWSL` 无路径 → Unknown，正好排在"未识别复核队列"第一。另外 `OnlineLookup` 会缓存 `not_configured` 7 天且先读缓存再查端点，配了端点也不生效。
3. **你提的"全盘程序目录+注册表+联网"**：调研结论是注册表 Uninstall + 开始菜单 + 已知安装根足以覆盖；**全盘递归扫描**分钟级 IO、杀软干扰、噪声大，不推荐（按你的选择做"安装根按需扫描"，不做全盘）。

## v0.2.3 — 缺陷修复（先发，可独立验证）

**GPU**
- `src/MemPilot.App/ViewModels/MainViewModel.cs`：`_gpu = gpuOut` 移出 `uiActive` 门控（只改内存字段+通知，仍在 dispatcher 内）；`src/MemPilot.App/MainWindow.xaml.cs` 的 `ShowWindow()` 后触发一次 `RefreshAsync()`，从托盘打开立即有值。
- `src/MemPilot.Core/Services/GpuInfoService.cs`：`LuidLowPart` 改 `uint`、`LuidHighPart` 保持 `int`（LUID 8 字节对齐），修正 `Flags` 偏移；software 跳过分支补 `ReleaseComObject`；GDI 过滤改为"排除 MIRRORING_DRIVER(0x8) 与 Basic Render"，补 `ExactSpelling=true`；空结果不缓存（或 TTL 5s）；失败原因写诊断日志（不再无声 `catch{}`）。
- `src/MemPilot.Core/Models/Models.cs`：`GpuAdapterInfo.Summary` 在 `UsedVideoMemoryBytes==0` 时显示 `"{Name}  {VRAM}（用量不可用）"`，不再假 0%。
- `tests/MemPilot.Tests/GpuInfoServiceTests.cs`：去掉恒真断言，改为"有适配器时名称/厂商非空；环境不可见时显式 Skip 并输出原因"。

**容器/进程识别**
- `src/MemPilot.Core/Services/ClassificationEngine.cs`：ContainerVm 判定前移到 DevTool 之前；`DevPathFragments` 移除 `\docker\`；新增 `ContainerVmPathFragments = { "\\docker\\", "\\wsl\\", "\\windowsapps\\docker" }`；`PublisherRules` 加 `("docker", ContainerVm)`；`ContainerVmNames` 补 `docker desktop`、`com.docker.backend`、`com.docker.build`、`cagent`、`wslservice`、`wslhost`、`wslrelay`；`vmmem`/`vmmemwsl` 名称前缀特判 ContainerVm。
- `src/MemPilot.Core/Services/OnlineLookupService.cs`：先查端点再读缓存；`not_configured`/`disabled` 不写缓存；端点从空变已配置时丢弃旧缓存。
- 新增 `tests/MemPilot.Tests/ContainerClassificationTests.cs`：Docker Desktop / com.docker.backend / com.docker.build / cagent / vmmemWSL / wslservice 分类断言 + `\docker\` 路径不再判 DevTool。

## v0.3.0 — 程序清单（Program Catalog）

1. **模型 + 服务**：`Models.cs` 加 `InstalledProgram`（DisplayName/Publisher/Version/InstallLocation/ExecutablePath/IconPath/Source/LastIndexedUtc）；新增 `src/MemPilot.Core/Services/ProgramCatalogService.cs`（仿 `BehaviorLedger.Load/Save` 与 `OnlineLookupService` 缓存范式）。
2. **采集三源**（按你选的范围）：
   - 注册表 Uninstall 三处（HKLM64/HKLM32/HKCU，`RegistryView` 双视图）：DisplayName/Publisher/DisplayIcon/InstallLocation/SystemComponent 过滤，从 InstallLocation/DisplayIcon 提取 exe；
   - 开始菜单两个 `Programs` 目录：`IShellLinkW`+`IPersistFile`+`CoCreateInstance` P/Invoke（声明放 `Helpers/NativeMethods.cs`，零新增 NuGet），取 lnk 名 + TargetPath；
   - 常见安装根按需扫描：Program Files / (x86) / `%LOCALAPPDATA%\Programs` / `%PROGRAMDATA%` + 非系统盘同名根，固定深度 2–3，跳过 reparse point/回收站；后台、可取消、有进度，默认"首次后台 + 设置页手动重建"。
   - 复用 `ProcessService.FillVersionInfo`（抽取为共享 `ExeMetadataCache`，保留 (path,lastWrite) 缓存）补 Company/Product/Description。
3. **索引 + 持久化**：exe 全路径 → 程序（O(1) 字典）；安装目录 → 程序（最长前缀）；`%APPDATA%\MemPilot\programs.json`（SchemaVersion+BuiltUtc、TTL 24h、增量重建、原子写盘）。
4. **接入分类**：`ClassificationEngine` 可选依赖 `ProgramCatalogService`，查询位置在硬规则（系统/安全/反作弊/驱动）之后、发布者/名称/路径规则之前；命中仅作证据（`CategoryReason = program_catalog:<DisplayName>`），未命中仍走原规则/Unknown——**清单不构成处置依据**。
5. **UI + 反馈**：进程页加"所属程序"列；设置页"重建程序清单"按钮 + 上次构建时间；`FeedbackStore.Find` 支持 `Scope=software`（按安装目录/exe 匹配），修掉"同发布者全命中"的误伤。

## 明确不做
- 全盘递归扫描（分钟级 IO、杀软干扰、噪声大）→ 用"已知安装根按需扫描"替代。
- WMI 当显存来源（`AdapterRAM` 32 位截断：本机 4293918720 假值 vs 真值 17103323136）；保留 DXGI + 注册表 `qwMemorySize`。

## 验证
1. `dotnet build` 0 错误；`dotnet test` 全绿（117 → 约 130）。
2. 真机：启动 3 秒内 GPU 卡片显示 "NVIDIA GeForce RTX 5060 Ti 15.9 GB"；趋势图完全在卡片内；Docker 启动后进程页出现"容器/虚拟机"分组、`vmmemWSL` 不再出现在未识别队列首行；程序清单构建后 Docker Desktop / WeGame 游戏 / 绿色版程序可识别。
3. 与码头 `cap run proc-classify --summary` 前后对照 Unknown 数量。
4. 发布：v0.2.3 → 真机验证 → v0.3.0；均走 `tools/publish-release.ps1`，安装后检查 crash.log 零增量。