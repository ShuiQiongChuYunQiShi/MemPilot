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
  "version": "0.13.0",
  "totalGb": 31.7, "availableGb": 16.3,
  "commitLimitGb": 60.9, "commitFreePct": 41.2,
  "leaseActive": false
}
```

### POST /api/lease

请求体：

| 字段 | 类型 | 说明 |
|---|---|---|
| `targetAvailableGb` | number | 任务需要的可用内存（GB） |
| `ttlSeconds` | int | 最长占用（30–86400，到期自动归还） |
| `requester` | string | 调用方标识（写入决策日志，便于追查） |
| `policy` | string | `soft`（默认）或 `hard` |

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

错误：`401` 令牌错误 · `400` JSON 无效/字段非法 · `409` 已有活跃租约。

### GET /api/lease

当前租约状态（无活跃租约返回 `404`）。

### DELETE /api/lease/{id}

归还。hard 策略挂起的进程**全部自动恢复**（响应里逐个带 `restored` 标记）；
TTL 到期同样自动归还并恢复。

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
- 每次租约的开始/归还/守护告警/恢复都写决策日志（`Kind=Lease`）
- 失败的 relief 不占冷却；401/400/409 会写诊断（`Diag.Record`）供排查

## 示例

- [`tools/lease-example.py`](../tools/lease-example.py) — 最小接入（acquire 放在加载模型前，release 放 finally）
- [`tools/lease-load-test.py`](../tools/lease-load-test.py) — 整链路自检（达成量核对/归还恢复 100%/决策日志完整）

```python
status, lease = post("/api/lease", {
    "targetAvailableGb": 12, "ttlSeconds": 3600,
    "requester": "comfyui-job-42", "policy": "soft",
})
try:
    run_generation()
finally:
    delete(f"/api/lease/{lease['id']}")
```
