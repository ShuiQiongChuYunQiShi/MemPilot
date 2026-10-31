# 迭代优化清单（数据驱动）

> 基线：2026-10-28/29 两天遥测（249 条 5 分钟采样、48 条决策事件）+ 现有代码逐点对账。
> 目标版本：v0.1.4 起的迭代。完成一项勾一项，验收后更新状态。

## P0 — 正确性与安全

- [x] **P0.1 系统级 Commit 压力看门狗** ✅ 2026-10-29
  - 证据：2026-10-29 18:29–18:39 ollama 加载模型，Commit 17.7→39.4GB（+22GB/10min）、可用 15.2→3.3GB，决策日志零条目。GrowthWatch 只看单进程工作集 ≥512MB，模型加载走 commit/页面文件，天然漏报。
  - 实现：`ResidentEngine.WatchCommitPressure`——每 tick 记录滚动 10 分钟窗口；Commit 增长 ≥8GB 或余量 <10% → `DecisionEntry(Kind=CommitWatch)`，30 分钟冷却。
  - 验收：待真机 ollama 加载时复核（代码路径与重放阈值逻辑已就位）。

- [x] **P0.2 反作弊误伤的真实数据断言** ✅ 2026-10-29
  - 证据：ACE-Helper / SGuardUpdate64 实测住在 `F:\WeGameApps\...\AntiCheatExpert\`（lookups 缓存实证）。规则顺序已保证安全，缺真实数据侧守卫。
  - 实现：`EnumerationIntegrationTests.AntiCheatPathsNeverGameInner`——真实枚举中路径含 anticheat 的进程必须判为 AntiCheat 且受保护；无此类进程时真空通过。
  - 验收：本机跑绿（74 测试全过）。

- [x] **P0.3 睡眠/断档语义化 + 本地日期文件名** ✅ 2026-10-29
  - 证据：02:12→12:21 十小时空白无法区分睡眠/退出/崩溃；文件按 UTC 日期滚动，凌晨 0–8 点样本落在“前一天”文件。
  - 实现：`UsageRecorder` 文件改本地日期命名（RecordSample/ReadWindow/Prune 三处一致）；`UsageSample` 新增 `GapMinutes`/`Event`；启动时读最后一条采样作断档基线，gap≥10min 标 `resume`。
  - 验收：新增 2 条单测（本地文件名、6 小时断档标 resume），74 测试全绿。

## P1 — AI 三件套第一步

- [x] **P1.4 习惯档案 v1（何时提醒腾内存）** ✅ 2026-10-29
  - 证据：小时均值 19–21 点 5.6/4.8GB（全天最紧）、13–16 点工作高峰 7.5~13GB。
  - 实现：`HabitProfile.Build` 按本地小时聚合可用内存（P25 分类：<6 紧张 / <10 偏紧 / <14 正常 / ≥14 宽松）；`ResidentEngine.CheckHabitHint` 在紧张时段前 30 分钟写 `HabitHint`（6 小时冷却）；历史页摘要追加时段画像。
  - 验收：3 条单测（P25 等级、连续时段合并文案、空历史）全绿；真机画像随数据积累生效。

- [x] **P1.5 腾挪预测 v1（历史加权排序）** ✅ 2026-10-29
  - 证据：python、NZMClient、DeltaForce、llama-server 反复登场；msedge/Weixin 为高频“受害者”。BuildPlan 有风险分但无历史。
  - 实现：`UsageAnalytics.ComputeProcessWeights` = 出镜率(0..1) × 峰值 P90(GB)；`BuildPlan` 新增可选 `historyWeights` 参数，排序规则变为「风险档 → 历史权重 → 体量」，VM 侧用近 7 天采样计算传入（Core 不碰文件 IO）。
  - 验收：3 条单测（权重排序、缺省行为不变、权重计算），81 测试全绿。

- [x] **P1.6 孤儿残留检测（服务型垃圾）** ✅ 2026-10-29
  - 证据：QQLive 退场后 QQLiveService 110–127MB 全程驻留；wetype_update 47 次出镜；AlibabaProtect 常驻。
  - 实现：`ResidualDetector`（宿主映射：QQLiveService/QQLiveLCService→QQLive、wetype_update→输入法三件套、lghub_updater→lghub、promecefpluginhost→WPS 主程序）；`UnusedDetector` 在候选理由中追加「残留服务（宿主 X 已退出）」，仅提示不处置，服务型仍被 IsService 排除。
  - 验收：4 条单测（宿主缺失/存在/未知进程），78 测试全绿；真机下次关腾讯视频后可复核标注。

## P2 — 体验与工程

- [x] **P2.7 可清理页垃圾分组置顶** ✅ 2026-10-29（UnusedCandidates 排序：Bloatware 优先 → 可回收量降序）
- [x] **P2.8 历史页标注采样缺口** ✅ 2026-10-29（摘要追加「最长 N 分钟未运行（睡眠/退出）」）
- [x] **P2.9 GrowthWatch 冷却键改 StateKey** ✅ 2026-10-29（(PID, 启动时间) 键，PID 复用不再误冷却）
- [x] **P2.10 Top 门槛 64MB→32MB** ✅ 2026-10-29（残留服务 50–110MB 段不再被历史遗漏）
- [x] **P2.11 wharf 证据链** ✅ 2026-10-29（干净树提交后 `wharf test` 留 EV）
- [x] **P2.12 v0.1.4 发布** ✅ 2026-10-29（commit 23f5805 / tag v0.1.4；release workflow 成功，资产：portable zip + win-x64 exe + SHA256SUMS）

## 建议顺序

P0.1 + P0.3（同一批动 ResidentEngine/UsageRecorder）→ P0.2 → P1.4 → P1.6 → P1.5 → P2 随版本带。

## P3 — 垃圾识别打磨（长期，用户明确要求"慢慢打磨"）

垃圾判定原则：**看身份与关系，不看"有没有窗口"**——无界面不等于垃圾（ollama/mysql/无界面内核常驻都正常），有窗口也不豁免垃圾身份（只把处置降级为 L1 回收工作集）。证据分四层，处置按证据等级门控：

- [x] **P3.1 证据分级与处置门控** ✅ 2026-10-29（`JunkSweepItem.Evidence`：vendor-signature / exact-name / host-relation，写入 JunkSweep 决策日志；自动清扫门控维持 A/B/C+D 宿主退出）
- [x] **P3.2 行为证据采集** ✅ 2026-10-29（`BehaviorLedger`：按进程名累计 在场/无窗口/空闲/有声/孤儿 次数与峰值内存，JSON 持久化 `%APPDATA%\MemPilot\behavior.json`，约 5 分钟落盘一次；只入画像不定级）
- [ ] **P3.3 Unknown 复核队列**：可清理页展示 Top-N（按驻留内存×出现频率）未知进程，一键联网核实或人工投票，结果落 FeedbackStore（已有覆盖机制）。
- [ ] **P3.4 规则卫生审计**：每条规则带来源与日期（注释已有雏形）；每周自检报告：规则命中次数、被用户保留覆盖次数、0 命中的死规则——死规则清理、误报规则降级。
- [ ] **P3.5 反馈驱动晋升**：用户对非规则进程连续 n 次手动结束 → 在首页提议"收录为垃圾规则"；用户标记保留 → 白名单 + 对应规则置信度降级。永不自动扩大规则。
- [x] **P3.6 垃圾证据分** ✅ 2026-10-29（`BehaviorLedger.JunkScore`＝无窗口占比×0.4+空闲占比×0.3+在场频率×0.3+孤儿证据 1 分；可清理页新增"驻留分"列，可解释排序）

安全不变式（硬编码，任何配置不可放宽）：受保护/系统关键/安全软件/反作弊/驱动/未识别永不清扫；Windows 服务不清扫（需手动禁用）；前台/有声/存活不足/白名单永不动作。

## 版本迭代计划（2026-10-30 制定，基于两天遥测+代码实测）

### v0.3.6 — 历史曲线"打开了不显示"修复（2026-11-01，待发布）

用户报：历史页内存曲线特别慢、打开后没有曲线。排查发现是**真 bug**，不是慢：

- **根因**：图表点集用共享 `PointCollection` + `Clear()/Add()` 变异。实测视觉树上 `Points.Count=231`，但 `Polyline.RenderedGeometry` 为空、`ActualWidth=0` —— Shape 从未重建几何，所以一片空白（摘要与表格正常更新，更容易让人以为是"还在算"）。
- [x] 六条点集（内存/显存曲线 + 面积 + 主页火花线）改为**整体替换**（`new PointCollection` + `PropertyChanged`），与项目既有"集合必须整体替换"规则同源。
- [x] 去掉 `LoadHistory` 的 10 秒节流（实测会静默跳过 → "点了没反应"），只保留 0.8 秒防连击；首次进入先显示"正在统计历史数据…"。
- [x] 统计失败不再静默 `return`，摘要行显示错误原因。
- [x] 从托盘恢复窗口时，若当前停在历史页，主动补一次统计（页签没变不会触发 SelectionChanged，此前会一直空着）。
- [x] 守卫测试 `ChartCollectionGuardTests`（12 条）：图表点集禁止 `Clear()`、必须是可整体替换的属性。
- 测试 210→222；真机截图确认两条曲线恢复渲染（内存 232 点 / 显存 131 点）。

### v0.3.5 — 曲线口径统一（2026-11-01，待发布）

用户指出：内存曲线画"可用"、显存曲线画"占用"，同一页两种方向，且都没有纵轴刻度。讨论后拍板统一为**占用率**（与进度条/任务管理器/容量图心智一致），并补参考线。

- [x] 历史页内存曲线：`Avail/Total` → `(Total-Avail)/Total`；caption 改「内存占用率（越高越紧张；可用 GB 见上方摘要）」；摘要行补「最近可用 x GB」（可执行数字不丢）。
- [x] 主页火花线：存 `AvailablePhysicalBytes` → 占用率，标题改「内存占用趋势（越高越紧张）」。
- [x] 两张 640×160 曲线（内存 + 显存）加 0/50/100% 三档虚线参考线与右侧百分比刻度（Canvas 宽 640→700，绘制区不变）。
- 数据无需迁移：采样里存的是 `Avail`/`Total`，占用是算出来的，老样本照样能画。
- 测试 210 全绿（曲线无单测，靠 caption 与真实数据核对）；真机启动零崩溃。

### v0.3.4 — GPU 观测与记录（2026-11-01，待发布）

**缘起**：用户问「GPU 内存是不是读不到」。实测结论：能读到，只是我们只走了 DXGI——本机 `CreateDXGIFactory1` 返回 `E_NOINTERFACE`，落到 GDI 兜底后只有容量、没有用量；而 PDH 的 `GPU Process Memory(*)` / `GPU Adapter Memory(*)` 一直可用（16.6 GB 对 nvidia-smi 的 15821 MiB，误差 <1%）。

- [x] **PdhQuery 统一读取层**（`Helpers/PdhQuery.cs`）：PDH 查询/计数器封装，`PDH_MORE_DATA` 时扩容重试（旧实现会整帧丢弃 → 曲线假空洞）；`GpuUsageService` 改为复用（GPU% 语义不变）。
- [x] **GpuMemoryService**：逐进程 `Local Usage`/`Non Local Usage` + 适配器 `Dedicated Usage`；`UsedBytes` 优先适配器级、退而进程合计，不可用返回 **null**（绝不用 0 冒充）。
- [x] **GpuInfoService 兜底与诊断**：失败记 HRESULT（本机 `hr=0x80004002`）；新增纯函数 `ApplyPdhFallback`——单卡用合计、多卡按 LUID 匹配、**无 LUID 不猜**；`UsageSource` 标 `pdh` 并在摘要显示「（PDH 汇总）」；缓存 60s→10s。
- [x] **30 秒独立采样**（`GpuRecorder`/`GpuMonitor`）：`history/gpu-yyyyMMdd.jsonl` 单独落盘、保留 30 天；记录 `Used/Total/Src/Procs/Top5(name+pid+专用+共享+GPU%)`；`Event` 标 `resume`（间隔 >3 倍）与 `gpu-spike`（单条跳增 ≥4 GB，只标记不告警）。设置页可开关、间隔 15/30/60/300 秒。
- [x] **UI**：进程页「显存」列（不可用显示 `-`）；主页 GPU 卡第二行「已用/总量（%）· 来源」；历史页独立「显存」卡片（24h 曲线 + 显存大户 Top10 + 峰值/跳增摘要）。顺手修掉历史页在 UI 线程重复读 7 天窗口的老 bug。
- [x] **回归守卫**：`GpuRecorderTests`（往返/跳增/醒来/保留）、`GpuHistoryTests`（缺数据不画假 0、峰值均值）、`GpuMemoryServiceTests`（环境容忍 + **PDH_FMT_LARGE 溢出守卫**：写成 LONG 时 16 GB 会截成 `int.MaxValue`，实测踩到过）、`GpuInfoServiceTests` 补兜底与 HRESULT。
- 测试 188→210。真机：30 秒三条样本、`Src=pdh-adapter`、Top1 为 `python`（ComfyUI 常驻 15 GB，正是用户视频生成后的残留），crash.log 零增量。

### v0.3.3 — god-class 拆分 + UI 回归守卫（2026-10-31，待发布）

- [x] **MainWindow 按页签拆 partial**：`MainWindow.xaml.cs` 1198 → 335 行；新增 `ProcessPage`(24 成员)/`CleanupPage`(23)/`MakeRoomPage`(9)/`SettingsPage`(10)/`Tabs`(23) 五个 partial。机械拆分，只动文件边界不动逻辑，靠编译器与后续真机启动验证兜底。
- [x] **MainViewModel 拆 partial**：988 → 577 行；新增 `ProcessPage`/`CleanupPage`/`HistoryPage`/`SettingsPage` 四个 partial（跨页编排如 `QuickReliefAsync` 留在核心）。`MainViewModel.cs` 现为 `public partial class`。
- [x] **页签重建回归守卫**（prism test/ui-regression）：新增 `TabRebuildPolicy`（Core 纯函数）——页签键解析、4s 节流、只有进程页重建进程集合；VM 的 `UpdateCollections`/`RebuildActiveTab` 改为经策略判断。7 条测试：策略语义 + 源码静态扫描（进程集合只允许在 Process 分支重建一次、`UpdateCollections` 不得碰集合、必须调用策略）。
- [x] **签名决策（2026-10-31，用户拍板）：不购买商业代码签名证书**（¥1–3k/年，且自签对他人机器无效）。替代方案：官方产物保持未签名，Release 正文自动追加「哈希校验命令 + 360 放行说明」；README 写清决策与免费路径（公开仓库攒信誉 > Azure Trusted Signing > 商业证书）。工程链路保留 `SIGN_ENABLED` 开关，将来有证书零改动接入。
- 测试 171→188。真机：Release 构建起活、crash.log 零增量、引擎刷新写入 decisions.json 正常。

### v0.3.2 — 棱镜第二轮整改（2026-10-31，待发布）

第二轮棱镜（证据包 ev-22be20dd7790，HEAD 3d573d4）：19 条 findings，新增 6 / 已解决 4 / 仍活跃 9；两个历史 P0 全部认账关闭。本轮先做成本最低、收益最直接的一批：

- [x] **CI 机器产物**（prism test/coverage）：`dotnet test --logger trx --collect:"XPlat Code Coverage"` + TestResults 上传；`MemPilot.Core` 行覆盖门槛 **50%**（实测基线 52.4%，目标 60%——定一个当下就红的口槛等于没门槛，先锁现状再迭代抬升）。新增覆盖率汇总步骤打印 TRX 用例数与 Core 行覆盖。
- [x] **锁文件**（prism stack/dependency）：三个 csproj 启用 `RestorePackagesWithLockFile`，`packages.lock.json` 入库；CI/release 均先 `dotnet restore --locked-mode`。
- [x] **文档漂移**（prism docs/drift）：HANDOFF.md 更新到 v0.3.1/152 用例并加"只写不常变的事实"维护约定；README 删除重复的「腾挪工作流」段、补 v0.3.0/v0.3.1 功能、修正「零 NuGet」（实际 1 个包）、补「许可」段。
- [x] **进程表密度**（prism ui/table-density）：PID/名称冻结（`FrozenColumnCount=2`）；新增「列设置 ▾」菜单（11 个可切换列）并把默认收起的 9 个低频列写入 `AppConfig.ProcessHiddenColumns`（列改名会静默失效 → 静态守卫测试锁死）。
- [x] **用途筛选**（prism product/classification-quality）：进程页新增「全部用途/工作/娱乐/AI/系统/其他/未知」下拉，选择持久化到 `AppConfig.WorkloadFilter`；筛选先于树构建（树里不残留未命中父节点）。守卫测试断言下拉项与 `WorkloadClassifier.DisplayName` 完全一致。
- [x] **影子结算**（prism completeness/shadow-loop）：`SceneShadowReport` 纯函数——影子决断/自动切换/人工切换按本地日聚合；±10 分钟内的人工切换才算标签（同向=一致、异向=分歧、否则「未验证」）；无标签时只报频率不编造准确率。设置页「场景影子校准（近 7 天）」卡片 + 刷新按钮。
- [x] **持久化收敛**（prism maintainability/duplication + backend/error-handling）：`JsonFileStore`（tmp+Move 原子写、重试、最终失败计数）+ `StoreDiagnostics`（内存计数 + 最近 20 条 + 可选 diag.log）；DecisionLog/AuditLogger/FeedbackStore/ConfigStore/SuspendRegistry/lookups 六处改为调用，设置页显示落盘诊断一句话。
- [x] 签名入 CI 的**可选项**：release.yml 增加 `SIGN_ENABLED` 变量门控的签名步骤（缺密钥=告警跳过，不打断发布链）；证书与变量由用户侧配置后生效。
- 测试 152→171（新增：进程页列设置/用途筛选守卫 5、影子结算 7、JsonFileStore 7）。
- 未做：god-class 拆分、UI 性能回归守卫、签名默认启用（需用户上传证书）。

### v0.3.1 — 用途维度（2026-10-31，已发布安装）

- [x] `WorkloadKind`（娱乐/工作/AI/系统/其他/未知）+ `WorkloadClassifier`：身份（Category）之上派生用途；强类别优先（游戏/AI/系统/开发容器），名称/清单显示名/清单发布者关键词补充；歧义进程（浏览器/聊天）归"其他"不硬猜；Bloatware 不参与用途判定。用途仅展示/排序，**不构成处置依据**。
- [x] 进程页新增『用途』列（WorkloadTextConverter）；刷新循环逐进程派生。真机分布：系统 255 / 工作 135 / 其他 120 / 未知 6 / AI 2 / 娱乐 1。
- [x] 修复真机暴露的短名子串误伤：`et`（WPS 表格）把 wetype/SocketServer 误判"工作"——短名改精确匹配（wps/et/wpp/code/cmd/project），回归测试锁死。
- 测试 136→152。

### v0.3.0 — 程序清单（2026-10-31，已发布安装）

- [x] `ProgramCatalogService` 三源采集：注册表 Uninstall（HKLM64/HKLM32/HKCU 双视图、SystemComponent 过滤、DisplayIcon/UninstallString 反推 exe）→ 开始菜单 .lnk（IShellLinkW+IPersistFile COM，注册/所有用户）→ 常见安装根按需扫描（Program Files/(x86)/%LOCALAPPDATA%\Programs/非系统盘同名根，深度 2，跳过 reparse point；**不做全盘递归**）。
- [x] 索引与持久化：exe 路径字典（O(1)）+ 安装目录最长前缀（边界校验）；`programs.json`（SchemaVersion+24h TTL、原子写盘、后台构建）。真机实测 328 条/238 目录索引/1.27s。
- [x] 分类接入：`ClassificationEngine.Catalog` 可选依赖——填 `ProgramDisplayName/ProgramPublisher`；签名不可用时用清单发布者匹配 `PublisherRules`（Docker Inc→ContainerVm）；规则全不中但属于已安装程序 → ThirdPartyApp（不再 Unknown）。**清单只作证据、不构成处置依据**。
- [x] UI：进程页『所属程序』列；设置页『程序清单』卡片（信息 + 重建按钮）。
- [x] `FeedbackStore.Find` 去掉按发布者泛匹配（一条"腾讯=游戏"曾误伤全部腾讯应用）；software 级反馈按安装目录前缀匹配。
- 测试 128→136。

### v0.2.3 — 缺陷修复（2026-10-31，已发布安装）

- [x] **GPU 链路**：`_gpu` 赋值移出 `uiActive` 门控（StartMinimizedToTray 下首刷隐藏 → 永远不可用）+ `ShowWindow` 后立即刷新；`DXGI_ADAPTER_DESC1.LuidLowPart` 由 `long` 改 `uint`（LUID 4+4，修正 `Flags` 错位）；GDI 兜底去掉 `ATTACHED_TO_DESKTOP` 过滤（远程/虚拟会话全滤），改排除镜像驱动与 Basic Render；**修复 v0.2.2 兜底死代码**（DXGI 工厂创建失败时直接 return 绕过兜底）；空结果不再缓存 60s；GDI 无用量时显示「（用量不可用）」不再假 0%；新增 `LastDiag` 诊断。实测原 DXGI 失败上下文现返回 `NVIDIA GeForce RTX 5060 Ti 15.93GB`。
- [x] **容器识别**：ContainerVm 前移到 DevTool 之前（`\docker\` 路径此前把 Docker 全家误标开发工具且 ContainerVm 永不达）；`DevPathFragments` 移除 `\docker\`，新增 `ContainerVmPathFragments`（`\docker\`/`\wsl\`/`\windowsapps\docker`）；`ContainerVmNames` 补 `docker desktop`/`com.docker.*`/`cagent`/`wslservice`/`wslhost`/`wslrelay`/`vmmem*`；`PublisherRules` 加 `docker→ContainerVm`；vmmem 前缀特判。
- [x] **在线核实**：`not_configured`/`disabled` 不写缓存，读缓存跳过旧无效状态（配置端点后立即生效）。
- 测试 117→128（容器分类 10 条 + GPU 摘要断言）。

### v0.2.0 — 视觉基础（亮色，2026-10-31 开工）

- [x] 设计令牌：颜色（Slate 中性色 + 单一强调色 #2563EB）、字阶五档（28/20/15/13/11）、圆角（10/8/6）、间距令牌，全部进 `App.xaml`。
- [x] 导航刷新：选中态由「大块蓝填充」改为「左侧 3px 强调条 + 深色底」，hover 统一；字阶走令牌。
- [x] 卡片/按钮/表格：卡片圆角 12→10、阴影减淡（0.035/深度1）、按钮字号令牌、表格行高 26→28、斑马纹更淡。
- [x] **场景语义：游戏模式 → 娱乐模式（游戏/直播/影视）**（用户裁决：看游戏直播和玩游戏属于同一个场景，判定不区分"玩/看"）。老配置迁移：改名 + 并入播放器/直播平台保护模式（bilibili/douyu/huya/iqiyi/qyplayer/qqlive/potplayer/vlc/mpv/netflix/youtube），只增不删。
- [x] **v0.2.1 界面重构（方向 B · 双栏工作台）**：左侧导航改浅色（白底+右边框），选中项为靛蓝浅底 pill（#EEF2FF/#4F46E5）；强调色改靛蓝 #4F46E5；主页重构为双栏——主栏（内存/场景/Top 分类/决策/汇总）+ 右侧洞察栏（告警卡、时段画像、待处理、快捷动作）；表格改回收虚拟化（前轮）+ 表头改 11px 中性灰。
- [x] **按 Open Design brand-spec 精修令牌**（`mempilot-ui-6b9a` 生成稿）：核心六色对齐（--bg #F7F7FB / --fg #17172B / --muted #6B6B85 / --border #ECECF5 / --accent #4F46E5 / --surface #FFF）、派生 warn 家族（#FFF9EC/#7A4B05）与 ok #12734F、字阶 20/15/13/12/11、字体族 Segoe UI Variable、卡片 12px/控件 9px 圆角。
- 生成稿 residue：Open Design 项目 `mempilot-ui-6b9a`（主页高保真 + tokens 页 + brand-spec.md），daemon 重启后 pull 失败（已提 FB-20261031-001），产物从磁盘恢复。
- [ ] 后续（v0.2.1）：页面头/空状态/图表/告警条视觉统一；用 Open Design 出亮色方向选型后再做整体走查（当前先落地令牌与导航，避免大改后返工）。

### 场景识别 v2（计划编号 v0.2.2；实际随 v0.2.0 一次发布，v0.2.1 为启动崩溃 hotfix）

补充裁决（2026-10-31）：**场景分类按"活动类别"不按"具体应用"**——看游戏直播 = 玩游戏 = 娱乐；看视频/听歌也归娱乐。因此判定不需要区分"玩/看"，只需识别「娱乐活动」：全屏/GPU 高/音频活跃/输入活跃度（低=观看、高=操作）都可归入娱乐。这移除了原设计里"看直播 vs 玩"的伪边界。

- [x] **信号层**：`InputActivity`（GetLastInputInfo，只取时间戳不采集内容）+ `GpuUsageService`（PDH \GPU Engine(*) 逐进程 GPU% ，真机验证可用；不可用时按"缺信号"处理不当 0 分）+ 标题词表 + 进程树/分类（已有）。
- [x] **打分模型**（`SceneDetector`）：娱乐 = 全屏30 + GPU20/10 + 输入15 + 反作弊15 + 游戏进程活跃15 + 前台是游戏15 + 音频5 + 标题10 + 全屏影音15；AI = 常驻25 + GPU30/10 + 负载20 + 无输入10 + 前台10；开发 = 前台30 + 输入15 + 编译/DB20 + 标题10。切换条件「最高分 ≥60 且领先 ≥20」，否则只建议。
- [x] **影子模式**：默认开启（`SceneShadowEnabled`），只记录 `SceneShadow` 决策（目标变化且 5 分钟间隔），不动作；自动切换仍由 `AutoModeSwitch` 控制，复用 2 轮滞回 + 60s 间隔。
- [x] **诊断面板**：模式页顶部卡片显示三场景分数条 + 判定摘要/理由。
- [ ] 校准：用真实使用数据跑一周影子记录，对比 `ModeSwitch` 手动审计，调权重与阈值后再启用自动切换。
- 测试 115（8 条场景判定 + 1 条 GPU 采集真机验证）。

### v0.1.6 — 性能与交互（已完成，见下）

**根因（代码+数据双重定位）**
1. 每次刷新（10s）在 UI 线程重建 3 个 ObservableCollection（Processes/Groups/FilteredProcesses）+ ApplyFilter + DataGrid 全量重绑：进程页 17 列 × ~550 行、可清理页 11 列。今天内存压力峰值（00:39 可用 0.1GB、16 点小时均值 4.5GB）下 WPF 布局渲染被放大到秒级——用户点页签撞上重建周期，体感就是"卡 4-5 秒"。
2. 后台线程每轮对 ~550 个进程逐个调用 WinVerifyTrust（`ProcessService.cs:113`，无缓存）——CPU 长期高位（今天进程数 221~841），进一步加剧 UI 卡顿。
3. 历史页 LoadHistory 在页签切换时同步读 7 天采样并构建图表点集（可清理页无此问题，但同类隐患）。
4. 可清理页底部 8 个按钮 + 提示文字单行横排：1280 宽窗口放不全（已确认），窄窗更糟。

**修复项**
- [x] ~~P5.1 WinVerifyTrust 缓存~~ **核实结果：TrustVerifier 早已有 (path, lastWrite) 缓存，此项不存在**（如实修正之前判断）。真正的大头在 UI 线程重建与刷新管道阻塞，见 P5.2/P5.6。
- [x] P5.2 页签感知的 UI 更新：只重建当前可见页签的集合（📋 进程全量/🧹 候选/⏸ 挂起/🧠 决策/主页轻量），可见页最短 4s 节流；切页签时由 `OnTabActivated` 强制重建。
- [x] P5.3 列精简：可清理页 11→9 列（去掉 预计释放(提交)、风险）；DataGrid 加 `VirtualizationMode=Recycling` + `ScrollUnit=Pixel`。
- [x] P5.4 按钮区重构：单条动作（L1-L4）一行、「批量 ▾」菜单收纳全部候选 L1 / 一键清理全部 / 清扫垃圾驻守；「一键压缩全部进程工作集」移到系统内存清理页；WrapPanel 自适应窄窗。
- [x] P5.5 LoadHistory 异步化：读盘与聚合放后台线程，UI 只落点集；10s 内复用结果。
- [x] P5.6 刷新耗时自诊断：状态栏显示「枚举 Xms · 分类 Yms」。
- [x] 附带修复：LookupUnknowns 等待上限 15s→3s（刷新管道阻塞源）。
- 验收：待发布安装后真机复测（页签切换 p95 < 200ms；状态栏耗时数字肉眼可查）。

### v0.1.8 — 棱镜审查 P0（2026-10-30，prism 评审后追加）

- [x] **告警触达**：`ResidentEngine.Alert` 事件 → 托盘气泡（`TrayIconService.ShowBalloon`）+ 主页横幅（`HasAlert`/`AlertBannerTitle/Text`）；CommitWatch 与 HabitHint 双通道触达；设置页「重要提示触达」开关（默认开，取消=仅写决策日志）。依据：prism ux/high——15 次 CommitWatch + 13 次 HabitHint 两天全部只进日志，含 00:39 可用 0.1GB 事件。
- [x] **低风险自动档 + 一键腾挪**：设置页「应用低风险自动档」按钮（自动优化 + 自动清扫孤儿残留，清理仍只 L1）；主页告警横幅带「立即低风险腾挪（只压缩/清扫，不结束进程）」按钮（`QuickReliefAsync` = 全机工作集压缩 + 孤儿残留清扫）。依据：prism product/high——默认观察模式与真实工作场景（0.1GB 濒危）存在鸿沟。
- 其余 11 条 prism findings 待排期：`~/.wharf/projects/memorytool/prism/current.json`（含锁文件、覆盖率、静默 catch、god class、未签名等，见 rounds 存档）；下一轮 `cap run prism -- diff` 复核。

### v0.1.7 — 识别打磨 + 驻留分修正

- [x] P3.6b 驻留分加体量因子（对数，峰值 ≥2GB 记满分权重）——修正 1~25MB 系统服务与 400MB 更新器同拿 10.0 分的缺陷（今日台账实证）。
- [x] P3.3 Unknown 复核队列（可清理页可折叠区：高占用×高频未识别进程 Top15 + 标记为垃圾/标记保留/联网核实，写入反馈库）。
- [x] P3.5 反馈驱动晋升（近 30 天手动结束 ≥3 次且不在规则中的进程 → 决策日志提议收录，Kind=PromoteRule；永不自动扩大规则）。
- [x] P3.4 规则卫生审计（每周一次 RuleHygiene 周报写决策日志：命中规则/死规则候选/用户保留覆盖/晋升建议；只报告不修改）。
- [x] 低风险自动清扫选项：`AutoJunkSweepOrphansOnly`（自动清扫只清宿主已退出的孤儿残留）——设置页开关。

### v0.2.0 — P4 M1 租约 API（视频生成接入）

- 本地 HTTP + token：`/memory`、`/habit/pressure`、`/plan`（干跑）、`/lease`、`/lease/{id}`、`/sweep`；soft/balanced/aggressive 策略
- 首个接入样例：python 视频生成脚本（今日 00:34–01:20 峰值 17.4GB、00:39 可用 0.1GB/提交 99.5% 即需求论据）
- 验收：脚本开跑前调用 lease，CommitWatch 不再出现"余量 <10%"事件

### v0.2.1 — M2 硬租约 + 码头注册；M3 NativeAOT 拆分（远期）

## P4 — 展望：API 调用能力与租借式内存腾挪（等功能稳定后启动）

**核心语义**：把"腾内存"从人工界面动作升级为可被程序调用的服务——大内存任务（视频生成等）开跑前申请"内存租约"，MemPilot 腾出并让渡，任务结束归还恢复；最终注册为码头外置能力，wharf 管辖的任何项目可调用。

**租约模型**（照搬 wharf `res lease --ttl --mode exclusive` 语义）：
- 申请(目标GB, policy, TTL, requesterPid, 理由) → 返回租约句柄 + **实测达成量**（非承诺量）；凑不够返回 409+缺口明细
- 占用期 CommitWatch 反向守护（让渡被侵蚀→告警）；归还=恢复挂起+重启被停服务+审计；调用方死亡→TTL 自动恢复（挂起登记簿兜底）
- 单活跃租约模型；干跑接口 `POST /plan`（零风险预览）

**政策（用户已拍板，2026-10-29）**：除 系统级进程、MemPilot 自身及关联、调用方自己及其进程树、白名单 外，**所有第三方均可处置（L1→L4 全梯队）**——需求本质是"给工作让路"。两个读法都按安全方式落地：requesterPid 及其子树进硬不变式。策略预设 `soft`（清扫+L1+备用列表）/ `balanced`（+L2 挂起）/ `aggressive`（+服务停止+L3+L4，工作场景默认档）；`includeForeground`/`includeAudio` 为参数位默认关。执行顺序代价从小到大，凑够目标即停手。

**新动作级别（服务形态的可逆硬腾挪）**：Windows 服务不走 TerminateProcess（SCM 会复活），走 SCM 停止 + 租约归还时可重启——记录停过哪些服务。

**API 面（M1）**：`GET /memory`、`GET /habit/pressure`、`POST /plan`（干跑）、`POST /lease`、`GET /lease/{id}`、`DELETE /lease/{id}`、`POST /sweep`；仅 localhost + Bearer token（照搬 wharf UI --rotate-token）；单活跃租约。

**架构路线**：M1 = HTTP 监听进 WPF 常驻进程（成本最低）；M2 = 挂起式硬租约 + TTL 自动恢复 + 前台/模式门控 + 码头 cap 注册（mem-lease 能力）；M3 = NativeAOT 服务拆分（路线图 #3 地基）+ 多调用方策略 + 习惯学习联动（"预约腾挪"：建议把大任务排到低压力时段）。

**复用映射**：BuildPlan/ExecutePlanAsync（计划与执行）、SuspendRegistry（恢复）、CommitWatch（占用期守护）、HabitProfile（时机建议）、JunkSweeper（软腾挪前置）、DecisionLog/Audit/采样 Event（全程留痕，回头分析腾挪是否真的帮到视频生成）。

**风险与对策**：软承诺被当硬承诺用（返回实测达成量+显式级别声明；硬需求走挂起式租约）；用户在场冲突（前台/有声参数位+白名单优先）；调用方死亡（TTL+登记簿）；360/权限（备用列表需管理员，API 不提权时该子动作诚实返回跳过）。

**待拍板**：首个真实调用方及其内存需求模板（决定 lease 默认档位）；触发模式（轮询 vs 回调 URL）；mem-lease 进码头的具体注册形态。
