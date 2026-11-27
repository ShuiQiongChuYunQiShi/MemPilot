# MemPilot 中文说明

> [English README](../README.md) ｜ 中文版（当前，随 v0.22.0 更新）
>
> 界面为**中英双语**（设置 → 界面语言，运行时切换，无需重启）。

MemPilot 是 Windows 常驻内存与进程领航员，面向「并行开发 + 游戏 + AI 生图/生视频」混合场景：
识别每个进程的真实身份，判定谁真正未使用，**按进程**腾内存/回收内存——让 ComfyUI 拿到它需要的
内存，而不把你的 IDE、浏览器、游戏当代价。

核心立场：**清缓存和降提交是两件事，我们两件都做，但界面分开、风险分级**。
standby list 本来就是"可用"内存，游戏纹理和文件缓存驻留其中——全量 purge 只会让游戏重新读盘，
**而且不降提交**。因此清缓存（清备用列表 / 刷文件缓存 / 压缩工作集）在「清缓存与工作集」页
显式提供：需管理员权限、逐项勾选、结果如实标注"仅物理占用，不降提交"。

## 为什么不是又一个内存清理器

11 天 1,839 个采样实测：**这台机器真正紧张的指标是提交（commit），不是物理内存**——
提交峰值 73.3 GB vs 物理仅 31.7 GB，11.2% 的时间提交余量 <10%。清 standby list 不降提交，
这就是"清了 3 GB 却照样 OOM"的原因。所以 MemPilot 把提交当一等公民：
提交告急时唯一真正有效的动作（优雅关闭闲置后台）由你逐次确认；其余动作只针对物理内存，
并且**在结果里明确写出"不降提交"**——不拿"释放 2 GB"冒充"下次不会 OOM"。

## 下载

| 渠道 | 文件 | 体积 | 需要 |
|---|---|---|---|
| **Portable（推荐）** | `MemPilot-<ver>-portable.zip` | **zip 约 500 KB（解压约 440 KB）** | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) |
| 自包含 | `MemPilot-<ver>-win-x64.exe` | 约 62 MB | 无——运行时已打包 |

发布均带 `SHA256SUMS.txt`，安装前校验：

```powershell
(Get-FileHash .\MemPilot-<ver>-win-x64.exe -Algorithm SHA256).Hash.ToLower()
Get-Content .\SHA256SUMS.txt
# 两个值一致 = 未被改动
```

产物**未签名**（不购商业证书的决策见英文 README），首次运行被 360/SmartScreen 提示属预期；
信任模型见 [SECURITY.md](../SECURITY.md)，数据说明见 [PRIVACY.md](PRIVACY.md)。

## 60 秒试一下

租约 API 是同类工具都没有的那一块：任务在加载模型前申请内存，拿回的是**实测腾出量**，
结束后归还。

```powershell
# 1. 安装（二选一）
#    a) winget —— 清单已在本仓库，提交审核中，下个版本起可用
winget install ShuiQiongChuYunQiShi.MemPilot
#    b) 现在：从 Releases 下载 MemPilot-<ver>-win-x64.exe（无需运行时）

# 2. 一次性开启：设置 →「租约 API」（默认端口 52717）
#    令牌落在 %APPDATA%\MemPilot\lease-token.txt

# 3. 跑自检脚本（自动读令牌与端口）
python tools/lease-demo.py --gb 4 --policy hard --stress
```

输出形状（每一行都是本项目必须守住的承诺）：

```
租约 c31c54f71963（hard）· 实测 14.98 GB / 目标 16.0 GB · 缺口 1.02 GB
  ✓ 调用方自身（pid=34152）不在处置范围 · 挂起 3 个
  ✓ 归还状态 released · 恢复 3/3 个挂起进程
```

- `实测 … 缺口 …`——报的是真腾出多少，不是你要多少。
- `调用方自身…不在处置范围`——调用方及其进程树永不被挂起（`requesterPid`），
  agent 不会把自己的 worker 冻住。
- `恢复 3/3`——挂起的进程逐个恢复，数字来自响应而不是要你相信的日志。

## 让 agent 直接用（MCP）

MemPilot 内置一个极简 MCP server（stdio 传输），agent 不写任何 HTTP 就能查内存、申请租约。
在你的 MCP 客户端里加：

```json
{
  "mcpServers": {
    "mempilot": {
      "command": "C:\\Program Files\\MemPilot\\MemPilot.exe",
      "args": ["--mcp"]
    }
  }
}
```

路径换成你自己的 `MemPilot.exe` 位置。winget portable 安装会把 `mempilot` 放进 PATH，
那里写 `"command": "mempilot"` 也可以。

只有三个 tool——每多一个，模型选错的概率就多一分：

| Tool | 作用 |
|---|---|
| `mem_status` | 内存状态、`capabilities`（`lease` / `lease.plan`）、`limits` |
| `mem_plan` | **零动作**预演：实时可用、上界预估、**将会**被挂起的进程、以及被安全边界挡掉多少（附原因） |
| `mem_lease` | acquire（返回**实测**腾出量）/ release |

这个 server 只当**客户端**：它把请求转发给 127.0.0.1 的租约 API。租约账本必须留在
唯一那个长驻进程里——第二个进程自带账本，就是"进程被挂起、没人负责恢复"的老路。
MemPilot 没在跑或租约 API 没开时，tool 会返回一条说清"该去哪里打开"的错误。

`mem_lease` 还支持 `waitSeconds`（0–120）：若已有活跃租约，按服务端建议的退避
（`retryAfterSeconds`，封顶 10 秒）重试，直到空出或等待超时，两种情况都会报出当前持有者。
重试放在 MCP 客户端而不是服务端——服务端长轮询会留下"授予了但客户端已断开"的僵尸租约。

`mem_plan` 不做任何动作：读上一次进程快照、返回预估。`projectionBasis` 说明这个数字怎么来的——
`hard-upper-bound`（当前 + 将会挂起进程的私有提交）或 `current-only`（下界，soft 的回收量不可预测）。

可选的 skill 文件——让 agent 知道**什么时候该用**，而不只是"有这个东西"：

```powershell
# Claude Code：安装 skill
Copy-Item -Recurse skills\mempilot $env:USERPROFILE\.claude\skills\mempilot
# Codex / ZCode：把片段粘进项目的 AGENTS.md
Get-Content docs\agents\mempilot-agents-snippet.md
```

### 或者直接把命令包起来

如果任务就是一条命令行（agent 起任务时最常见的样子），把它包起来：

```powershell
mempilot lease run --gb 16 -- python gen.py
```

它会 plan → 申请 → 跑子进程 → 子进程退出时归还（失败与 Ctrl+C 同样归还）。
`--policy hard` 同时挂起闲置后台，`--dry-run` 只打印计划不申请，`--wait 60` 在别人占用时排队。

（winget 安装后 `mempilot` 在 PATH 里；在那之前直接用 `MemPilot.exe`。）

## 核心能力

### 1. 先分类，再动手
精确名规则 + 路径片段 + **WinVerifyTrust 签名验证后的发布者规则**（自签证书冒充
"Microsoft" 骗不过）+ Steam 库解析 + 程序清单证据。未识别（Unknown）**永不自动处置**。

### 2. 诚实的"未使用"判定
最小存活期（默认 120 秒）+ 连续 N 轮空闲观察 + 无窗口 + 低 CPU/IO + **无音频活动**
（WASAPI 峰值检测——后台放歌的进程不算未使用）。空闲状态按 `(pid, startTime)` 累计，
PID 复用不会污染。

### 3. 动作阶梯，永不跳级
L1 回收工作集 → L2 挂起（原子、可恢复）→ L3 优雅关闭（需确认）→ L4 强制结束（**永不自动**，
人工双确认）。自动路径只到 L1/L2。

### 4. 决策可解释、可导出
每个自动决定——切了什么模式、执行/跳过了谁、**哪条守卫拦下的**——写入 `decisions.json`
（5,000 条，按 Kind 分桶保护重要记录）。没有管理员权限时日志写「跳过：需要管理员」，
而不是假装成功。决策日志页支持**按 Kind 筛选**与 **CSV/JSON 导出**；v0.22 起原因带稳定
code，界面与导出按当前语言渲染（旧记录回退原文）。

### 5. 租约 API——像申请锁一样申请内存
AI 任务开跑前申请、结束后归还；返回**实测达成量**而非承诺量。详见
[LEASE-API.md](LEASE-API.md)。默认关闭，设置页打开。

### 6. 垃圾进程建议（v0.15.0）
每天最多一次：找出**整天无窗口/音频/CPU 活动**的垃圾进程，在主页给一张卡——
挂起 2 小时 / 挂起 8 小时（到期自动恢复）/ 结束（走确认流程）/ 忽略。
你的选择会被学习：反复忽略同一个进程，下次提示间隔从 7 天升到 30 天再到 90 天；
**永不自动加白名单**。通知行为见 [NOTIFICATION-POLICY.md](NOTIFICATION-POLICY.md)。

### 7. 长驻未用建议（v0.21.0，默认关闭）
跑满 8 小时、今天零活动、私有提交 ≥512MB 的非垃圾进程（如厂商服务）——**只建议挂起**，
不提供结束。与垃圾建议共用学习冷却，每天最多一张卡。

### 8. WSL 常驻提示（v0.22.0，默认开启）
`vmmemWSL` 占用 ≥2GB 且今天没有 WSL 活动时，主页提示可 `wsl --shutdown`
（只提供复制命令，**绝不自动执行**）。`vmmem` 一族永久排除在压缩/挂起/make-room 之外。

### 9. 60 秒 commit 对比（v0.22.0）
主页「60 秒对比」：回收安全子集的闲置工作集，并排显示**可用物理**与**提交余量**的前后变化——
让你亲眼看到「物理降了，提交没动」，以及为什么降提交只能靠结束闲置进程。

### 10. 显存观测
逐进程专用/共享显存（PDH）+ 30 秒采样 + AI 会话识别（N-of-M 滞回，单点回落不劈断会话）。
谁吃了 15 GB 显存、吃了多久，事后可查。

### 11. 提交兜底（v0.13 起三级）
提交告急时：清备用列表 → （失败或不达标）**立即**安全子集压缩 → 低可用兜底
（可用 <3GB 或提交余量 <10% 直接触发，10 分钟去重）→ 可选 L3（非模态确认卡）。

## 安全模型

| 防线 | 行为 |
|---|---|
| 分类保护 | 系统关键/安全软件/反作弊/驱动/服务/启动项 永不候选 |
| Unknown 豁免 | 未识别进程永不自动处置 |
| 前台/音频豁免 | 前台、全屏、正在发声的进程永不触碰 |
| PID 复用复检 | 每次状态变更前按 `(pid, startTime)` 复核 |
| L4 双确认 | 强制结束只能人工、两次确认 |
| 诚实日志 | 权限缺失记「跳过」，失败记「失败 + 原因」 |

## 构建

```bash
git clone https://github.com/ShuiQiongChuYunQiShi/MemPilot.git
cd MemPilot
dotnet build src/MemPilot.App/MemPilot.App.csproj -c Release
dotnet test  tests/MemPilot.Tests/MemPilot.Tests.csproj
```

无 `.sln`（有意为之），命令显式指定 csproj。需要 .NET 10 SDK + Windows 10/11。

## 已知边界

- 界面为中文（英文 README 已就绪，界面字符串抽取进行中，欢迎贡献）
- 提权进程的内存/命令行读取受限（已标记，不影响分类）
- 混合显卡笔记本：适配器用量按 LUID 匹配，匹配不上显示"不可用"而非猜测

## 许可

[Apache-2.0](../LICENSE)。第三方组件见 [NOTICE](../NOTICE)。
