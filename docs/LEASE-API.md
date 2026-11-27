# 租约 API 参考

MemPilot 的租约（Lease）让 AI 任务在加载模型前**申请**可用内存、结束后**归还**——
返回的是实测达成量而非承诺量。默认关闭：设置 →「租约 API」勾选启用。

- 只绑定 `127.0.0.1`，不监听外网
- 认证：`Authorization: Bearer <token>`；令牌在设置页显示/重新生成，
  落盘 `%APPDATA%\MemPilot\lease-token.txt`（同用户可读的边界，详见 SECURITY.md）
- 单活跃租约：同一时刻最多一个；冲突返回 `409`
- 全部响应 JSON camelCase；连接 `Connection: close`

## 端点

### GET /api/status

```json
{
  "version": "0.23.0",
  "availableGb": 16.03, "totalGb": 31.69,
  "commitFreePct": 62.6,
  "leaseActive": false,
  "capabilities": ["lease", "lease.renew", "lease.plan"],
  "limits": { "minTargetGb": 0.5, "maxTtlSeconds": 86400, "maxSuspendCount": 8 },
  "lease": null
}
```

`capabilities` 是能力发现（v0.23.0）：先看它再决定调什么，避免"试了才知道不支持"。
`limits` 里的 `maxSuspendCount` 与调度器同一个常量（`LeaseHardPlanner.MaxSuspendCount`），不会漂移。

### POST /api/plan

**零动作预演**（v0.23.0）：问"能不能拿到 N GB"，但不碰任何进程。

请求体同 `POST /api/lease`（`targetAvailableGb` 必填，`policy` / `requester` / `requesterPid` 可选）。

响应（200）：

| 字段 | 说明 |
|---|---|
| `currentAvailableGb` | 实时可用内存（不是快照） |
| `projectedAvailableGb` | 预计能达到的可用量，口径见 `projectionBasis` |
| `projectionBasis` | `current-only`（不动任何进程就有这么多，是下界）或 `hard-upper-bound`（当前 + 将要挂起的私有提交合计，是上界，实际释放 ≤ 此值） |
| `feasible` | `projected >= target` |
| `shortfallGb` | 与目标的缺口 |
| `wouldSuspend` | hard 策略下**将会**挂起的进程（`pid` / `name` / `privateBytes`），最多 `maxSuspendCount` 个 |
| `excluded` | 被安全边界挡住的进程统计：`total` + `byReason`（`caller`/`protected`/`category`/`workload`/`inUse`/`self`/`invalid`） |
| `denied` | 被排除的进程统计：`total`（进程数）+ `names`（去重后的进程名）+ `defaultCount`（内置名单命中）+ `callerCount`（你的 `denyNames` 命中）。与 `excluded` 刻意分开：`excluded` 是"安全边界不让动"，`denied` 是"别动"——混在一起会让调用方误以为这些进程有风险。**调用方不传 `denyNames` 时 `defaultCount` 也可能非零**，这是内置的网络基础设施保护（见下） |
| `snapshotAgeSeconds` | 进程快照年龄；**-1 = 还没有快照**（刚启动）。plan 基于上一次 tick 的列表，不是实时枚举 |
| `samplingWarm` | CPU/IO 采样是否已预热（前两次枚举没有基线，会把大量进程算成"忙"）。**`false` 时应稍后重试**，不要据此判断机器没余量 |

```json
{
  "targetAvailableGb": 28, "currentAvailableGb": 16.03,
  "projectedAvailableGb": 17.9, "shortfallGb": 10.1, "feasible": false,
  "policy": "hard", "projectionBasis": "hard-upper-bound", "snapshotAgeSeconds": 14.3,
  "wouldSuspend": [{ "pid": 8200, "name": "Weixin", "privateBytes": 458797056 }],
  "excluded": { "total": 278, "byReason": { "protected": 247, "category": 29, "self": 1, "inUse": 1 } }
}
```

**怎么读 `excluded`**：它把"L4 永不自动 / Unknown 永不自动"从 README 里的一句话变成数字。
`byReason.category` 包含 Unknown、系统、驱动、安全软件、反作弊、游戏、容器宿主——这些进程
在任何策略下都不会被处置。调用方据此判断"我的余量来自哪里、代价是什么"。

**注意**：soft 策略的 `projectedAvailableGb` 只报下界（清备用列表/压缩工作集的实际收益无法预测），
所以 `feasible: false` 不代表 soft 申请一定失败。要看"刚性可行性"请用 `policy: "hard"`。

错误：`401` · `400` · **`503` `{"error":"plan_unavailable"}`**（宿主没接入预演能力——
老实说"不支持"，而不是返回一个全 0 的假 plan）。

plan 不写 `Kind=Lease`，只写 `Kind=LeasePlan`（同一 requester/目标/策略 30 秒去重），
且**不占用**决策日志的重要记录配额。

### POST /api/lease

请求体：

| 字段 | 类型 | 说明 |
|---|---|---|
| `targetAvailableGb` | number | 任务需要的可用内存（GB） |
| `ttlSeconds` | int | 最长占用（30–86400，到期自动归还） |
| `requester` | string | 调用方标识（写入决策日志，便于追查） |
| `policy` | string | `soft`（默认）或 `hard` |
| `requesterPid` | int | 可选，**建议总是传**。调用方 PID——该进程及其子树永不进入处置候选（v0.23.0）。worker 常是"无窗口 + 空闲"，正是 hard 策略的挂起画像；不传就可能把自己挂起。0 = 未指定（旧行为） |
| `denyNames` | string[] | 可选。调用方点名不想被处置的进程名（**精确名、忽略大小写、自动去空格**）。典型用法：plan 告诉你"会挂起 Weixin"，你传 `["Weixin"]` 再 plan 一次，看换成了谁。它只**缩小**候选集，永远不会扩大 |

响应（200）：

| 字段 | 说明 |
|---|---|
| `id` | 租约 ID（归还时用） |
| `beforeAvailableGb` / `afterAvailableGb` | 动作前后实测可用 |
| `achievedAvailableGb` | **实测达成量**——低于目标时任务方应自行决定是否继续 |
| `shortfallGb` | 与目标的缺口（达标为 0） |
| `suspended` | hard 策略挂起的进程数组（含 `restored` 标记） |
| `policy` | 生效策略 |
| `relief` | 动作明细（清了什么、压缩了谁、排除了多少） |

错误：`401` 令牌错误 · `400` JSON 无效/字段非法 · `409` 已有活跃租约（**响应带 holder 与退避建议**）。

`409` 不是死胡同——它告诉等待方"谁占着、还剩多久、该等多久"：

```json
{
  "error": "lease_active",
  "holder": {
    "requester": "comfyui-job-42",
    "expiresUtc": "2026-11-26T12:34:56Z",
    "remainingSeconds": 92.4,
    "achievedAvailableGb": 18.5
  },
  "retryAfterSeconds": 10
}
```

`retryAfterSeconds` 取"剩余时间的 1/4"并封顶 10 秒——不是"还剩多久就等多久"，
因为持有方**随时可能提前归还**。排队本身不做在服务端：服务端长轮询会留下
"租约授予了但客户端已超时断开"的僵尸租约，重试放在客户端的话，进程一死就自然停止。
MCP 的 `mem_lease` 已经把这段逻辑包好了（见 `waitSeconds`）。

### GET /api/lease

当前租约状态（无活跃租约返回 `404`）。

### DELETE /api/lease/{id}

归还。hard 策略挂起的进程**全部自动恢复**（响应里逐个带 `restored` 标记）；
TTL 到期同样自动归还并恢复。

### POST /api/lease/{id}/renew

续期（v0.23.0）。请求体：`{"ttlSeconds": 900}`（30–86400，缺省 3600）。

**为什么要有这个端点**：TTL 到期会**立即恢复所有挂起进程**——正好发生在长任务吃满内存、
最需要那块内存的时候。任务跑得比预期久时，续期比"重新申请"安全：重新申请要等旧租约过期
（这段时间内存已经被还回去了），而续期是连续的。

响应（200）：更新后的租约，`expiresUtc` 从**续期时刻**重新计算，并带上
`renewCount`（续期次数）与 `remainingSeconds`（剩余秒数）。

错误：`410 {"error":"expired"}` —— 租约已经过期，挂起进程**已被恢复**，
只能重新申请（这是与 404 的关键区别：`404` 是 id 不对，`410` 是它存在过但已经死了）。

续期不会重新执行腾挪（`beforeAvailableGb`/`achievedAvailableGb` 保持不变），
也**不会重置守护告警**——否则调用方可以用 renew 把"内存已被侵蚀"的真实告警刷掉。

## 内置排除名单：挂起即断网的那一类

`denyNames` 之外，还有一份**总是生效**的默认名单（精确名、忽略大小写）：

| 类别 | 名字 |
|---|---|
| 代理 / 分流内核 | `FlClashCore` `FlClash` `clash` `clash-meta` `mihomo` `verge-mihomo` `sing-box` `v2ray` `xray` `hysteria` `trojan` `naive` |
| VPN / 远程访问 | `tailscaled` `wireguard` `wg` `openvpn` `zerotier-one` `netbird` `cloudflared` |
| 虚拟网卡 / 桥接 | `wintun` `tap-windows` |

**为什么必须默认排除**：这些进程无窗口、空闲、体积够大，完美满足硬租约的挂起条件。
但挂起代理或 VPN 内核会切断**所有**网络——包括调用方自己调 LLM API 的连接。
调用方看到的是"请求超时"，只会去重试，永远不会想到是内存工具干的。

**为什么它不进安全边界**（`TrimSelection`）：安全边界回答"谁绝对不能动"（系统/安全/Unknown），
这份名单回答"默认别动谁"。前者不可配置，后者是默认偏好。实测里 `FlClashCore` 有连接在传数据时
本来就不满足"够闲"，名单只在它真正空闲时兜底——这是保险，不是限制。

名单必然不全（新代理工具层出不穷）。漏了只是回到旧行为，不会更糟；
调用方发现漏网时用 `denyNames` 补。

## 自动预留：AI 任务开始就申请（v0.25.0，默认关）

设置 →「租约 API」→「为 AI 任务自动预留内存」勾选后，检测到 AI 会话开始就申请一个
**soft** 租约（目标 GB 可配，TTL 12 小时），会话结束归还。

| 约束 | 值 | 理由 |
|---|---|---|
| 策略 | **只有 soft** | hard 会挂起进程；"自动挂起"超出"只观察不处置"的设计边界 |
| TTL | 12 小时，不自动续期 | 实测最长 AI 会话 212 分钟；自动续期只多一条要维护的路径 |
| requester | `auto:ai-session` | 手动申请遇到它会**先行让位**（自动预留在 `LeaseApiService` 里被释放后重试），因为手动申请是明确意图、自动预留可再生 |
| 冲突 | 409 时跳过并记 `lease.auto_skipped` | 手动优先 |
| 检测延迟 | 约 2–2.5 分钟 | 5 个 30 秒样本里 ≥3 个；短脚本请用 `mempilot lease run` |

**已知取舍**：自动预留只能覆盖"长任务"，因为它依赖显存采样检测；3 分钟的脚本在检测到之前
就结束了。短任务的正解是让命令自己申请（CLI 形态，`mempilot lease run`）。

## 策略

| | soft（默认） | hard |
|---|---|---|
| 清低优先级备用列表 | ✓ | ✓ |
| 压缩"安全子集"工作集 | ✓ | ✓ |
| 挂起"足够闲"的进程（无窗口或空闲 ≥10 分钟，最多 8 个） | ✗ | 额度不够时 |
| 归还时恢复挂起进程 | — | ✓ 自动 |

两种策略都**绝不**碰：工作/游戏/前台/发声/受保护/未识别进程（同一套 `TrimSelection`
排除规则）。占用期可用内存跌破授予下限会触发"守护告警"（决策日志 + 托盘气球）。

## 行为与安全语义

- 申请动作（清缓存/压缩/挂起）发生在**占位成功之后**——并发请求不会产生"无人记账的挂起"
- **调用方自身及其进程树永不处置**（`requesterPid`，v0.23.0）：这是一条硬不变式，先于保护/分类判断。
  即使调用方的 worker 满足 hard 策略的"无窗口 + 空闲"条件，也不会被挂起——
  否则 agent 会把自己冻住然后一直等一个永远不会返回的任务
- 每次租约的开始/归还/守护告警/恢复都写决策日志（`Kind=Lease`）
- 失败的 relief 不占冷却；401/400/409 会写诊断（`Diag.Record`）供排查

## 示例

- [`tools/lease-example.py`](../tools/lease-example.py) — 最小接入（acquire 放在加载模型前，release 放 finally）
- [`tools/lease-load-test.py`](../tools/lease-load-test.py) — 整链路自检（达成量核对/归还恢复 100%/决策日志完整）

```python
import os

status, lease = post("/api/lease", {
    "targetAvailableGb": 12, "ttlSeconds": 3600,
    "requester": "comfyui-job-42", "policy": "soft",
    "requesterPid": os.getpid(),          # 自己和子进程永不处置
})
try:
    run_generation()
finally:
    delete(f"/api/lease/{lease['id']}")
```
