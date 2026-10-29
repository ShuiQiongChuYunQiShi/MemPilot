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
