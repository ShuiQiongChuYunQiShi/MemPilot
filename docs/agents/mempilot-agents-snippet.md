# MemPilot：内存紧张时的项目级约定

> 复制这段到任意项目的 `AGENTS.md`（或 Codex/ZCode 的规则文件）。十行，只讲"什么时候该先问一句"。

## 跑大内存任务前先 plan

如果 MemPilot 正在运行且已开启租约 API，并且本机配置了 `mempilot --mcp`：

- **大内存任务优先用 `mempilot lease run --gb N -- <command>` 把命令包起来**：
  申请与归还由进程边界保证，不必自己管生命周期（漏 release 会让挂起的进程一直等到 TTL）。
  加 `--dry-run` 先看能不能腾出来，`--wait 60` 在别人占用时排队。
- 需要手动协商时才用 `mem_plan` / `mem_lease`（模型加载、视频生成、大测试、批量渲染之前）。它不动作，
  只告诉你"能不能腾出来、会挂起谁、有多少被安全规则挡住"。
- 拿到租约后**按 `achievedAvailableGb` / `shortfallGb` 判断**，不要看到 HTTP 200 就认为够用。
- 任务可能跑超过 `ttlSeconds` 时**先 `renew`**：过期会立刻恢复所有挂起进程，
  正好在你最需要内存的时候把内存还回去。
- 结束时 `release`，失败路径也要释放。
- 冲突（409）用 `waitSeconds` 让工具自己重试，不要手动空转。
- 传 `requesterPid` = 你自己的任务进程 PID：它和它的子树永不被挂起。

详细规则见 MemPilot 仓库的 `skills/mempilot/SKILL.md`。
