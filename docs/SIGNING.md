# 代码签名：现状与 SignPath 接入

## 为什么签名是分发的前提

产物目前**未签名**。对一个要求"读进程内存、挂起/结束进程"的工具，这会造成三层拦截：

| 拦截 | 表现 | 影响 |
|---|---|---|
| SmartScreen | "Windows 已保护你的电脑"（更多信息 → 仍要运行） | 首次安装流失约一半 |
| 360 安全卫士 | 拒绝未信任 exe 的托盘注册（win32 5），静默取消开机自启 | 中国用户装上了也不工作 |
| 企业环境 | 组策略禁止未签名 exe 运行 | 直接无法分发 |

仓库里已有一条签名步骤（`.github/workflows/release.yml` 的 `Sign artifacts (optional)`），
但它是**自签 pfx**（`/f sign.pfx`）——只对装过该证书的机器有效，**对其他人的机器没有任何作用**。
它保留作为本机开发验证用，不能解决分发问题。

## 目标：SignPath 免费开源签名

SignPath 对公开仓库的 OSS 项目提供免费代码签名（证书由 SignPath 持有，签名在他们的
托管环境执行，私钥不出他们的 HSM）。这是"不买商业证书"决策下唯一能拿到**他人机器也认**
的签名的路径。

### 第一步：申请（人工，约 3–10 个工作日）

1. 在 <https://signpath.io/> 注册，选择 **Open Source** 计划。
2. 提交项目：仓库地址 `https://github.com/ShuiQiongChuYunQiShi/MemPilot`、
   许可证 Apache-2.0、说明这是 Windows 桌面工具（需要 Authenticode 签名）。
3. 审核通过后，在控制台创建：
   - Organization（拿到 **organization ID**）
   - Project（slug 建议 `mempilot`）
   - Signing Policy（slug 建议 `release-signing`，触发条件 = 仅 release 分支/tag）
   - Artifact Configuration（slug 建议 `portable-exe`，描述"单个 .exe 文件"）
4. 控制台会给出该 Artifact Configuration 对应的 **GitHub Actions YAML 片段**。
   **以控制台给出的片段为准**——不同 policy 类型的输入名可能不同，照抄网上的示例容易失败。

### 第二步：配置仓库

| 类型 | 名称 | 值 |
|---|---|---|
| Secret | `SIGNPATH_API_TOKEN` | 控制台生成的 CI 用户 token（只读签名权限） |
| Variable | `SIGNPATH_ENABLED` | `true` |
| Variable | `SIGNPATH_ORGANIZATION_ID` | 第一步拿到的 org ID |

**不要**把 token 写进 workflow 或仓库变量；它必须只在 secret 里。

### 第三步：workflow 改动形态

现有顺序是 `publish → (可选自签) → SHA256SUMS → release`。接入 SignPath 后的顺序必须是
`publish → 上传未签名产物 → SignPath 签名 → 用签名后产物覆盖 dist → SHA256SUMS → release`，
**SHA256SUMS 必须在签名之后算**，否则哈希对不上。

关键点（不是完整 YAML，完整片段以控制台为准）：

- 签名前的产物要先 `actions/upload-artifact`（SignPath 从 artifact 取文件，不能直接读 workspace）。
- 签名是**异步**的：必须等完成（`wait-for-completion: true` 或轮询 API）再把结果下载回 `dist/`。
- 只签 `MemPilot-<version>-win-x64.exe`。`portable.zip` 里的 exe 也可以签，但 zip 本身
  **不能**签（Authenticode 只支持 PE 文件）；zip 的完整性靠 SHA256SUMS。
- 自签步骤保留但门控改成 `SIGN_ENABLED == 'true' && SIGNPATH_ENABLED != 'true'`，
  避免两套签名互相覆盖。

### 第四步：验证

```powershell
# 本机验证（需要 Windows SDK 的 signtool）
signtool verify /pa /v MemPilot-0.23.0-win-x64.exe

# 期望看到
#   Issued to: SignPath Foundation   （或证书主体名）
#   Successfully verified: MemPilot-0.23.0-win-x64.exe
```

用户侧验证：右键 exe → 属性 → 数字签名，能看到签名者名称；SmartScreen 不再弹"未知发布者"
（声誉仍需下载量积累，通常 3–5 天）。

## 与 winget 的先后关系

winget **不强制**要求签名，但：

- 未签名包的首次安装仍会撞 SmartScreen → winget 的"一条命令装好"体验被打断；
- `winget-pkgs` 审核对未签名包的 manifest 会要求更严格的 hash 与 URL 稳定性（我们已满足）。

所以顺序是：**先签名，再提 winget PR**。manifest 已经写好
（`manifests/s/ShuiQiongChuYunQiShi/MemPilot/0.22.0/`），签名接入后**下一个版本**直接复用，
只需改版本号与 hash。

## 如果 SignPath 审核不通过

退路（按优先级）：

1. 继续自签 + README 首屏给出 `SHA256SUMS` 校验命令与"为什么未签名"的说明。
2. 积累下载量与仓库信誉后再申请一次（SignPath 对活跃项目更宽松）。
3. **不购买商业证书**——这是 2026-10-31 的既定决策（¥1–3k/年，且不解决"用户不认识发布者"的问题）。
