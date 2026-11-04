# git 历史隐私审计报告

> 生成时间：自动生成 ｜ 扫描范围：`--all` 共 **83** 个 commit

> 目的：仓库转公开前确认历史中没有泄露本机路径、凭据或个人信息。
> 本报告由 `tools/history-audit.py` 生成，只读扫描，未修改任何历史。

## 结论速览

| 分类 | commit 数 |
|---|---|
| ✅ clean | 68 |
| ⚠️ 低风险（仅代理地址/账号名等） | 0 |
| 🔍 需复核（路径/凭据/个人信息） | 15 |

### ✅ 结论：可以公开

**未发现任何 blocker 级泄露。** 所有命中均属以下三类，已逐条判定：

1. **作者自己的机器路径**（`D:\work\MemPilot`、`D:\Program Files\MemPilot`）——公开后无信息价值，
   反而印证这是实机长期使用的工具；
2. **单元测试里的示例路径与假密钥**（`C:\Users\me`、`sk-test-1234567890`）——不是真实数据；
3. **文档里提到 `.pfx` / `lease-token.txt` 这些文件名**——从未提交过文件本体。

三个 critical 类（私钥块、真实令牌值、真实密钥赋值）**命中数为 0**。

## 按规则命中统计

| 规则 | 命中 commit 数 |
|---|---|
| 本机路径（high） | 11 |
| 用户目录（high） | 2 |
| 内网/本机 IP（medium） | 0 |
| 代理地址（low） | 1 |
| 私钥块（critical） | 0 |
| 持有令牌（critical） | 2 |
| 密钥字段赋值（high） | 1 |
| pfx 文件（high） | 3 |
| 个人姓名（high） | 0 |
| GitHub 账号（low） | 5 |

## 敏感数据文件从未进入历史

未发现 `config.json` / `crash.log` / `lease-token.txt` 等运行时数据被提交。

## 开发杂物曾进入历史

- `903b4e98  add_ctx.py`

## 逐 commit 明细

| commit | 日期 | 摘要 | 状态 | 命中规则数 |
|---|---|---|---|---|
| `32326d65` | 2026-11-04 | refactor: v0.9.2 — 安全判定收敛到 1 份 + ProcessService 拆分 + | clean | 0 |
| `85d2d79e` | 2026-11-04 | fix: v0.9.1 — 复盘 P1：非阻塞弹窗/日志合并写/枚举 fail-safe | clean | 0 |
| `ed678ad5` | 2026-11-04 | fix: v0.9.0 — 三轮复盘整改：修 8 个真 bug + 降噪 + 租约真实验证 | 需复核 | 1 |
| `424f3a19` | 2026-11-04 | feat: v0.8.0 — 租约 M2：硬租约（挂起闲置进程 + 归还自动恢复） | clean | 0 |
| `d2b3b7ae` | 2026-11-04 | feat: v0.7.0 — 租约 API M1（本地 HTTP + 令牌，单活跃 + TTL + 驻守 | 需复核 | 1 |
| `f65f2315` | 2026-11-04 | feat: v0.6.0 — 导航与页面重构（反馈降级/线程下线/分组导航/工具栏重做） | clean | 0 |
| `6617f320` | 2026-11-03 | fix: v0.5.1 — 提交兜底纠偏（L3 可选释放）+ 会话补提交峰值 | clean | 0 |
| `d0613dc1` | 2026-11-02 | feat: v0.5.0 — 提交内存兜底 + AI 会话观测（四天真实数据驱动） | clean | 0 |
| `48d8c887` | 2026-11-01 | feat: v0.4.0 — 用途名单外置 + 右键标记用途（最后一个 P2） | clean | 0 |
| `f190ca92` | 2026-11-01 | refactor: v0.3.9 — 采集链路接入统一诊断 + ResidentEngine 拆分（59 | clean | 0 |
| `30dc0f33` | 2026-11-01 | fix(test): 反馈时间断言不能写死时区（CI runner 是 UTC） | clean | 0 |
| `b8a2fff8` | 2026-11-01 | chore: v0.3.8 — 棱镜第三轮遗留整改（文档/notes/using/Dispose/联网加 | 需复核 | 3 |
| `a5bafa5f` | 2026-11-01 | fix: v0.3.7 — 反馈页重做（布局重叠/空提交/无说明/不可撤销） | clean | 0 |
| `946db20a` | 2026-11-01 | fix: v0.3.6 — 历史曲线打开后空白（点集 Clear+Add 不复位几何）+ 静默节流/静默 | clean | 0 |
| `4348e621` | 2026-11-01 | feat: v0.3.5 — 内存/显存曲线统一为占用率 + 0/50/100% 参考线 | clean | 0 |
| `3e8486e8` | 2026-11-01 | feat: v0.3.4 — GPU 显存观测：PDH 逐进程/适配器读取 + 30 秒采样落盘 + 显 | clean | 0 |
| `dc66e067` | 2026-10-31 | docs: 签名决策落定——不购商业证书，改为哈希校验+360 放行指引 | 需复核 | 1 |
| `cbad3543` | 2026-10-31 | refactor: v0.3.3 — god-class 拆分（MainWindow/MainViewM | 需复核 | 2 |
| `4e2f2e45` | 2026-10-31 | feat: v0.3.2 — 棱镜第二轮整改（CI 产物/锁文件/文档/列设置/用途筛选/影子结算/持久 | 需复核 | 3 |
| `12caae87` | 2026-10-31 | chore(wharf): kb 映射到归藏 mempilot（项目 id 与归藏登记名不同） | clean | 0 |
| `3d573d49` | 2026-10-31 | docs: v0.3.1 用途维度记录入 backlog | clean | 0 |
| `b4681ff9` | 2026-10-31 | feat: v0.3.1 — 用途维度（娱乐/工作/AI/系统/其他）接入进程识别 | clean | 0 |
| `755ad89e` | 2026-10-31 | docs: v0.3.0 程序清单记录入 backlog | clean | 0 |
| `0d481aa3` | 2026-10-31 | feat: v0.3.0 — 程序清单（注册表 Uninstall + 开始菜单 lnk + 安装根扫描 | 需复核 | 1 |
| `638adbfb` | 2026-10-31 | docs: v0.2.3 修复记录入 backlog | clean | 0 |
| `c6a333e2` | 2026-10-31 | chore: 忽略 .zcode/（工具生成的计划文件不入库） | clean | 0 |
| `31d4dc2b` | 2026-10-31 | fix: v0.2.3 — GPU 链路（门控/结构体/兜底死代码）+ Docker 识别归属 + 在线 | 需复核 | 1 |
| `31bd55d2` | 2026-10-31 | fix: v0.2.2 — 趋势图坐标系溢出 + DXGI COM 接口 vtable 错位 + GPU | clean | 0 |
| `6d939b4f` | 2026-10-31 | docs: 澄清计划编号与发布版本对应关系（v0.2.2 工作项随 v0.2.0 发布，v0.2.1 为 | clean | 0 |
| `654dcbb1` | 2026-10-31 | fix: v0.2.1 — DropShadowEffect.Color 被赋画刷资源导致启动崩溃 +  | clean | 0 |
| `e01ab80c` | 2026-10-31 | feat: v0.2.0 — UI redesign (direction B) + brand-spe | clean | 0 |
| `3d172cbb` | 2026-10-30 | feat: v0.1.8 — perf & UX pass, identification polish | clean | 0 |
| `de67a0bc` | 2026-10-29 | feat: v0.1.5 — batch cleanup UX + junk resident swee | clean | 0 |
| `459dc5dc` | 2026-10-29 | docs: v0.1.4 released — iteration backlog all code i | clean | 0 |
| `23f5805c` | 2026-10-29 | fix: publish-release.ps1 — PS 5.1 compatible Join-Pa | clean | 0 |
| `cc1faa4d` | 2026-10-29 | feat: v0.1.4 — rule library expansion + AI trio v1 ( | 需复核 | 2 |
| `712b2845` | 2026-10-29 | docs: v0.1.3 install references | 需复核 | 2 |
| `524a7d94` | 2026-10-29 | chore: v0.1.3 — MemPilot naming + data dir migration | clean | 0 |
| `204643eb` | 2026-10-29 | docs: HANDOFF path corrections | 需复核 | 1 |
| `14bed7ac` | 2026-10-29 | docs: HANDOFF updated — full MemPilot naming, wharf  | clean | 0 |
| `c0807f2f` | 2026-10-29 | refactor: complete rename MemoryTool -> MemPilot (na | 需复核 | 1 |
| `1678c9cc` | 2026-10-29 | docs: HANDOFF.md — new-session handoff prompt | 需复核 | 3 |
| `8ec5959a` | 2026-10-29 | chore: rename product to MemPilot — repo, assembly,  | clean | 0 |
| `fb5a9288` | 2026-10-29 | release: gate publishing behind workflow_dispatch —  | clean | 0 |
| `64672d89` | 2026-10-29 | test: classification asserts gated on real SystemCri | clean | 0 |
| `10952e04` | 2026-10-29 | test: fix literal newline in Capture | clean | 0 |
| `1516977e` | 2026-10-29 | test: capture integration failures into the diag art | clean | 0 |
| `69bf88ce` | 2026-10-29 | test: integration classification asserts gated on fu | clean | 0 |
| `26c21076` | 2026-10-29 | diag: TrustVerifier.VerifyDetailed with raw wintrust | clean | 0 |
| `c7b18fa2` | 2026-10-29 | diag: TrustVerifier.VerifyDetailed (raw wintrust sta | clean | 0 |
| `e1d4b715` | 2026-10-29 | ci: move diagnostics upload after the test step | clean | 0 |
| `cc77949a` | 2026-10-29 | test: Dump mirrors to enumeration-diag.txt (artifact | clean | 0 |
| `2c34497c` | 2026-10-29 | ci: test diagnostics to file artifact (console swall | clean | 0 |
| `8fb88b8e` | 2026-10-29 | test: fix last Dump(output) reference (HasSystemShap | clean | 0 |
| `8c70e9d4` | 2026-10-29 | test: fix leftover output reference in protected-ass | clean | 0 |
| `3000032f` | 2026-10-28 | test: fix xUnit1001 — constructor-inject ITestOutput | clean | 0 |
| `42943451` | 2026-10-28 | test: integration diagnostics — unconditional enumer | clean | 0 |
| `6a0909ac` | 2026-10-28 | perf+ux: process page overhaul — collection swap, to | clean | 0 |
| `db6b16bb` | 2026-10-28 | feat: K32 fix (private working sets), tree view, cat | clean | 0 |
| `0132603f` | 2026-10-28 | feat: K32 fix (private working sets), tree view, cat | clean | 0 |
| `01a63935` | 2026-10-28 | chore(wharf): 发布验证拆成两条链（日常看最新 tag / 发版后看 HEAD） | clean | 0 |
| `8d181ab2` | 2026-10-28 | chore: 反馈库已迁至 wharf 本体（D:/tool-yunwei/wharf-feedback | clean | 0 |
| `e8c2d270` | 2026-10-28 | chore: wharf release chain (verify-release) + publis | clean | 0 |
| `591458f3` | 2026-10-28 | ci: fix release asset upload — pwsh array splatting  | clean | 0 |
| `50e13315` | 2026-10-28 | ci: fix YAML — unquoted colon in step name broke rel | clean | 0 |
| `ba2d3b29` | 2026-10-28 | chore: version 0.1.0, GitHub CI + release workflows, | clean | 0 |
| `8de2a5d4` | 2026-10-28 | fix: second launch black-frozen window — signal hand | clean | 0 |
| `1ba0cd63` | 2026-10-28 | fix: tray icon blocked by 360 (win32 5 ACCESS_DENIED | clean | 0 |
| `e1461366` | 2026-10-28 | fix: tray icon never registered — ADD without icon d | clean | 0 |
| `194e03fa` | 2026-10-28 | feat: auto-promote tray icon to the visible tray are | clean | 0 |
| `e764e1ac` | 2026-10-28 | feat: tray icon ring-gauge redesign + curve area fil | clean | 0 |
| `0bb53057` | 2026-10-28 | feat: phase 3 — usage-habit recording, history dashb | clean | 0 |
| `b8046e79` | 2026-10-28 | feat: local trust engineering — self-signed code sig | clean | 0 |
| `71e38d70` | 2026-10-28 | feat: phase 2 polish — app identity, on-demand eleva | clean | 0 |
| `3a4c11a2` | 2026-10-28 | feat: .NET 10 LTS, Core/App split, lightweight tray  | clean | 0 |
| `19a8f5dd` | 2026-10-28 | fix: critical detector dt bug + complete theme syste | clean | 0 |
| `5fed7006` | 2026-10-28 | ui: redesign as task-oriented app — sidebar nav + re | clean | 0 |
| `0c8808a1` | 2026-10-28 | feat: decision log, tray percentage, relaunchable di | 需复核 | 1 |
| `a08ff588` | 2026-10-28 | fix: persist suspend registry, add auto-start/minimi | 需复核 | 2 |
| `d3681e95` | 2026-10-28 | fix: run graceful close / plan execution off the UI  | clean | 0 |
| `903b4e98` | 2026-10-28 | refactor: honest unused-detection, background refres | clean | 0 |
| `61914440` | 2026-10-28 | chore: baseline source before iteration | clean | 0 |
| `506a75b5` | 2026-10-28 | chore: init repo before refactor (baseline) | clean | 0 |

## 需复核项原文

下表逐条列出命中原文与判定。判定由 `tools/history-audit.py` 顶部的 `VERDICTS` 维护——扫描器只负责找，能不能公开是人定的。

| 规则 | 判定 | 理由 |
|---|---|---|
| GitHub 账号 | **intentional** | 账号名本来就是公开的（仓库所有者） |
| pfx 文件 | **safe-doc** | 只在文档/CI 脚本里出现 .pfx 这个扩展名，从未提交 pfx 本体 |
| 代理地址 | **safe-own-machine** | 127.0.0.1 本地代理，无信息价值 |
| 密钥字段赋值 | **safe-test-fixture** | 全部是单元测试里的假密钥（sk-test-1234567890 / sk-secret-abc） |
| 持有令牌 | **safe-file-mention** | 只出现文件名 lease-token.txt，未出现任何真实令牌值 |
| 本机路径 | **safe-own-machine** | 只暴露作者自己的盘符与安装目录（D:\work、D:\Program Files\MemPilot），不含他人信息或凭据 |
| 用户目录 | **safe-test-fixture** | 单元测试用的示例路径（C:\Users\me、C:\Users\Administrator 作为第三方软件安装位置样例） |

> ✅ 无 blocker 级泄露。全部命中为「本机路径 / 测试假数据 / 文档提及」，不含真实凭据或他人信息。

### `ed678ad5` 2026-11-04 — fix: v0.9.0 — 三轮复盘整改：修 8 个真 bug + 降噪 + 租约真实验证

**持有令牌**（safe-file-mention）
- `+    path = os.path.join(DATA_DIR, "lease-token.txt")`

### `d2b3b7ae` 2026-11-04 — feat: v0.7.0 — 租约 API M1（本地 HTTP + 令牌，单活跃 + TTL + 驻守

**持有令牌**（safe-file-mention）
- `+    // 只绑 127.0.0.1；令牌落盘 lease-token.txt，设置页可见/可重新生成。`
- `+    public static string PathFor(string dataDir) => Path.Combine(dataDir, "lease-token.txt");`
- `+/// <summary>租约 API 令牌的存取（明文落盘 lease-token.txt；仅本机可用，权限即用户目录）。</summary>`

### `b8a2fff8` 2026-11-01 — chore: v0.3.8 — 棱镜第三轮遗留整改（文档/notes/using/Dispose/联网加

**本机路径**（safe-own-machine）
- `+- 本地安装副本：`D:\Program Files\MemPilot\` 下**最新版本号的 exe**（桌面快捷方式与 HKCU Run 键都指向它；旧版留 `.bak`）`
- `-- 本地安装副本：`D:\Program Files\MemPilot\MemPilot-0.3.1-win-x64.exe`（桌面快捷方式 MemPilot.lnk 指向这里）`

**密钥字段赋值**（safe-test-fixture）
- `+        const string secret = "sk-test-1234567890";`
- `+        store.Config.OnlineLookupApiKey = "sk-secret-abc";`

**GitHub 账号**（intentional）
- `+- GitHub：`github.com/ShuiQiongChuYunQiShi/MemPilot`（**私有**）。主线版本以 `git tag` / `MemPilot.App.csproj` 为准，安装版本看上面目录里的最新 exe —— 本文不再写死版本号`
- `-- GitHub：`github.com/ShuiQiongChuYunQiShi/MemPilot`（**私有**）；**安装版 v0.3.1**，仓库主线已到 v0.3.3（未发布）。版本以 `git tag` 与 `MemPilot.App.csproj` 为准`

### `dc66e067` 2026-10-31 — docs: 签名决策落定——不购商业证书，改为哈希校验+360 放行指引

**pfx 文件**（safe-doc）
- `-1. 把自签证书导出为 pfx，转 base64：`[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`；`

### `cbad3543` 2026-10-31 — refactor: v0.3.3 — god-class 拆分（MainWindow/MainViewM

**pfx 文件**（safe-doc）
- `+1. 把自签证书导出为 pfx，转 base64：`[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx"))`；`

**GitHub 账号**（intentional）
- `+- GitHub：`github.com/ShuiQiongChuYunQiShi/MemPilot`（**私有**）；**安装版 v0.3.1**，仓库主线已到 v0.3.3（未发布）。版本以 `git tag` 与 `MemPilot.App.csproj` 为准`
- `-- GitHub：`github.com/ShuiQiongChuYunQiShi/MemPilot`（**私有**），版本 **v0.3.1 已发布并装回本机运行**`

### `4e2f2e45` 2026-10-31 — feat: v0.3.2 — 棱镜第二轮整改（CI 产物/锁文件/文档/列设置/用途筛选/影子结算/持久

**本机路径**（safe-own-machine）
- `+- 开发仓库：`D:\work\MemPilot`（从 D:\MemoryTool 迁来）`
- `+- 本地安装副本：`D:\Program Files\MemPilot\MemPilot-0.3.1-win-x64.exe`（桌面快捷方式 MemPilot.lnk 指向这里）`
- `-- 开发仓库：`D:\work\MemPilot`（从 D:\MemoryTool 迁来；旧目录残留空壳被 wharf 代理会话锁定，关会话后手动删）`

**pfx 文件**（safe-doc）
- `+          $pfx = Join-Path $env:RUNNER_TEMP 'sign.pfx'`

**GitHub 账号**（intentional）
- `+- GitHub：`github.com/ShuiQiongChuYunQiShi/MemPilot`（**私有**），版本 **v0.3.1 已发布并装回本机运行**`
- `-- GitHub：`github.com/ShuiQiongChuYunQiShi/MemPilot`（**私有**），版本 **v0.1.3 已发布并装回本机运行**，49 个测试全绿`

### `0d481aa3` 2026-10-31 — feat: v0.3.0 — 程序清单（注册表 Uninstall + 开始菜单 lnk + 安装根扫描

**本机路径**（safe-own-machine）
- `+            ["InstallLocation"] = @"""C:\Program Files\Example""",`
- `+            ["UninstallString"] = @"""C:\Program Files\Tool\uninst.exe"" /S",`
- `+        Assert.Equal("Docker Engine", catalog.FindByPath(@"C:\Program Files\Docker\Docker\dockerd.exe")!.DisplayName); // 精确路径优先`

### `31d4dc2b` 2026-10-31 — fix: v0.2.3 — GPU 链路（门控/结构体/兜底死代码）+ Docker 识别归属 + 在线

**本机路径**（safe-own-machine）
- `+        var p = Make("devenv", @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe");`
- `+    [InlineData("Docker Desktop", @"C:\Program Files\Docker\Docker\frontend\Docker Desktop.exe")]`
- `+    [InlineData("cagent", @"C:\Program Files\Docker\Docker\resources\bin\cagent.exe")]`

### `cc1faa4d` 2026-10-29 — feat: v0.1.4 — rule library expansion + AI trio v1 (

**本机路径**（safe-own-machine）
- `+        var p = Make("SomeAsusHelper", @"C:\Program Files (x86)\ASUS\ArmouryDevice\somehelper.exe",`
- `+        var p = Make("wetype_server", @"C:\Program Files\Tencent\WeType\3.0.0.38\wetype_server.exe",`
- `+    [InlineData("AlibabaProtect", @"C:\Program Files (x86)\AlibabaProtect\1.0.70.3194\AlibabaProtect.exe")]`

**用户目录**（safe-test-fixture）
- `+        var p = Make("wps", @"C:\Users\me\AppData\Local\Kingsoft\WPS Office\12.1.0.29161\office6\wps.exe",`
- `+    [InlineData("WeChatAppEx", @"C:\Users\Administrator\AppData\Roaming\Tencent\xwechat\xplugin\plugins\RadiumWMPF\WeChatAppEx.exe")]`
- `+    [InlineData("wpscenter", @"C:\Users\me\AppData\Local\Kingsoft\WPS Office\12.1.0.29161\office6\wpscenter.exe")]`

### `712b2845` 2026-10-29 — docs: v0.1.3 install references

**本机路径**（safe-own-machine）
- `+- 本地安装副本：`D:\Program Files\MemPilot\MemPilot-0.1.3-win-x64.exe`（桌面快捷方式 MemPilot.lnk 指向这里）`
- `-- 本地安装副本：`D:\Program Files\MemPilot\MemPilot-0.1.2-win-x64.exe`（桌面快捷方式 MemPilot.lnk 指向这里）`

**GitHub 账号**（intentional）
- `+- GitHub：`github.com/ShuiQiongChuYunQiShi/MemPilot`（**私有**），版本 **v0.1.3 已发布并装回本机运行**，49 个测试全绿`
- `-- GitHub：`github.com/ShuiQiongChuYunQiShi/MemPilot`（**私有**），版本 **v0.1.2 已发布并装回本机运行**，49 个测试全绿`

### `204643eb` 2026-10-29 — docs: HANDOFF path corrections

**本机路径**（safe-own-machine）
- `+- 开发仓库：`D:\work\MemPilot`（从 D:\MemoryTool 迁来；旧目录残留空壳被 wharf 代理会话锁定，关会话后手动删）`
- `-- 开发仓库：`D:\work\MemPilot`（从 D:\MemPilot 迁来；旧目录残留空壳被 wharf 代理会话锁定，关会话后手动删）`

### `c0807f2f` 2026-10-29 — refactor: complete rename MemoryTool -> MemPilot (na

**本机路径**（safe-own-machine）
- `+- 开发仓库：`D:\work\MemPilot`（从 D:\MemPilot 迁来；旧目录残留空壳被 wharf 代理会话锁定，关会话后手动删）`
- `-- 开发仓库：`D:\work\MemPilot`（从 D:\MemoryTool 迁来；旧目录残留空壳被 wharf 代理会话锁定，关会话后手动删）`

### `1678c9cc` 2026-10-29 — docs: HANDOFF.md — new-session handoff prompt

**本机路径**（safe-own-machine）
- `+- 开发仓库：`D:\work\MemPilot`（从 D:\MemoryTool 迁来；旧目录残留空壳被 wharf 代理会话锁定，关会话后手动删）`
- `+- 本地安装副本：`D:\Program Files\MemPilot\MemPilot-0.1.2-win-x64.exe`（桌面快捷方式 MemPilot.lnk 指向这里）`

**代理地址**（safe-own-machine）
- `+3. **代理（FlClash 127.0.0.1:7890）时开时关**：git push 失败时用「临时 unset .gitconfig 的 github 代理 → push → 恢复」；gh 资产下载必须 `HTTPS_PROXY=http://127.0.0.1:7890 gh release downlo`

**GitHub 账号**（intentional）
- `+- GitHub：`github.com/ShuiQiongChuYunQiShi/MemPilot`（**私有**），版本 **v0.1.2 已发布并装回本机运行**，49 个测试全绿`

### `0c8808a1` 2026-10-28 — feat: decision log, tray percentage, relaunchable di

**本机路径**（safe-own-machine）
- `+                    "path"        "C:\\Program Files (x86)\\Steam"`
- `+        Assert.Contains("C:\\Program Files (x86)\\Steam\\", roots);`

### `a08ff588` 2026-10-28 — fix: persist suspend registry, add auto-start/minimi

**本机路径**（safe-own-machine）
- `+        var p = Make("BEService", @"C:\Program Files (x86)\BattlEye\BEService.exe");`
- `+        var p = Make("EpicGamesLauncher", @"C:\Program Files\Epic Games\Launcher.exe");`
- `+    [InlineData("Teams", @"C:\Program Files\Microsoft\Teams\teams.exe")]`

**用户目录**（safe-test-fixture）
- `+        var p = Make("defenderclone", @"C:\Users\me\AppData\Local\defenderclone.exe");`
